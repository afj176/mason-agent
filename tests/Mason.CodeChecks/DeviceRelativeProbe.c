#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <stdio.h>
#include <stdint.h>
#include <wchar.h>

/* QA only: an ordinary permanently restricted actor exercises the fixed
 * cryptography capability. Baseline versus candidate evidence depends on
 * the selected host build; this fixture implements no host mapped-memory
 * write, ACL update or compatibility fallback. */
typedef NTSTATUS (NTAPI *OPEN_FILE)(HANDLE*,ACCESS_MASK,POBJECT_ATTRIBUTES,PIO_STATUS_BLOCK,ULONG,ULONG);
typedef NTSTATUS (NTAPI *CREATE_FILE)(HANDLE*,ACCESS_MASK,POBJECT_ATTRIBUTES,PIO_STATUS_BLOCK,PLARGE_INTEGER,ULONG,ULONG,ULONG,ULONG,PVOID,ULONG);
typedef NTSTATUS (NTAPI *QUERY_OBJECT)(HANDLE,ULONG,PVOID,ULONG,PULONG);
typedef NTSTATUS (NTAPI *DUPLICATE_OBJECT)(HANDLE,HANDLE,HANDLE,HANDLE*,ACCESS_MASK,ULONG,ULONG);
typedef BOOL (WINAPI *COMPARE_OBJECTS)(HANDLE,HANDLE);
static HANDLE imageOutput=(HANDLE)(uintptr_t)1;
static HANDLE imageDuplicate=(HANDLE)(uintptr_t)1;
static IO_STATUS_BLOCK imageStatus;
static BOOL fixed_name(QUERY_OBJECT query,HANDLE file) {
    __declspec(align(8)) BYTE buffer[512]={0};ULONG required=0;
    if(query(file,1,buffer,sizeof(buffer),&required)<0)return FALSE;
    UNICODE_STRING *name=(UNICODE_STRING*)buffer;const WCHAR expected[]=L"\\Device\\KsecDD";
    return name->Length==sizeof(expected)-sizeof(WCHAR)&&name->Buffer&&(BYTE*)name->Buffer>=buffer&&(BYTE*)name->Buffer+name->Length<=buffer+sizeof(buffer)&&memcmp(name->Buffer,expected,name->Length)==0;
}
static BOOL fixed_prefix(QUERY_OBJECT query,HANDLE file) {
    __declspec(align(8)) BYTE buffer[2048]={0};ULONG required=0;
    if(query(file,1,buffer,sizeof(buffer),&required)<0)return FALSE;
    UNICODE_STRING *name=(UNICODE_STRING*)buffer;const WCHAR expected[]=L"\\Device\\KsecDD";USHORT length=sizeof(expected)-sizeof(WCHAR);
    return name->Length>=length&&name->Buffer&&(BYTE*)name->Buffer>=buffer&&(BYTE*)name->Buffer+name->Length<=buffer+sizeof(buffer)&&memcmp(name->Buffer,expected,length)==0&&(name->Length==length||name->Buffer[length/sizeof(WCHAR)]==L'\\');
}
int wmain(int argc,WCHAR **argv) {
    UNREFERENCED_PARAMETER(argc);UNREFERENCED_PARAMETER(argv);
#pragma warning(push)
#pragma warning(disable:4191)
    HMODULE native=GetModuleHandleW(L"ntdll.dll");
    OPEN_FILE open=(OPEN_FILE)GetProcAddress(native,"NtOpenFile");QUERY_OBJECT query=(QUERY_OBJECT)GetProcAddress(native,"NtQueryObject");
    CREATE_FILE create=(CREATE_FILE)GetProcAddress(native,"NtCreateFile");
    DUPLICATE_OBJECT duplicate=(DUPLICATE_OBJECT)GetProcAddress(native,"NtDuplicateObject");
    COMPARE_OBJECTS compare=(COMPARE_OBJECTS)GetProcAddress(GetModuleHandleW(L"kernelbase.dll"),"CompareObjectHandles");
#pragma warning(pop)
    if(!open||!create||!query||!duplicate||!compare)return 2;
    /* Exercise the existing module-load rearming path before the capability
     * request. This pure-C fixture otherwise restores its startup context
     * after the initial debugger stop without loading another DLL. */
    if(!LoadLibraryW(L"version.dll"))return 6;
    WCHAR device[]=L"\\Device\\KsecDD",empty[]=L"";
    UNICODE_STRING name={(USHORT)(sizeof(device)-sizeof(WCHAR)),sizeof(device),device};
    OBJECT_ATTRIBUTES attrs={0};attrs.Length=sizeof(attrs);attrs.Attributes=0x40;attrs.ObjectName=&name;
    HANDLE root=NULL;IO_STATUS_BLOCK status={0};NTSTATUS first=create(&root,0x100003,&attrs,&status,NULL,0,3,1,0x20,NULL,0);
    if(first<0||!root||!fixed_name(query,root)){printf("{\"privateOpen\":\"%08lx\",\"sourceAvailable\":false}\n",(ULONG)first);return 3;}
    BYTE basic[56]={0};ULONG required=0;NTSTATUS sourceQuery=query(root,0,basic,sizeof(basic),&required);ULONG sourceRights=0;memcpy(&sourceRights,basic+4,4);
    IO_STATUS_BLOCK bridgeStatus={0};
    NTSTATUS bridge=create(&imageOutput,0x100003,&attrs,&bridgeStatus,NULL,0,3,1,0x20,NULL,0);
    ULONG bridgeRights=0;BOOL bridgeSame=FALSE;
    if(bridge>=0&&imageOutput){ZeroMemory(basic,sizeof(basic));if(query(imageOutput,0,basic,sizeof(basic),&required)>=0)memcpy(&bridgeRights,basic+4,4);bridgeSame=compare(root,imageOutput);CloseHandle(imageOutput);}
    printf("{\"absoluteBridgeIntoImage\":\"%08lx\",\"sameFileObject\":%s,\"rights\":%lu,\"ioStatus\":\"%08lx\"}\n",(ULONG)bridge,bridgeSame?"true":"false",bridgeRights,(ULONG)bridgeStatus.Status);
    if(bridge<0||!bridgeSame||bridgeRights!=0x100003||bridgeStatus.Status!=bridge)return 7;
    HANDLE *readOnly=(HANDLE*)VirtualAlloc(NULL,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READONLY);if(!readOnly)return 8;
    IO_STATUS_BLOCK invalidIo={0};NTSTATUS invalidOutput=create(readOnly,0x100003,&attrs,&invalidIo,NULL,0,3,1,0x20,NULL,0);
    BOOL untouched=*readOnly==NULL;VirtualFree(readOnly,0,MEM_RELEASE);
    printf("{\"readOnlyOutputStatus\":\"%08lx\",\"untouched\":%s,\"ioStatus\":\"%08lx\"}\n",(ULONG)invalidOutput,untouched?"true":"false",(ULONG)invalidIo.Status);
    if(invalidOutput>=0||!untouched||invalidIo.Status!=invalidOutput)return 9;
    MEMORY_BASIC_INFORMATION outputMemory={0};if(VirtualQuery(&imageOutput,&outputMemory,sizeof(outputMemory))!=sizeof(outputMemory))return 4;
    name=(UNICODE_STRING){0,sizeof(empty),empty};attrs.RootDirectory=root;
    NTSTATUS relative=open(&imageOutput,0x100001,&attrs,&imageStatus,3,0x20);
    BOOL sameDevice=relative>=0&&imageOutput&&fixed_name(query,imageOutput);ULONG resultRights=0;
    if(relative>=0&&imageOutput){ZeroMemory(basic,sizeof(basic));if(query(imageOutput,0,basic,sizeof(basic),&required)>=0)memcpy(&resultRights,basic+4,4);CloseHandle(imageOutput);}
    printf("{\"privateOpen\":\"%08lx\",\"sourceQuery\":\"%08lx\",\"sourceRights\":%lu,\"relativeOpen\":\"%08lx\",\"nativeIoStatus\":\"%08lx\",\"imageType\":%lu,\"imageProtection\":%lu,\"sameDeviceName\":%s,\"resultRights\":%lu}\n",(ULONG)first,(ULONG)sourceQuery,sourceRights,(ULONG)relative,(ULONG)imageStatus.Status,outputMemory.Type,outputMemory.Protect,sameDevice?"true":"false",resultRights);
    NTSTATUS duplication=duplicate(GetCurrentProcess(),root,GetCurrentProcess(),&imageDuplicate,0x100003,0,0);ULONG duplicatedRights=0;BOOL sameObject=FALSE;
    if(duplication>=0&&imageDuplicate){ZeroMemory(basic,sizeof(basic));if(query(imageDuplicate,0,basic,sizeof(basic),&required)>=0)memcpy(&duplicatedRights,basic+4,4);sameObject=compare(root,imageDuplicate);CloseHandle(imageDuplicate);}
    printf("{\"actorSelfDuplicateStatus\":\"%08lx\",\"sameFileObject\":%s,\"duplicatedRights\":%lu,\"outputImageType\":%lu,\"outputImageProtection\":%lu}\n",(ULONG)duplication,sameObject?"true":"false",duplicatedRights,outputMemory.Type,outputMemory.Protect);
    ACCESS_MASK requests[]={READ_CONTROL,WRITE_DAC,WRITE_OWNER,GENERIC_ALL,MAXIMUM_ALLOWED};
    for(size_t i=0;i<sizeof(requests)/sizeof(requests[0]);i++) {
        HANDLE candidate=NULL;IO_STATUS_BLOCK io={0};NTSTATUS result=open(&candidate,requests[i],&attrs,&io,3,0);
        ULONG granted=0;if(result>=0&&candidate){ZeroMemory(basic,sizeof(basic));if(query(candidate,0,basic,sizeof(basic),&required)>=0)memcpy(&granted,basic+4,4);}
        printf("{\"restrictedActorAccessProbe\":%lu,\"status\":\"%08lx\",\"granted\":%s,\"grantedMask\":%lu}\n",requests[i],(ULONG)result,result>=0?"true":"false",granted);if(result>=0&&candidate)CloseHandle(candidate);
    }
    const WCHAR *unrelated[]={L"..\\..\\Device\\Null",L"\\Device\\Null",L"\\??\\C:\\Windows\\win.ini"};
    for(size_t i=0;i<sizeof(unrelated)/sizeof(unrelated[0]);i++) {
        name.Buffer=(WCHAR*)unrelated[i];name.Length=(USHORT)(wcslen(name.Buffer)*sizeof(WCHAR));name.MaximumLength=name.Length+sizeof(WCHAR);
        HANDLE candidate=NULL;IO_STATUS_BLOCK io={0};NTSTATUS result=open(&candidate,0x100001,&attrs,&io,3,0x20);
        printf("{\"unrelatedNameProbe\":%llu,\"status\":\"%08lx\",\"succeeded\":%s,\"remainsFixedDevice\":%s,\"fileType\":%lu}\n",(unsigned long long)i,(ULONG)result,result>=0?"true":"false",result>=0&&fixed_prefix(query,candidate)?"true":"false",result>=0?GetFileType(candidate):0);if(result>=0&&candidate)CloseHandle(candidate);
    }
    WCHAR outside[4096],ntOutside[4100];DWORD full=GetFullPathNameW(L"..\\device-relative-canary.txt",4096,outside,NULL);if(!full||full>=4096||swprintf_s(ntOutside,4100,L"\\??\\%s",outside)<0)return 5;
    name.Buffer=ntOutside;name.Length=(USHORT)(wcslen(ntOutside)*sizeof(WCHAR));name.MaximumLength=name.Length+sizeof(WCHAR);
    HANDLE candidate=NULL;IO_STATUS_BLOCK io={0};attrs.RootDirectory=NULL;
    NTSTATUS control=open(&candidate,0x100001,&attrs,&io,3,0x20);if(control>=0&&candidate)CloseHandle(candidate);
    attrs.RootDirectory=root;candidate=NULL;NTSTATUS relativeCanary=open(&candidate,0x100001,&attrs,&io,3,0x20);
    BOOL remains=relativeCanary>=0&&fixed_prefix(query,candidate),leaked=FALSE;DWORD fileType=0;
    if(relativeCanary>=0&&candidate){char bytes[64]={0};DWORD got=0;fileType=GetFileType(candidate);BOOL read=ReadFile(candidate,bytes,63,&got,NULL);leaked=read&&got>=25&&memcmp(bytes,"device-relative-sentinel",24)==0;CloseHandle(candidate);}
    printf("{\"outsideCanaryControl\":\"%08lx\",\"relativeCanaryStatus\":\"%08lx\",\"relativeRemainsFixedDevice\":%s,\"relativeFileType\":%lu,\"sentinelExposed\":%s}\n",(ULONG)control,(ULONG)relativeCanary,remains?"true":"false",fileType,leaked?"true":"false");
    CloseHandle(root);return 0;
}
