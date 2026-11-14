#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <aclapi.h>
#include <sddl.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>

/* Runs inside the permanently restricted command, never inside the desktop host.
 * Six private creation descriptors and two bounded self-descriptor updates
 * are adapted. Every actual syscall retains the original token. */
typedef NTSTATUS (NTAPI *QUERY_OBJECT)(HANDLE, ULONG, PVOID, ULONG, PULONG);
typedef union { ULONG_PTR alignment; BYTE bytes[4096]; } TOKEN_BUFFER;
typedef struct {
    OBJECT_ATTRIBUTES attributes;
    UNICODE_STRING objectName;
    HANDLE rootCopy;
    WCHAR name[221];
    WCHAR rootName[2048];
    union { ULONG_PTR alignment; BYTE bytes[1024]; } descriptor;
    ULONG stage;
} PRIVATE_REQUEST;
static volatile LONG attempts;
static volatile LONG descriptorAttempts;
static BOOL decimal_id(const WCHAR *text,size_t length) {
    if(length<1||length>10)return FALSE;
    ULONGLONG value=0;
    for(size_t i=0;i<length;i++){if(text[i]<L'0'||text[i]>L'9')return FALSE;value=value*10+(text[i]-L'0');}
    return value>0&&value<=UINT32_MAX;
}

#pragma warning(push)
#pragma warning(disable:4191) /* Exact SDK-compatible native prototypes are checked at every call site. */
#define NATIVE(type,name) ((type)GetProcAddress(GetModuleHandleW(L"ntdll.dll"),name))

static BOOL same_text(const WCHAR *a, USHORT bytes, const WCHAR *b) {
    size_t length=wcslen(b);
    return bytes==length*sizeof(WCHAR) && CompareStringOrdinal(a,bytes/2,b,(int)length,TRUE)==CSTR_EQUAL;
}

static BOOL object_text(QUERY_OBJECT query,HANDLE handle,ULONG information,WCHAR *text,size_t capacity) {
    union { ULONG_PTR alignment; BYTE bytes[8192]; } data;
    ULONG needed=0;
    NTSTATUS status=query(handle,information,data.bytes,sizeof(data.bytes),&needed);
    if(status<0)return FALSE;
    UNICODE_STRING *name=(UNICODE_STRING*)data.bytes;
    uintptr_t start=(uintptr_t)data.bytes,p=(uintptr_t)name->Buffer;
    if((name->Length&1)||name->Length/2>=capacity||p<start||p>start+sizeof(data.bytes)||name->Length>start+sizeof(data.bytes)-p)return FALSE;
    memcpy(text,name->Buffer,name->Length);text[name->Length/2]=0;return TRUE;
}

static BOOL current_context(HANDLE token,TOKEN_BUFFER *user,TOKEN_BUFFER *app,TOKEN_BUFFER *restricted) {
    DWORD bytes=0,container=0;
    if(!GetTokenInformation(token,TokenIsAppContainer,&container,sizeof(container),&bytes)||container!=1||!IsTokenRestricted(token))return FALSE;
    if(!GetTokenInformation(token,TokenUser,user->bytes,sizeof(user->bytes),&bytes))return FALSE;
    if(!GetTokenInformation(token,TokenAppContainerSid,app->bytes,sizeof(app->bytes),&bytes))return FALSE;
    if(!GetTokenInformation(token,TokenRestrictedSids,restricted->bytes,sizeof(restricted->bytes),&bytes))return FALSE;
    TOKEN_GROUPS *groups=(TOKEN_GROUPS*)restricted->bytes;
    return groups->GroupCount==1&&IsValidSid(((TOKEN_USER*)user->bytes)->User.Sid)&&
        IsValidSid(((TOKEN_APPCONTAINER_INFORMATION*)app->bytes)->TokenAppContainer)&&IsValidSid(groups->Groups[0].Sid);
}

static int private_request_policy(POBJECT_ATTRIBUTES supplied,PRIVATE_REQUEST *request,BOOL pidLink,BOOL charge) {
    HANDLE token=GetCurrentProcessToken(),effective=GetCurrentThreadEffectiveToken();int accepted=0;
    request->rootCopy=NULL;
    request->name[0]=0;request->rootName[0]=0;
    request->stage=1;
    if(!supplied)return 0;
    __try {
        if(supplied->Length!=sizeof(OBJECT_ATTRIBUTES)||!supplied->RootDirectory||!supplied->ObjectName||!supplied->ObjectName->Buffer)return FALSE;
        UNICODE_STRING name=*supplied->ObjectName;
        if(!name.Length||(name.Length&1)||name.MaximumLength<name.Length||name.Length>220*sizeof(WCHAR))return FALSE;
        for(USHORT i=0;i<name.Length/2;i++) {
            WCHAR c=name.Buffer[i];if(c<0x20||c>0x7e||c==L'\\'||c==L'/')return FALSE;
        }
        memcpy(request->name,name.Buffer,name.Length);request->name[name.Length/2]=0;
        if(pidLink&&(name.Length/2<8||wcsncmp(request->name,L"winpid.",7)!=0||!decimal_id(request->name+7,name.Length/2-7)))return FALSE;
        request->attributes=*supplied;
        request->objectName=name;request->objectName.Buffer=request->name;
        request->objectName.MaximumLength=(USHORT)(name.Length+sizeof(WCHAR));
        request->attributes.ObjectName=&request->objectName;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
    QUERY_OBJECT query=NATIVE(QUERY_OBJECT,"NtQueryObject");
    WCHAR type[64];
    if(!DuplicateHandle(GetCurrentProcess(),request->attributes.RootDirectory,GetCurrentProcess(),&request->rootCopy,0,FALSE,DUPLICATE_SAME_ACCESS))return 0;
    request->attributes.RootDirectory=request->rootCopy;
    /* Type first: never issue a possibly blocking file/pipe name query. */
    if(!query||!object_text(query,request->rootCopy,2,type,64)||wcscmp(type,L"Directory")!=0||
        !object_text(query,request->rootCopy,1,request->rootName,2048))goto done;
    TOKEN_BUFFER user,app,restricted;
    request->stage=2;
    request->stage=3;
    if(!current_context(token,&user,&app,&restricted))goto done;
    request->stage=4;
    {
        TOKEN_BUFFER eu,ea,er;
        if(!current_context(effective,&eu,&ea,&er)||
            !EqualSid(((TOKEN_USER*)user.bytes)->User.Sid,((TOKEN_USER*)eu.bytes)->User.Sid)||
            !EqualSid(((TOKEN_APPCONTAINER_INFORMATION*)app.bytes)->TokenAppContainer,((TOKEN_APPCONTAINER_INFORMATION*)ea.bytes)->TokenAppContainer)||
            !EqualSid(((TOKEN_GROUPS*)restricted.bytes)->Groups[0].Sid,((TOKEN_GROUPS*)er.bytes)->Groups[0].Sid))goto done;
    }
    request->stage=5;
    WCHAR relative[2048],expected[2048];DWORD needed=0,session=0;
    if(!GetAppContainerNamedObjectPath(token,NULL,2048,relative,&needed))goto done;
    if(relative[0]==L'\\') {
        if(wcscpy_s(expected,2048,relative))goto done;
    } else {
        if(!ProcessIdToSessionId(GetCurrentProcessId(),&session)||_snwprintf_s(expected,2048,_TRUNCATE,L"\\Sessions\\%lu\\%s",session,relative)<0)goto done;
    }
    size_t prefix=wcslen(expected),length=wcslen(request->rootName);
    request->stage=6;
    if(length<prefix||!same_text(request->rootName,(USHORT)(prefix*2),expected))goto done;
    if(length!=prefix) {
        const WCHAR *suffix=request->rootName+prefix;
        if(wcsncmp(suffix,L"\\msys-",6)!=0)goto done;
        for(size_t i=1;suffix[i];i++)if(!((suffix[i]>=L'a'&&suffix[i]<=L'z')||(suffix[i]>=L'A'&&suffix[i]<=L'Z')||(suffix[i]>=L'0'&&suffix[i]<=L'9')||suffix[i]==L'-'||suffix[i]==L'_'||suffix[i]==L'.'))goto done;
    }
    if(charge&&InterlockedIncrement(&attempts)>64) { accepted=2;goto done; }
    request->stage=7;
    SECURITY_DESCRIPTOR descriptor;__declspec(align(8)) BYTE aclBytes[512],labelBytes[128];
    PACL acl=(PACL)aclBytes,label=(PACL)labelBytes;
    SID_IDENTIFIER_AUTHORITY authority=SECURITY_MANDATORY_LABEL_AUTHORITY;
    __declspec(align(8)) BYTE lowBytes[SECURITY_MAX_SID_SIZE];PSID low=(PSID)lowBytes;
    if(!InitializeSid(low,&authority,1))goto done;
    *GetSidSubAuthority(low,0)=SECURITY_MANDATORY_LOW_RID;
    if(!InitializeAcl(acl,sizeof(aclBytes),ACL_REVISION)||
        !AddAccessAllowedAce(acl,ACL_REVISION,GENERIC_ALL,((TOKEN_USER*)user.bytes)->User.Sid)||
        !AddAccessAllowedAce(acl,ACL_REVISION,GENERIC_ALL,((TOKEN_APPCONTAINER_INFORMATION*)app.bytes)->TokenAppContainer)||
        !AddAccessAllowedAce(acl,ACL_REVISION,GENERIC_ALL,((TOKEN_GROUPS*)restricted.bytes)->Groups[0].Sid)||
        !InitializeAcl(label,sizeof(labelBytes),ACL_REVISION)||
        !AddMandatoryAce(label,ACL_REVISION,0,SYSTEM_MANDATORY_LABEL_NO_WRITE_UP,low)||
        !InitializeSecurityDescriptor(&descriptor,SECURITY_DESCRIPTOR_REVISION)||
        !SetSecurityDescriptorDacl(&descriptor,TRUE,acl,FALSE)||
        !SetSecurityDescriptorSacl(&descriptor,TRUE,label,FALSE))goto done;
    DWORD size=sizeof(request->descriptor.bytes);
    if(!MakeSelfRelativeSD(&descriptor,(PSECURITY_DESCRIPTOR)request->descriptor.bytes,&size))goto done;
    request->attributes.SecurityDescriptor=request->descriptor.bytes;
    /* Caller name and root are not redirected; only the NEW object's descriptor changes.
       An existing object ignores this creation descriptor. No existing ACL is edited. */
    accepted=TRUE;
done:
    /* Query-only current-token pseudo-handles are neither duplicated nor closed. */
    if(accepted!=1&&request->rootCopy){CloseHandle(request->rootCopy);request->rootCopy=NULL;}
    return accepted;
}
static int private_request(POBJECT_ATTRIBUTES supplied,PRIVATE_REQUEST *request,BOOL pidLink) { return private_request_policy(supplied,request,pidLink,TRUE); }

typedef NTSTATUS (NTAPI *MUTANT_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,BOOLEAN);
typedef NTSTATUS (NTAPI *EVENT_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,ULONG,BOOLEAN);
typedef NTSTATUS (NTAPI *SEMAPHORE_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,LONG,LONG);
typedef NTSTATUS (NTAPI *TIMER_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,ULONG);
typedef NTSTATUS (NTAPI *JOB_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES);
#define UNSUPPORTED ((NTSTATUS)0xc00000bbL)
#define QUOTA ((NTSTATUS)0xc0000044L)
static void finish(PRIVATE_REQUEST *request){if(request->rootCopy)CloseHandle(request->rootCopy);}
NTSTATUS NTAPI DbcNtCreateMutant(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,BOOLEAN owner) {
    MUTANT_FN real=NATIVE(MUTANT_FN,"NtCreateMutant");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    int adapt=private_request(attributes,&request,FALSE);if(adapt==2)return QUOTA;
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes,owner);finish(&request);return status;
}
NTSTATUS NTAPI DbcNtCreateEvent(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,ULONG type,BOOLEAN initial) {
    EVENT_FN real=NATIVE(EVENT_FN,"NtCreateEvent");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    request.rootCopy=NULL;int adapt=type<=1?private_request(attributes,&request,FALSE):0;if(adapt==2)return QUOTA;
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes,type,initial);finish(&request);return status;
}
NTSTATUS NTAPI DbcNtCreateSemaphore(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,LONG initial,LONG maximum) {
    SEMAPHORE_FN real=NATIVE(SEMAPHORE_FN,"NtCreateSemaphore");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    request.rootCopy=NULL;int adapt=initial>=0&&maximum>0&&initial<=maximum?private_request(attributes,&request,FALSE):0;if(adapt==2)return QUOTA;
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes,initial,maximum);finish(&request);return status;
}
NTSTATUS NTAPI DbcNtCreateTimer(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,ULONG type) {
    TIMER_FN real=NATIVE(TIMER_FN,"NtCreateTimer");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    request.rootCopy=NULL;int adapt=type<=1?private_request(attributes,&request,FALSE):0;if(adapt==2)return QUOTA;
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes,type);finish(&request);return status;
}
NTSTATUS NTAPI DbcNtCreateJobObject(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes) {
    JOB_FN real=NATIVE(JOB_FN,"NtCreateJobObject");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    int adapt=private_request(attributes,&request,FALSE);if(adapt==2)return QUOTA;
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes);finish(&request);return status;
}
typedef NTSTATUS (NTAPI *LINK_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PUNICODE_STRING);
NTSTATUS NTAPI DbcNtCreateSymbolicLinkObject(PHANDLE out,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,PUNICODE_STRING suppliedTarget) {
    LINK_FN real=NATIVE(LINK_FN,"NtCreateSymbolicLinkObject");PRIVATE_REQUEST request;
    if(!real)return UNSUPPORTED;
    WCHAR text[11];UNICODE_STRING target;BOOL numeric=FALSE;
    __try {
        if(suppliedTarget&&suppliedTarget->Buffer&&suppliedTarget->Length<=20&&!(suppliedTarget->Length&1)&&suppliedTarget->MaximumLength>=suppliedTarget->Length) {
            target=*suppliedTarget;memcpy(text,target.Buffer,target.Length);text[target.Length/2]=0;
            numeric=decimal_id(text,target.Length/2);target.Buffer=text;target.MaximumLength=(USHORT)(target.Length+2);
        }
    } __except(EXCEPTION_EXECUTE_HANDLER) { numeric=FALSE; }
    request.rootCopy=NULL;request.name[0]=0;request.rootName[0]=0;request.stage=0;int adapt=numeric?private_request(attributes,&request,TRUE):0;if(adapt==2)return QUOTA;
#ifdef MASON_COMPAT_DIAGNOSTICS
    static volatile LONG observations;
    if(InterlockedIncrement(&observations)<=32) {
        WCHAR observed[65]=L"",name[65]=L"";
        __try {
            if(suppliedTarget&&suppliedTarget->Buffer&&suppliedTarget->Length<=128&&!(suppliedTarget->Length&1)){memcpy(observed,suppliedTarget->Buffer,suppliedTarget->Length);observed[suppliedTarget->Length/2]=0;}
            if(attributes&&attributes->ObjectName&&attributes->ObjectName->Buffer&&attributes->ObjectName->Length<=128&&!(attributes->ObjectName->Length&1)){memcpy(name,attributes->ObjectName->Buffer,attributes->ObjectName->Length);name[attributes->ObjectName->Length/2]=0;}
        } __except(EXCEPTION_EXECUTE_HANDLER) { observed[0]=0;name[0]=0; }
        char message[1024];DWORD written;
        int length=_snprintf_s(message,sizeof(message),_TRUNCATE,"COMPAT pid=%lu numeric=%d adapt=%d stage=%lu error=%lu name=%ls target=%ls root=%ls\n",GetCurrentProcessId(),numeric,adapt,request.stage,GetLastError(),name,observed,request.rootName);
        if(length>0)WriteFile(GetStdHandle(STD_ERROR_HANDLE),message,(DWORD)length,&written,NULL);
    }
#endif
    NTSTATUS status=real(out,access,adapt==1?&request.attributes:attributes,adapt==1?&target:suppliedTarget);finish(&request);return status;
}
/* MSYS resets its default DACL during user initialization. Preserve only the
 * two command grants already established by the launcher before that reset. */
typedef union { ULONG_PTR alignment; BYTE bytes[16384]; } ACL_BUFFER;
static BOOL has_grant(PACL acl,PSID sid,DWORD mask) {
    for(WORD i=0;i<acl->AceCount;i++) {
        PVOID value=NULL;if(!GetAce(acl,i,&value))return FALSE;
        ACE_HEADER *header=(ACE_HEADER*)value;
        if(header->AceType==ACCESS_ALLOWED_ACE_TYPE&&header->AceFlags==0) {
            ACCESS_ALLOWED_ACE *ace=(ACCESS_ALLOWED_ACE*)value;
            if(EqualSid(&ace->SidStart,sid)&&(ace->Mask&mask)==mask)return TRUE;
        }
    }
    return FALSE;
}
static BOOL bounded_acl(PACL acl) {
    return acl&&acl->AclSize>=sizeof(ACL)&&acl->AclSize<=8192&&acl->AceCount<=256&&
        (acl->AclRevision==ACL_REVISION||acl->AclRevision==ACL_REVISION_DS)&&IsValidAcl(acl);
}
static BOOL descriptor_context(TOKEN_BUFFER *app,TOKEN_BUFFER *restricted) {
    TOKEN_BUFFER user,eu,ea,er;
    if(!current_context(GetCurrentProcessToken(),&user,app,restricted)||
        !current_context(GetCurrentThreadEffectiveToken(),&eu,&ea,&er))return FALSE;
    return EqualSid(((TOKEN_USER*)user.bytes)->User.Sid,((TOKEN_USER*)eu.bytes)->User.Sid)&&
        EqualSid(((TOKEN_APPCONTAINER_INFORMATION*)app->bytes)->TokenAppContainer,((TOKEN_APPCONTAINER_INFORMATION*)ea.bytes)->TokenAppContainer)&&
        EqualSid(((TOKEN_GROUPS*)restricted->bytes)->Groups[0].Sid,((TOKEN_GROUPS*)er.bytes)->Groups[0].Sid);
}
static BOOL self_token(HANDLE token) {
    QUERY_OBJECT query=NATIVE(QUERY_OBJECT,"NtQueryObject");WCHAR type[32];
    TOKEN_STATISTICS supplied,current;DWORD size=0;
    if(!query||!object_text(query,token,2,type,32)||wcscmp(type,L"Token")!=0||
        !GetTokenInformation(token,TokenStatistics,&supplied,sizeof(supplied),&size)||
        !GetTokenInformation(GetCurrentProcessToken(),TokenStatistics,&current,sizeof(current),&size))return FALSE;
    return supplied.TokenType==TokenPrimary&&supplied.TokenId.LowPart==current.TokenId.LowPart&&supplied.TokenId.HighPart==current.TokenId.HighPart;
}
static BOOL self_process(HANDLE process) {
    if(GetProcessId(process)!=GetCurrentProcessId())return FALSE;
    if(process==GetCurrentProcess())return TRUE;
    QUERY_OBJECT query=NATIVE(QUERY_OBJECT,"NtQueryObject");WCHAR type[32];
    return query&&object_text(query,process,2,type,32)&&wcscmp(type,L"Process")==0;
}
static BOOL preserve_acl(PACL supplied,ACL_BUFFER *merged,TOKEN_BUFFER *app,TOKEN_BUFFER *restricted) {
    ACL_BUFFER defaults;DWORD size=0;
    if(!GetTokenInformation(GetCurrentProcessToken(),TokenDefaultDacl,defaults.bytes,sizeof(defaults.bytes),&size))return FALSE;
    PACL original=((TOKEN_DEFAULT_DACL*)defaults.bytes)->DefaultDacl;
    PSID appSid=((TOKEN_APPCONTAINER_INFORMATION*)app->bytes)->TokenAppContainer,writeSid=((TOKEN_GROUPS*)restricted->bytes)->Groups[0].Sid;
    __try {
        /* Never invent a grant absent from the current launcher descriptor. */
        if(!bounded_acl(original)||!bounded_acl(supplied)||!has_grant(original,appSid,GENERIC_ALL)||!has_grant(original,writeSid,GENERIC_ALL))return FALSE;
        PACL result=(PACL)merged->bytes;
        if(!InitializeAcl(result,sizeof(merged->bytes),supplied->AclRevision))return FALSE;
        for(WORD i=0;i<supplied->AceCount;i++) {
            PVOID ace=NULL;if(!GetAce(supplied,i,&ace)||!AddAce(result,supplied->AclRevision,MAXDWORD,ace,((ACE_HEADER*)ace)->AceSize))return FALSE;
        }
        if(!has_grant(result,appSid,GENERIC_ALL)&&!AddAccessAllowedAce(result,supplied->AclRevision,GENERIC_ALL,appSid))return FALSE;
        if(!has_grant(result,writeSid,GENERIC_ALL)&&!AddAccessAllowedAce(result,supplied->AclRevision,GENERIC_ALL,writeSid))return FALSE;
        ACL_SIZE_INFORMATION used;if(!GetAclInformation(result,&used,sizeof(used),AclSizeInformation)||used.AclBytesInUse>8192)return FALSE;
        result->AclSize=(WORD)used.AclBytesInUse;
        return TRUE;
    } __except(EXCEPTION_EXECUTE_HANDLER) {return FALSE;}
}
typedef NTSTATUS (NTAPI *TOKEN_SET_FN)(HANDLE,TOKEN_INFORMATION_CLASS,PVOID,ULONG);
typedef NTSTATUS (NTAPI *SECURITY_SET_FN)(HANDLE,SECURITY_INFORMATION,PSECURITY_DESCRIPTOR);
#define INVALID_INPUT ((NTSTATUS)0xc000000dL)
NTSTATUS NTAPI DbcNtSetInformationToken(HANDLE token,TOKEN_INFORMATION_CLASS information,PVOID buffer,ULONG size) {
    TOKEN_SET_FN real=NATIVE(TOKEN_SET_FN,"NtSetInformationToken");if(!real)return UNSUPPORTED;
    if(information!=TokenDefaultDacl)return real(token,information,buffer,size);
    TOKEN_BUFFER app,restricted;if(!descriptor_context(&app,&restricted)||!self_token(token))return real(token,information,buffer,size);
    if(size!=sizeof(TOKEN_DEFAULT_DACL))return INVALID_INPUT;
    ACL_BUFFER merged;TOKEN_DEFAULT_DACL value;
    __try {if(!buffer)return INVALID_INPUT;value=*(TOKEN_DEFAULT_DACL*)buffer;} __except(EXCEPTION_EXECUTE_HANDLER) {return INVALID_INPUT;}
    if(!preserve_acl(value.DefaultDacl,&merged,&app,&restricted))return INVALID_INPUT;
    if(InterlockedIncrement(&descriptorAttempts)>16)return QUOTA;
    value.DefaultDacl=(PACL)merged.bytes;return real(token,information,&value,sizeof(value));
}
NTSTATUS NTAPI DbcNtSetSecurityObject(HANDLE process,SECURITY_INFORMATION information,PSECURITY_DESCRIPTOR descriptor) {
    SECURITY_SET_FN real=NATIVE(SECURITY_SET_FN,"NtSetSecurityObject");if(!real)return UNSUPPORTED;
    if(information!=DACL_SECURITY_INFORMATION)return real(process,information,descriptor);
    TOKEN_BUFFER app,restricted;if(!descriptor_context(&app,&restricted)||!self_process(process))return real(process,information,descriptor);
    ACL_BUFFER merged;PACL supplied=NULL;BOOL present=FALSE,defaults=FALSE;SECURITY_DESCRIPTOR value;SECURITY_DESCRIPTOR_CONTROL control;DWORD revision;
    __try {
        if(!descriptor||!IsValidSecurityDescriptor(descriptor)||!GetSecurityDescriptorDacl(descriptor,&present,&supplied,&defaults)||!present||!supplied||
            !GetSecurityDescriptorControl(descriptor,&control,&revision)||!preserve_acl(supplied,&merged,&app,&restricted))return INVALID_INPUT;
    } __except(EXCEPTION_EXECUTE_HANDLER) {return INVALID_INPUT;}
    SECURITY_DESCRIPTOR_CONTROL daclControl=SE_DACL_AUTO_INHERIT_REQ|SE_DACL_AUTO_INHERITED|SE_DACL_PROTECTED;
    if(!InitializeSecurityDescriptor(&value,SECURITY_DESCRIPTOR_REVISION)||!SetSecurityDescriptorDacl(&value,TRUE,(PACL)merged.bytes,defaults)||
        !SetSecurityDescriptorControl(&value,daclControl,control&daclControl))return INVALID_INPUT;
    if(InterlockedIncrement(&descriptorAttempts)>16)return QUOTA;
    return real(process,information,&value);
}
#ifdef MASON_HANDOFF
#include "CodeHandoff.inc"
#endif
#pragma warning(pop)
