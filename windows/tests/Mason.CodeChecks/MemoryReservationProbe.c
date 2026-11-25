#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <winternl.h>
#include <stdio.h>
#include <stdint.h>
#include <wchar.h>
#include <string.h>

/* QA ONLY. No MSYS hooks or production broker changes. Native structure layout
 * follows PHNT ntpsapi.h; private API support is an experimental outcome. */
#define HEAP_BASE ((void *)(uintptr_t)0x800000000ULL)
#define HEAP_BYTES ((SIZE_T)0x200000000ULL)
typedef union { ULONG_PTR alignment; BYTE data[8192]; } TOKEN_DATA;
typedef struct { UNICODE_STRING name;USHORT type,reserved;ULONG flags,count;void *values; } SECURITY_ATTRIBUTE;
typedef struct { USHORT version,reserved;ULONG count;SECURITY_ATTRIBUTE *attributes; } TOKEN_ATTRIBUTES;
typedef struct { TOKEN_DATA app,restricted,capabilities,privileges,integrity,user; DWORD aslr,dep,dynamicCode,cfg,lpac; } IDENTITY;
static BOOL range_outside(const void *p) { return (uintptr_t)p<(uintptr_t)HEAP_BASE || (uintptr_t)p>=(uintptr_t)HEAP_BASE+HEAP_BYTES; }
static BOOL token_field(HANDLE token,TOKEN_INFORMATION_CLASS kind,TOKEN_DATA *out) {
    DWORD bytes=0;ZeroMemory(out,sizeof(*out));return GetTokenInformation(token,kind,out->data,sizeof(out->data),&bytes);
}
static BOOL buffer_contains(const TOKEN_DATA *data,const void *pointer,SIZE_T bytes) {
    uintptr_t start=(uintptr_t)data->data,p=(uintptr_t)pointer;
    return p>=start&&p<=start+sizeof(data->data)&&bytes<=start+sizeof(data->data)-p;
}
static BOOL lpac_attribute(HANDLE token) {
    /* Class 46 returns ERROR_INVALID_PARAMETER on this Windows build. Query
     * the existing native security attribute instead; never write a token. */
    TOKEN_DATA data;if(!token_field(token,TokenSecurityAttributes,&data))return FALSE;
    TOKEN_ATTRIBUTES *info=(TOKEN_ATTRIBUTES*)data.data;
    if(info->version!=1||info->count>32||!buffer_contains(&data,info->attributes,info->count*sizeof(SECURITY_ATTRIBUTE)))return FALSE;
    const WCHAR expected[]=L"WIN://NOALLAPPPKG";
    for(ULONG i=0;i<info->count;i++) {
        SECURITY_ATTRIBUTE *attribute=&info->attributes[i];
        if(attribute->name.Length!=sizeof(expected)-sizeof(WCHAR)||!buffer_contains(&data,attribute->name.Buffer,attribute->name.Length))continue;
        if(memcmp(attribute->name.Buffer,expected,attribute->name.Length)==0)
            return attribute->type==2&&attribute->count==1&&buffer_contains(&data,attribute->values,sizeof(ULONGLONG))&&*(ULONGLONG*)attribute->values!=0;
    }
    return FALSE;
}
static BOOL identity(HANDLE process,IDENTITY *out) {
    HANDLE token=NULL;DWORD bytes=0,container=0;BOOL ok=FALSE;ZeroMemory(out,sizeof(*out));
    if(!OpenProcessToken(process,TOKEN_QUERY,&token))return FALSE;
    ok=GetTokenInformation(token,TokenIsAppContainer,&container,sizeof(container),&bytes)&&container==1&&IsTokenRestricted(token)
       &&token_field(token,TokenAppContainerSid,&out->app)&&token_field(token,TokenRestrictedSids,&out->restricted)
       &&token_field(token,TokenCapabilities,&out->capabilities)&&token_field(token,TokenPrivileges,&out->privileges)
       &&token_field(token,TokenIntegrityLevel,&out->integrity)&&token_field(token,TokenUser,&out->user);
    out->lpac=lpac_attribute(token)?1:0;
    if(out->lpac!=1)printf("{\"stage\":\"lpac-attribute\",\"present\":false,\"error\":%lu}\n",GetLastError());
    ok=ok&&out->lpac==1;
    CloseHandle(token);
    if(!ok||((TOKEN_GROUPS*)out->restricted.data)->GroupCount!=1)return FALSE;
    PROCESS_MITIGATION_ASLR_POLICY aslr={0};PROCESS_MITIGATION_DEP_POLICY dep={0};
    PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamicCode={0};PROCESS_MITIGATION_CONTROL_FLOW_GUARD_POLICY cfg={0};
    if(!GetProcessMitigationPolicy(process,ProcessASLRPolicy,&aslr,sizeof(aslr))
       ||!GetProcessMitigationPolicy(process,ProcessDEPPolicy,&dep,sizeof(dep))
       ||!GetProcessMitigationPolicy(process,ProcessDynamicCodePolicy,&dynamicCode,sizeof(dynamicCode))
       ||!GetProcessMitigationPolicy(process,ProcessControlFlowGuardPolicy,&cfg,sizeof(cfg)))return FALSE;
    out->aslr=aslr.Flags;out->dep=dep.Flags;out->dynamicCode=dynamicCode.Flags;out->cfg=cfg.Flags;return TRUE;
}
static BOOL group_equal(TOKEN_DATA *a,TOKEN_DATA *b) {
    TOKEN_GROUPS *x=(TOKEN_GROUPS*)a->data,*y=(TOKEN_GROUPS*)b->data;
    if(x->GroupCount!=y->GroupCount||x->GroupCount>128)return FALSE;
    for(DWORD i=0;i<x->GroupCount;i++)if(x->Groups[i].Attributes!=y->Groups[i].Attributes||!EqualSid(x->Groups[i].Sid,y->Groups[i].Sid))return FALSE;
    return TRUE;
}
static BOOL identity_equal(IDENTITY *a,IDENTITY *b) {
    TOKEN_PRIVILEGES *x=(TOKEN_PRIVILEGES*)a->privileges.data,*y=(TOKEN_PRIVILEGES*)b->privileges.data;
    return EqualSid(((TOKEN_APPCONTAINER_INFORMATION*)a->app.data)->TokenAppContainer,((TOKEN_APPCONTAINER_INFORMATION*)b->app.data)->TokenAppContainer)
        &&EqualSid(((TOKEN_USER*)a->user.data)->User.Sid,((TOKEN_USER*)b->user.data)->User.Sid)
        &&group_equal(&a->restricted,&b->restricted)&&group_equal(&a->capabilities,&b->capabilities)
        &&EqualSid(((TOKEN_MANDATORY_LABEL*)a->integrity.data)->Label.Sid,((TOKEN_MANDATORY_LABEL*)b->integrity.data)->Label.Sid)
        &&x->PrivilegeCount==y->PrivilegeCount&&x->PrivilegeCount<=128
        &&memcmp(x->Privileges,y->Privileges,x->PrivilegeCount*sizeof(LUID_AND_ATTRIBUTES))==0
        &&a->aslr==b->aslr&&a->dep==b->dep&&a->dynamicCode==b->dynamicCode&&a->cfg==b->cfg&&a->lpac==b->lpac;
}
static BOOL reservation(HANDLE process) {
    MEMORY_BASIC_INFORMATION memory={0};
    return VirtualQueryEx(process,HEAP_BASE,&memory,sizeof(memory))==sizeof(memory)
        &&memory.BaseAddress==HEAP_BASE&&memory.AllocationBase==HEAP_BASE&&memory.RegionSize==HEAP_BYTES
        &&memory.State==MEM_RESERVE&&memory.Type==MEM_PRIVATE
        &&!(memory.AllocationProtect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
#ifdef ROLE_SENTINEL
static BOOL inaccessible_reserve(void) {
    BYTE value=0;SIZE_T done=0;
    /* ReadProcessMemory/WriteProcessMemory against our OWN uncommitted page
     * must fail without changing the reservation or committing storage. */
    BOOL read=ReadProcessMemory(GetCurrentProcess(),HEAP_BASE,&value,1,&done);done=0;
    BOOL write=WriteProcessMemory(GetCurrentProcess(),HEAP_BASE,&value,1,&done);
    return !read&&!write&&reservation(GetCurrentProcess());
}
static BOOL write_inside(void) {
    HANDLE file=CreateFileW(L"reservation-inside.txt",GENERIC_WRITE|GENERIC_READ,FILE_SHARE_READ,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file==INVALID_HANDLE_VALUE)return FALSE;DWORD bytes=0;char data[6]={0};BOOL ok=WriteFile(file,"probe",5,&bytes,NULL)&&bytes==5;
    ok=ok&&SetFilePointer(file,0,NULL,FILE_BEGIN)!=INVALID_SET_FILE_POINTER&&ReadFile(file,data,5,&bytes,NULL)&&bytes==5&&memcmp(data,"probe",5)==0;
    CloseHandle(file);return ok;
}
static BOOL write_denied(const WCHAR *path) {
    HANDLE file=CreateFileW(path,FILE_WRITE_DATA|FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(file==INVALID_HANDLE_VALUE)return GetLastError()==ERROR_ACCESS_DENIED;CloseHandle(file);return FALSE;
}
static BOOL network_denied(unsigned short port) {
    WSADATA data;if(WSAStartup(MAKEWORD(2,2),&data)!=0)return FALSE;
    SOCKET socketHandle=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);BOOL ok=FALSE;
    if(socketHandle==INVALID_SOCKET)ok=WSAGetLastError()==WSAEACCES;
    else {
        struct sockaddr_in address={0};address.sin_family=AF_INET;address.sin_port=htons(port);address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
        DWORD timeout=500;setsockopt(socketHandle,SOL_SOCKET,SO_SNDTIMEO,(const char*)&timeout,sizeof(timeout));
        ok=connect(socketHandle,(const struct sockaddr*)&address,sizeof(address))==SOCKET_ERROR&&WSAGetLastError()==WSAEACCES;closesocket(socketHandle);
    }
    WSACleanup();return ok;
}
#endif
static BOOL port_value(const WCHAR *text,unsigned short *port) {
    unsigned long value=0;if(!*text)return FALSE;
    for(;*text;text++){if(*text<L'0'||*text>L'9')return FALSE;value=value*10+(unsigned long)(*text-L'0');if(value>65535)return FALSE;}
    *port=(unsigned short)value;return value>0;
}
static BOOL marker(const WCHAR *path,const char *text) {
    HANDLE file=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file==INVALID_HANDLE_VALUE)return FALSE;DWORD length=(DWORD)strlen(text),written=0;
    BOOL ok=WriteFile(file,text,length,&written,NULL)&&written==length;CloseHandle(file);return ok;
}

#ifdef ROLE_SENTINEL
int wmain(int argc,WCHAR **argv) {
    if(argc!=5||(wcscmp(argv[1],L"control")&&wcscmp(argv[1],L"reserved")&&wcscmp(argv[1],L"pause")&&wcscmp(argv[1],L"adopt")))return 2;
    BOOL adopt=wcscmp(argv[1],L"adopt")==0;
    BOOL paused=wcscmp(argv[1],L"pause")==0,reserved=wcscmp(argv[1],L"control")!=0,inJob=FALSE;unsigned short port=0;
    IDENTITY before,after;if(!port_value(argv[4],&port)||!identity(GetCurrentProcess(),&before))return 3;
    BOOL boundary=IsProcessInJob(GetCurrentProcess(),NULL,&inJob)&&inJob&&write_inside()&&write_denied(argv[2])&&write_denied(argv[3])&&network_denied(port);
    BOOL layout=TRUE,handoff=TRUE;MEMORY_BASIC_INFORMATION memory={0};
    if(reserved) {
        /* One exact uncommitted VAD excludes every other stack/TEB/PEB mapping
         * from this interval, including loader workers still alive at entry. */
        layout=reservation(GetCurrentProcess())&&range_outside(NtCurrentTeb())&&range_outside(&memory);
        if(!layout)return 4;
        if(paused) {
            char receipt[512];
            if(!boundary||sprintf_s(receipt,sizeof(receipt),"{\"pid\":%lu,\"boundary\":true,\"exactReservation\":true,\"aslr\":%lu}",GetCurrentProcessId(),before.aslr)<0||!marker(L"reservation-ready.json",receipt))return 6;
            Sleep(60000);return 7; // Parent cancellation must kill this owned process.
        }
        if(VirtualQuery(HEAP_BASE,&memory,sizeof(memory))!=sizeof(memory))return 4;
        if(adopt) {
            printf("{\"stage\":\"adoption-metadata\",\"allocationProtect\":%lu,\"protect\":%lu,\"state\":%lu,\"type\":%lu,\"bytes\":%llu}\n",memory.AllocationProtect,memory.Protect,memory.State,memory.Type,(unsigned long long)memory.RegionSize);fflush(stdout);
            /* Do not release the kernel-created range. The normal following
             * commit must operate on this exact reservation in place. */
            /* AllocationProtect is allocation metadata, not access to pages
             * with no committed storage. Admit only this non-executable
             * kernel variant after proving both read/write are denied. */
            handoff=(memory.AllocationProtect==PAGE_NOACCESS||memory.AllocationProtect==PAGE_READWRITE)&&inaccessible_reserve();
        } else handoff=VirtualFree(HEAP_BASE,0,MEM_RELEASE)&&VirtualAlloc(HEAP_BASE,HEAP_BYTES,MEM_RESERVE,PAGE_NOACCESS)==HEAP_BASE;
        if(handoff) {
            SIZE_T commit=adopt?0x300000:65536;
            void *page=VirtualAlloc(HEAP_BASE,commit,MEM_COMMIT,PAGE_READWRITE);handoff=page==HEAP_BASE;
            if(page==HEAP_BASE){((volatile BYTE*)page)[0]=0x5a;((volatile BYTE*)page)[commit-1]=0xa5;handoff=handoff&&((volatile BYTE*)page)[0]==0x5a&&((volatile BYTE*)page)[commit-1]==0xa5;}
            if(adopt){MEMORY_BASIC_INFORMATION committed={0},tail={0};handoff=handoff&&VirtualQuery(HEAP_BASE,&committed,sizeof(committed))==sizeof(committed)&&committed.State==MEM_COMMIT&&committed.Protect==PAGE_READWRITE&&VirtualQuery((BYTE*)HEAP_BASE+commit,&tail,sizeof(tail))==sizeof(tail)&&tail.State==MEM_RESERVE&&tail.AllocationBase==HEAP_BASE;}
            if(!VirtualFree(HEAP_BASE,0,MEM_RELEASE))handoff=FALSE;
            handoff=handoff&&VirtualQuery(HEAP_BASE,&memory,sizeof(memory))==sizeof(memory)&&memory.State==MEM_FREE;
        }
    }
    BOOL unchanged=identity(GetCurrentProcess(),&after)&&identity_equal(&before,&after);
    printf("{\"sentinel\":\"%s\",\"boundary\":%s,\"loaderLayout\":%s,\"handoff\":%s,\"identityUnchanged\":%s,\"aslr\":%lu}\n",reserved?"reserved":"control",boundary?"true":"false",layout?"true":"false",handoff?"true":"false",unchanged?"true":"false",before.aslr);fflush(stdout);
    return boundary&&layout&&handoff&&unchanged?0:5;
}
#else
typedef struct { ULONG_PTR attribute;SIZE_T size;union { ULONG_PTR value;void *pointer; } content;SIZE_T *returnLength; } ATTRIBUTE;
typedef struct { SIZE_T length;ATTRIBUTE entries[5]; } ATTRIBUTES;
typedef struct { void *address;SIZE_T size; } MEMORY_RESERVE;
typedef struct { SIZE_T size;ULONG state;union { ULONG_PTR alignment;BYTE data[72]; } content; } CREATE_INFO;
typedef struct { NTSTATUS status;void *teb;CLIENT_ID client;ULONG_PTR affinity;LONG priority,basePriority; } THREAD_INFO;
typedef NTSTATUS (NTAPI *CREATE_PROCESS)(HANDLE*,HANDLE*,ACCESS_MASK,ACCESS_MASK,OBJECT_ATTRIBUTES*,OBJECT_ATTRIBUTES*,ULONG,ULONG,void*,CREATE_INFO*,ATTRIBUTES*);
typedef NTSTATUS (NTAPI *CREATE_PARAMETERS)(void**,UNICODE_STRING*,UNICODE_STRING*,UNICODE_STRING*,UNICODE_STRING*,void*,UNICODE_STRING*,UNICODE_STRING*,UNICODE_STRING*,UNICODE_STRING*,ULONG);
typedef void (NTAPI *DESTROY_PARAMETERS)(void*);
typedef NTSTATUS (NTAPI *QUERY_THREAD)(HANDLE,ULONG,void*,ULONG,ULONG*);
typedef NTSTATUS (NTAPI *QUERY_PROCESS)(HANDLE,ULONG,void*,ULONG,ULONG*);
static void unicode(UNICODE_STRING *string,WCHAR *text) { string->Buffer=text;string->Length=(USHORT)(wcslen(text)*2);string->MaximumLength=string->Length+2; }
static BOOL initial_layout(HANDLE process,HANDLE thread,void **peb,void **teb,void **stack) {
    PROCESS_BASIC_INFORMATION basic={0};THREAD_INFO info={0};NT_TIB tib={0};SIZE_T read=0;CONTEXT context={0};
#pragma warning(push)
#pragma warning(disable:4191)
    HMODULE native=GetModuleHandleW(L"ntdll.dll");QUERY_THREAD queryThread=(QUERY_THREAD)GetProcAddress(native,"NtQueryInformationThread");QUERY_PROCESS queryProcess=(QUERY_PROCESS)GetProcAddress(native,"NtQueryInformationProcess");
#pragma warning(pop)
    if(!queryThread||!queryProcess||queryProcess(process,0,&basic,sizeof(basic),NULL)<0||queryThread(thread,0,&info,sizeof(info),NULL)<0)return FALSE;
    context.ContextFlags=CONTEXT_CONTROL;if(!GetThreadContext(thread,&context)||!ReadProcessMemory(process,info.teb,&tib,sizeof(tib),&read)||read!=sizeof(tib))return FALSE;
    *peb=basic.PebBaseAddress;*teb=info.teb;*stack=(void*)(uintptr_t)context.Rsp;
    return range_outside(*peb)&&range_outside(*teb)&&range_outside(*stack)&&range_outside(tib.StackLimit)&&range_outside(tib.StackBase);
}
int wmain(int argc,WCHAR **argv) {
    WCHAR image[4096],ntImage[4100],command[16384],directory[4096];unsigned short port=0;
    if((argc!=4&&argc!=5)||!port_value(argv[3],&port)||wcschr(argv[1],L'"')||wcschr(argv[2],L'"'))return 2;
    BOOL cancel=argc==5&&wcscmp(argv[4],L"cancel")==0,adopt=argc==5&&wcscmp(argv[4],L"adopt")==0;if(argc==5&&!cancel&&!adopt)return 2;
    DWORD imageLength=GetModuleFileNameW(NULL,image,4096);if(!imageLength||imageLength>=4000)return 2;
    WCHAR *name=wcsrchr(image,L'\\');if(!name)return 2;wcscpy_s(name+1,4096-(size_t)(name+1-image),L"MemoryReservationSentinel.exe");
    if(GetFileAttributesW(image)==INVALID_FILE_ATTRIBUTES||!GetCurrentDirectoryW(4096,directory))return 2;
    if(swprintf_s(ntImage,4100,L"\\??\\%s",image)<0)return 2;
#pragma warning(push)
#pragma warning(disable:4191)
    HMODULE native=GetModuleHandleW(L"ntdll.dll");CREATE_PROCESS create=(CREATE_PROCESS)GetProcAddress(native,"NtCreateUserProcess");CREATE_PARAMETERS parameters=(CREATE_PARAMETERS)GetProcAddress(native,"RtlCreateProcessParametersEx");DESTROY_PARAMETERS destroy=(DESTROY_PARAMETERS)GetProcAddress(native,"RtlDestroyProcessParameters");
#pragma warning(pop)
    if(!create||!parameters||!destroy||sizeof(CREATE_INFO)!=88||sizeof(ATTRIBUTE)!=32)return 3;
    IDENTITY parent;if(!identity(GetCurrentProcess(),&parent))return 3;
    BOOL parentJob=FALSE;if(!IsProcessInJob(GetCurrentProcess(),NULL,&parentJob)||!parentJob)return 3;
    /* Only these command-owned standard handles can cross into the sentinel. */
    HANDLE handles[3];size_t handleCount=0;DWORD standardIds[]={STD_INPUT_HANDLE,STD_OUTPUT_HANDLE,STD_ERROR_HANDLE};
    for(size_t i=0;i<3;i++) {
        HANDLE handle=GetStdHandle(standardIds[i]);DWORD flags=0;
        if(handle==NULL||handle==INVALID_HANDLE_VALUE||!GetHandleInformation(handle,&flags)||(flags&HANDLE_FLAG_INHERIT)==0)return 3;
        BOOL found=FALSE;for(size_t j=0;j<handleCount;j++)if(handles[j]==handle)found=TRUE;if(!found)handles[handleCount++]=handle;
    }
    /* Keep the exact named private desktop supplied by the Auto launcher.
     * A null native DesktopInfo defaults to an inaccessible interactive one.
     * PHNT x64 RTL_USER_PROCESS_PARAMETERS places DesktopInfo at 0xc0. */
    BYTE *ownParams=(BYTE*)NtCurrentTeb()->ProcessEnvironmentBlock->ProcessParameters;
    if(!ownParams||*(ULONG*)(ownParams+4)<0xd0)return 3;
    UNICODE_STRING desktop=*(UNICODE_STRING*)(ownParams+0xc0);
    if(!desktop.Buffer||!desktop.Length||(desktop.Length&1)||desktop.Length>8190||desktop.MaximumLength<desktop.Length)return 3;
    LPWCH environment=GetEnvironmentStringsW();if(!environment)return 3;
    if(cancel){char pid[32];if(sprintf_s(pid,sizeof(pid),"%lu",GetCurrentProcessId())<0||!marker(L"reservation-launcher.pid",pid)){FreeEnvironmentStringsW(environment);return 3;}}
    int final=0;
    /* At most 100 children total, including the control. Cancellation uses
     * a separate single-child fixture and never advances this loop. */
    for(int attempt=cancel?0:-1;attempt<(cancel?1:99);attempt++) {
#ifdef QA_MULTIPLE_RESERVATIONS
        if(attempt>0)break; /* One control and one experimental child only. */
#endif
        BOOL reserved=attempt>=0;UNICODE_STRING path,cmd,cwd;void *params=NULL;HANDLE process=NULL,thread=NULL;
        if(swprintf_s(command,16384,L"\"%s\" %s \"%s\" \"%s\" %u",image,cancel?L"pause":reserved?(adopt?L"adopt":L"reserved"):L"control",argv[1],argv[2],(unsigned)port)<0){final=3;break;}
        unicode(&path,image);unicode(&cmd,command);unicode(&cwd,directory);
        NTSTATUS status=parameters(&params,&path,NULL,&cwd,&cmd,environment,NULL,&desktop,NULL,NULL,1);
        if(status<0){printf("{\"stage\":\"parameters\",\"status\":\"%08lx\"}\n",(ULONG)status);final=4;break;}
        /* SDK exposes this prefix as Reserved fields. Native x64 standard
         * handles are at 0x20/0x28/0x30; no other parameter field is changed. */
        *(HANDLE*)((BYTE*)params+0x20)=GetStdHandle(STD_INPUT_HANDLE);*(HANDLE*)((BYTE*)params+0x28)=GetStdHandle(STD_OUTPUT_HANDLE);*(HANDLE*)((BYTE*)params+0x30)=GetStdHandle(STD_ERROR_HANDLE);
        /* Preserve explicit stdio instead of allowing kernel32 startup to
         * replace it with default console handles (there is no console). */
        *(ULONG*)((BYTE*)params+0xa4)|=STARTF_USESTDHANDLES;
        CREATE_INFO info={0};info.size=sizeof(info);ATTRIBUTES attributes={0};MEMORY_RESERVE reserve={HEAP_BASE,HEAP_BYTES};
        attributes.entries[0]=(ATTRIBUTE){0x20005,wcslen(ntImage)*2,{.pointer=ntImage},NULL};
        size_t count=1;if(reserved)attributes.entries[count++]=(ATTRIBUTE){0x20007,sizeof(reserve),{.pointer=&reserve},NULL};
#ifdef QA_MULTIPLE_RESERVATIONS
        MEMORY_RESERVE ranges[2]={reserve,{(void*)(uintptr_t)0xa00000000ULL,(SIZE_T)QA_SECOND_RESERVE_BYTES}};
        if(reserved)attributes.entries[1]=(ATTRIBUTE){0x20007,sizeof(ranges),{.pointer=ranges},NULL};
#endif
        attributes.entries[count++]=(ATTRIBUTE){0x2000b,handleCount*sizeof(HANDLE),{.pointer=handles},NULL};
        attributes.length=sizeof(SIZE_T)+count*sizeof(ATTRIBUTE);
        status=create(&process,&thread,PROCESS_QUERY_INFORMATION|PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_VM_READ|PROCESS_TERMINATE|SYNCHRONIZE,
            THREAD_QUERY_INFORMATION|THREAD_GET_CONTEXT|THREAD_SUSPEND_RESUME|SYNCHRONIZE,NULL,NULL,4,1,params,&info,&attributes);
        destroy(params);
        printf("{\"stage\":\"create\",\"attempt\":%d,\"status\":\"%08lx\",\"createState\":%lu,\"requestedBase\":\"%p\",\"requestedBytes\":%llu}\n",attempt,(ULONG)status,info.state,reserve.address,(unsigned long long)reserve.size);fflush(stdout);
        /* Native creation can return image/section handles even on failure. */
        if(info.state==6){HANDLE file=NULL,section=NULL;memcpy(&file,info.content.data+8,sizeof(file));memcpy(&section,info.content.data+16,sizeof(section));if(file)CloseHandle(file);if(section)CloseHandle(section);}
        else if(info.state==2||info.state==5){HANDLE file=NULL;memcpy(&file,info.content.data,sizeof(file));if(file)CloseHandle(file);}
        if(status<0){if(thread)CloseHandle(thread);if(process){TerminateProcess(process,1);CloseHandle(process);}final=10;break;}
        IDENTITY child;BOOL job=FALSE;void *peb=NULL,*teb=NULL,*stack=NULL;
        BOOL token=identity(process,&child)&&identity_equal(&parent,&child);
        BOOL layout=initial_layout(process,thread,&peb,&teb,&stack);
        BOOL memory=!reserved||reservation(process);BOOL jobOk=IsProcessInJob(process,NULL,&job)&&job;
#ifdef QA_MULTIPLE_RESERVATIONS
        if(reserved) {
            MEMORY_BASIC_INFORMATION extra={0};BOOL second=VirtualQueryEx(process,ranges[1].address,&extra,sizeof(extra))==sizeof(extra)
                &&extra.BaseAddress==ranges[1].address&&extra.AllocationBase==ranges[1].address&&extra.RegionSize==ranges[1].size
                &&extra.State==MEM_RESERVE&&extra.Type==MEM_PRIVATE&&!(extra.AllocationProtect&0xf0);
            printf("{\"stage\":\"second-reservation\",\"exact\":%s,\"base\":\"%p\",\"bytes\":%llu}\n",second?"true":"false",extra.BaseAddress,(unsigned long long)extra.RegionSize);fflush(stdout);
            memory=memory&&second;
        }
#endif
        printf("{\"stage\":\"suspended\",\"attempt\":%d,\"tokenAndMitigations\":%s,\"inJob\":%s,\"layout\":%s,\"exactReservation\":%s,\"peb\":\"%p\",\"teb\":\"%p\",\"stack\":\"%p\",\"aslr\":%lu}\n",attempt,token?"true":"false",jobOk?"true":"false",layout?"true":"false",memory?"true":"false",peb,teb,stack,child.aslr);fflush(stdout);
        DWORD exit=1;
        if(!token||!jobOk||!layout||!memory||ResumeThread(thread)==(DWORD)-1){TerminateProcess(process,1);final=11;}
        else if(WaitForSingleObject(process,10000)!=WAIT_OBJECT_0){TerminateProcess(process,1);final=12;}
        else if(!GetExitCodeProcess(process,&exit)||exit!=0)final=13;
        WaitForSingleObject(process,2000);CloseHandle(thread);CloseHandle(process);
        printf("{\"stage\":\"exit\",\"attempt\":%d,\"childExit\":%lu,\"result\":%d}\n",attempt,exit,final);fflush(stdout);
        if(final)break;
    }
    FreeEnvironmentStringsW(environment);return final;
}
#endif
