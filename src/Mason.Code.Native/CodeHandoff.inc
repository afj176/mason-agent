/* Included only by the separately built handoff helper variant. */
#define DBC_BUILD_HELPER
#include "CodeBrokerAbi.h"
#include <intrin.h>
typedef NTSTATUS (NTAPI *DIRECTORY_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES);
typedef NTSTATUS (NTAPI *SECTION_FN)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PLARGE_INTEGER,ULONG,ULONG,HANDLE);

static void handoff_frame(DBC_REQUEST *request,ULONG operation) {
    ZeroMemory(request,sizeof(*request));request->version=DBC_ABI_VERSION;request->size=sizeof(*request);request->operation=operation;
}
static BOOL handoff_name(DBC_REQUEST *request,POBJECT_ATTRIBUTES attributes) {
    __try {
        if(!attributes||attributes->Length!=sizeof(*attributes)||!attributes->ObjectName||!attributes->ObjectName->Buffer)return FALSE;
        UNICODE_STRING name=*attributes->ObjectName;
        if(!name.Length||(name.Length&1)||name.Length>440||name.MaximumLength<name.Length)return FALSE;
        for(USHORT i=0;i<name.Length/2;i++)if(name.Buffer[i]<0x20||name.Buffer[i]>0x7e)return FALSE;
        memcpy(request->name,name.Buffer,name.Length);request->nameBytes=name.Length;return TRUE;
    } __except(EXCEPTION_EXECUTE_HANDLER){return FALSE;}
}
NTSTATUS NTAPI DbcNtCreateDirectoryObject(PHANDLE output,ACCESS_MASK access,POBJECT_ATTRIBUTES supplied) {
    DIRECTORY_FN real=NATIVE(DIRECTORY_FN,"NtCreateDirectoryObject");if(!real)return UNSUPPORTED;
    if(!output)return real(output,access,supplied);
    DBC_REQUEST frame;handoff_frame(&frame,DBC_OP_DIRECTORY);
    if(!handoff_name(&frame,supplied))return real(output,access,supplied);
    __try {if(supplied->RootDirectory)return real(output,access,supplied);}__except(EXCEPTION_EXECUTE_HANDLER){return INVALID_INPUT;}
    TOKEN_BUFFER user,app,restricted;if(!current_context(GetCurrentProcessToken(),&user,&app,&restricted))return real(output,access,supplied);
    DWORD session=0;if(!ProcessIdToSessionId(GetCurrentProcessId(),&session))return INVALID_INPUT;
    WCHAR base[96],link[96];if(swprintf_s(base,96,L"\\Sessions\\%lu\\BaseNamedObjects",session)<0||swprintf_s(link,96,L"\\Sessions\\BNOLINKS\\%lu",session)<0)return INVALID_INPUT;
    BOOL alias=_wcsicmp(frame.name,L"\\BaseNamedObjects")==0||_wcsicmp(frame.name,base)==0||_wcsicmp(frame.name,link)==0;
    BOOL known=alias;
    const WCHAR *prefixes[]={L"\\BaseNamedObjects",base,link};
    for(int i=0;i<3;i++){size_t length=wcslen(prefixes[i]);if(_wcsnicmp(frame.name,prefixes[i],length)==0&&frame.name[length]==L'\\')known=TRUE;}
    if(!known)return real(output,access,supplied);
    frame.args[0]=access;frame.args[1]=supplied->Attributes;frame.caller=(ULONG_PTR)_ReturnAddress();
    ULONG_PTR response=DbcBrokerCall(DBC_OP_DIRECTORY,&frame,sizeof(frame));if((LONG_PTR)response<0)return (NTSTATUS)response;
    if(!response)return INVALID_INPUT;
    HANDLE root=(HANDLE)response;
    if(alias){__try{*output=root;return 0;}__except(EXCEPTION_EXECUTE_HANDLER){CloseHandle(root);return INVALID_INPUT;}}
    WCHAR *relative=wcsrchr(frame.name,L'\\');if(!relative||!relative[1]){CloseHandle(root);return INVALID_INPUT;}
    UNICODE_STRING name;name.Buffer=relative+1;name.Length=(USHORT)(wcslen(name.Buffer)*2);name.MaximumLength=name.Length+2;
    OBJECT_ATTRIBUTES attrs=*supplied;attrs.RootDirectory=root;attrs.ObjectName=&name;
    PRIVATE_REQUEST request;int accepted=private_request_policy(&attrs,&request,FALSE,FALSE);NTSTATUS status=INVALID_INPUT;
    if(accepted==1){status=real(output,access,&request.attributes);CloseHandle(request.rootCopy);}CloseHandle(root);return status;
}
NTSTATUS NTAPI DbcNtCreateSection(PHANDLE output,ACCESS_MASK access,POBJECT_ATTRIBUTES supplied,PLARGE_INTEGER maximum,ULONG protection,ULONG allocation,HANDLE file) {
    SECTION_FN real=NATIVE(SECTION_FN,"NtCreateSection");if(!real)return UNSUPPORTED;
    if(file||protection!=PAGE_READWRITE||allocation!=SEC_COMMIT)return real(output,access,supplied,maximum,protection,allocation,file);
    DBC_REQUEST frame;handoff_frame(&frame,DBC_OP_SECTION);if(!handoff_name(&frame,supplied))return real(output,access,supplied,maximum,protection,allocation,file);
    LONGLONG size=0;__try{if(!maximum)return real(output,access,supplied,maximum,protection,allocation,file);size=maximum->QuadPart;}__except(EXCEPTION_EXECUTE_HANDLER){return INVALID_INPUT;}
    if(size<=0||size>1024*1024)return real(output,access,supplied,maximum,protection,allocation,file);
    PRIVATE_REQUEST request;int accepted=private_request_policy(supplied,&request,FALSE,FALSE);
    if(accepted!=1)return real(output,access,supplied,maximum,protection,allocation,file);
    frame.args[0]=(ULONG_PTR)request.rootCopy;frame.args[1]=(ULONG_PTR)size;frame.args[2]=protection;frame.args[3]=allocation;frame.caller=(ULONG_PTR)_ReturnAddress();
    ULONG_PTR response=DbcBrokerCall(DBC_OP_SECTION,&frame,sizeof(frame));
    NTSTATUS status=(LONG_PTR)response<0?(NTSTATUS)response:real(output,access,response==1?supplied:&request.attributes,maximum,protection,allocation,file);
    CloseHandle(request.rootCopy);return status;
}
