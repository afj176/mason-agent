#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <stdio.h>
#include <stdint.h>
#include <wchar.h>
#include <string.h>
#include <aclapi.h>
#include <stddef.h>
typedef char sdk_process_header_is_256[(sizeof(SYSTEM_PROCESS_INFORMATION)==256)?1:-1];
typedef char sdk_process_id_is_80[(offsetof(SYSTEM_PROCESS_INFORMATION,UniqueProcessId)==80)?1:-1];
typedef char sdk_thread_record_is_80[(sizeof(SYSTEM_THREAD_INFORMATION)==80)?1:-1];
typedef char sdk_thread_owner_is_40[(offsetof(SYSTEM_THREAD_INFORMATION,ClientId)==40)?1:-1];

/* First-party QA actor. All created threads run existing functions in this
 * same restricted child; no code injection or executable allocation. */
typedef struct { volatile LONG64 normal,bypass,hidden,originallySuspended;volatile LONG stop; } COUNTERS;
typedef NTSTATUS (NTAPI *CREATE_THREAD)(HANDLE*,ACCESS_MASK,OBJECT_ATTRIBUTES*,HANDLE,LPTHREAD_START_ROUTINE,PVOID,ULONG,SIZE_T,SIZE_T,SIZE_T,PVOID);
static COUNTERS *counters;
static void *racePage;static HANDLE raceMapping;
static DWORD WINAPI worker(void *argument) {
    volatile LONG64 *value=(volatile LONG64*)argument;
    while(!counters->stop){
        if(value==&counters->hidden&&racePage) {
            if(!VirtualFree(racePage,0,MEM_RELEASE)||MapViewOfFileEx(raceMapping,FILE_MAP_READ,0,0,65536,racePage)!=racePage)return 8;
            InterlockedIncrement64(value);Sleep(1);
            if(!UnmapViewOfFile(racePage)||VirtualAlloc(racePage,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE)!=racePage)return 9;
        }
        InterlockedIncrement64(value);Sleep(1);
    }return 0;
}
static DWORD WINAPI presuspended(void *argument) { InterlockedIncrement64((volatile LONG64*)argument);return 0; }
static BOOL write_text(const WCHAR *path,const char *text) {
    HANDLE file=CreateFileW(path,GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file==INVALID_HANDLE_VALUE)return FALSE;DWORD length=(DWORD)strlen(text),done=0;BOOL ok=WriteFile(file,text,length,&done,NULL)&&done==length;CloseHandle(file);return ok;
}
int wmain(int argc,WCHAR **argv) {
    if(argc==2&&(!wcscmp(argv[1],L"actor")||!wcscmp(argv[1],L"actor-denied"))) {
        counters=VirtualAlloc(NULL,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!counters)return 2;
        HANDLE canary=CreateFileW(L"quiescence-mapped-canary.bin",GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
        if(canary==INVALID_HANDLE_VALUE)return 2;
        static BYTE initial[65536];memset(initial,0x41,sizeof(initial));DWORD written=0;
        if(!WriteFile(canary,initial,sizeof(initial),&written,NULL)||written!=sizeof(initial))return 2;
        raceMapping=CreateFileMappingW(canary,NULL,PAGE_READONLY,0,65536,NULL);CloseHandle(canary);
        racePage=VirtualAlloc(NULL,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(!raceMapping||!racePage)return 2;
#pragma warning(push)
#pragma warning(disable:4191)
        CREATE_THREAD create=(CREATE_THREAD)GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtCreateThreadEx");
#pragma warning(pop)
        if(!create)return 2;
        HANDLE threads[4]={0};threads[0]=CreateThread(NULL,0,worker,(void*)&counters->normal,0,NULL);
        if(!threads[0]||create(&threads[1],THREAD_ALL_ACCESS,NULL,GetCurrentProcess(),worker,(void*)&counters->bypass,0x40,0,0,0,NULL)<0
            ||create(&threads[2],THREAD_ALL_ACCESS,NULL,GetCurrentProcess(),worker,(void*)&counters->hidden,0x44,0,0,0,NULL)<0)return 3;
        threads[3]=CreateThread(NULL,0,presuspended,(void*)&counters->originallySuspended,CREATE_SUSPENDED,NULL);if(!threads[3])return 3;
        if(!wcscmp(argv[1],L"actor-denied")) {
            PACL oldAcl=NULL,denied=NULL;PSECURITY_DESCRIPTOR descriptor=NULL;PSID everyone=NULL;SID_IDENTIFIER_AUTHORITY world=SECURITY_WORLD_SID_AUTHORITY;
            if(GetSecurityInfo(threads[2],SE_KERNEL_OBJECT,DACL_SECURITY_INFORMATION,NULL,NULL,&oldAcl,NULL,&descriptor)!=ERROR_SUCCESS
                ||!AllocateAndInitializeSid(&world,1,SECURITY_WORLD_RID,0,0,0,0,0,0,0,&everyone))return 3;
            EXPLICIT_ACCESSW rule={0};rule.grfAccessPermissions=THREAD_SUSPEND_RESUME;rule.grfAccessMode=DENY_ACCESS;rule.Trustee.TrusteeForm=TRUSTEE_IS_SID;rule.Trustee.TrusteeType=TRUSTEE_IS_WELL_KNOWN_GROUP;rule.Trustee.ptstrName=(LPWSTR)everyone;
            if(SetEntriesInAclW(1,&rule,oldAcl,&denied)!=ERROR_SUCCESS||SetSecurityInfo(threads[2],SE_KERNEL_OBJECT,DACL_SECURITY_INFORMATION,NULL,NULL,denied,NULL)!=ERROR_SUCCESS)return 3;
            LocalFree(denied);LocalFree(descriptor);FreeSid(everyone);
        }
        char ready[512];if(sprintf_s(ready,sizeof(ready),"%lu %llu %lu %llu",GetCurrentProcessId(),(unsigned long long)(uintptr_t)counters,GetThreadId(threads[3]),(unsigned long long)(uintptr_t)racePage)<0||!write_text(L"quiescence-ready.txt",ready))return 4;
        ULONGLONG deadline=GetTickCount64()+60000;
        while(GetFileAttributesW(L"quiescence-stop.txt")==INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline)Sleep(5);
        InterlockedExchange(&counters->stop,1);ResumeThread(threads[3]);
        if(WaitForMultipleObjects(4,threads,TRUE,5000)!=WAIT_OBJECT_0)return 5;
        for(int i=0;i<4;i++)CloseHandle(threads[i]);VirtualFree(counters,0,MEM_RELEASE);VirtualFree(racePage,0,MEM_RELEASE);CloseHandle(raceMapping);return 0;
    }
    BOOL deny=argc==2&&!wcscmp(argv[1],L"denied"),cancel=argc==2&&!wcscmp(argv[1],L"cancel");if(argc!=1&&!deny&&!cancel)return 2;
    WCHAR exe[4096],command[8192];DWORD size=GetModuleFileNameW(NULL,exe,4096);if(!size||size>=4096)return 2;
    if(swprintf_s(command,8192,L"\"%s\" %s",exe,deny?L"actor-denied":L"actor")<0)return 2;
    STARTUPINFOW startup={0};PROCESS_INFORMATION child={0};startup.cb=sizeof(startup);startup.dwFlags=STARTF_USESTDHANDLES;
    startup.hStdInput=GetStdHandle(STD_INPUT_HANDLE);startup.hStdOutput=GetStdHandle(STD_OUTPUT_HANDLE);startup.hStdError=GetStdHandle(STD_ERROR_HANDLE);
    if(!CreateProcessW(exe,command,NULL,NULL,TRUE,CREATE_NO_WINDOW,NULL,NULL,&startup,&child))return 3;CloseHandle(child.hThread);
    ULONGLONG deadline=GetTickCount64()+10000;
    while(GetFileAttributesW(L"quiescence-ready.txt")==INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline)Sleep(5);
    FILE *ready=NULL;if(_wfopen_s(&ready,L"quiescence-ready.txt",L"rt")||!ready)return 4;
    unsigned long pid=0,originalTid=0;unsigned long long address=0,race=0;int fields=fscanf_s(ready,"%lu %llu %lu %llu",&pid,&address,&originalTid,&race);fclose(ready);if(fields!=4||pid!=child.dwProcessId)return 4;
    ULONG_PTR request[4]={pid,(ULONG_PTR)address,originalTid,(ULONG_PTR)race};RaiseException(deny?0xe0424351:cancel?0xe0424352:0xe0424350,0,4,request);
    if(!write_text(L"quiescence-stop.txt","stop"))return 5;
    DWORD exit=1;if(WaitForSingleObject(child.hProcess,5000)!=WAIT_OBJECT_0||!GetExitCodeProcess(child.hProcess,&exit))return 5;CloseHandle(child.hProcess);
    printf("{\"quiescenceActorExit\":%lu}\n",exit);return exit==0?0:6;
}
