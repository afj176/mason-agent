#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "../../src/Mason.Code.Native/CodeBrokerAbi.h"

/* QA DLL loaded by the installed, admitted MSYS Perl DynaLoader. */
typedef ULONG_PTR (__cdecl *BROKER_CALL)(ULONG,const DBC_REQUEST *,SIZE_T);
typedef char context_layout_must_match[(sizeof(CONTEXT)==1232)?1:-1];
static ULONG_PTR symbol(HMODULE module,const char *name) { FARPROC entry=GetProcAddress(module,name);ULONG_PTR address=0;memcpy(&address,&entry,sizeof(address));return address; }
static void output(const char *text) { DWORD written=0;WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),text,(DWORD)strlen(text),&written,NULL); }
static LONG clear_debug(PEXCEPTION_POINTERS exception) {
    PCONTEXT context=exception->ContextRecord;context->Dr0=context->Dr1=context->Dr2=context->Dr3=context->Dr6=context->Dr7=0;context->ContextFlags|=CONTEXT_DEBUG_REGISTERS;
    return EXCEPTION_CONTINUE_EXECUTION;
}
typedef struct {HANDLE mainThread,clearReady,clearDone,verifyReady,verifyDone;volatile LONG cleared,restored,failed;} LIVE_CONTEXT;
static BOOL inspect_main(LIVE_CONTEXT *state,BOOL clear) {
    DWORD before=SuspendThread(state->mainThread);if(before==(DWORD)-1)return FALSE;
    CONTEXT context={0};context.ContextFlags=CONTEXT_DEBUG_REGISTERS;BOOL success=GetThreadContext(state->mainThread,&context);
    if(success&&clear){context.Dr0=context.Dr1=context.Dr2=context.Dr3=context.Dr6=context.Dr7=0;success=SetThreadContext(state->mainThread,&context);context.ContextFlags=CONTEXT_DEBUG_REGISTERS;if(success)success=GetThreadContext(state->mainThread,&context);if(success)InterlockedExchange(&state->cleared,(context.Dr7&0x55)==0);}
    else if(success){HMODULE native=GetModuleHandleW(L"ntdll.dll");InterlockedExchange(&state->restored,(context.Dr7&0x55)==0x55&&context.Dr0==symbol(native,"NtCreateUserProcess")&&context.Dr1==symbol(native,"NtCreateNamedPipeFile")&&context.Dr2==symbol(native,"NtCreateFile")&&context.Dr3==symbol(native,"NtOpenFile"));}
    if(ResumeThread(state->mainThread)!=before+1)success=FALSE;return success;
}
static DWORD WINAPI context_worker(LPVOID argument) {
    LIVE_CONTEXT *state=(LIVE_CONTEXT *)argument;
    if(WaitForSingleObject(state->clearReady,15000)!=WAIT_OBJECT_0||!inspect_main(state,TRUE))InterlockedExchange(&state->failed,1);SetEvent(state->clearDone);
    if(WaitForSingleObject(state->verifyReady,15000)!=WAIT_OBJECT_0||!inspect_main(state,FALSE))InterlockedExchange(&state->failed,1);SetEvent(state->verifyDone);return 0;
}
static int probe(const char *mode) {
    HMODULE helper=GetModuleHandleW(L"dbcw.dll"),facade=GetModuleHandleW(L"dbck.dll"),msys=GetModuleHandleW(L"msys-2.0.dll"),native=GetModuleHandleW(L"ntdll.dll");
    ULONG_PTR entry=symbol(helper,"DbcBrokerCall");BROKER_CALL call=NULL;memcpy(&call,&entry,sizeof(call));
    if(!entry||!facade||!msys)return 70;
    if(strcmp(mode,"continuation")==0) {
        LIVE_CONTEXT state={0};
        if(!DuplicateHandle(GetCurrentProcess(),GetCurrentThread(),GetCurrentProcess(),&state.mainThread,THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,0))return 82;
        state.clearReady=CreateEventW(NULL,TRUE,FALSE,NULL);state.clearDone=CreateEventW(NULL,TRUE,FALSE,NULL);state.verifyReady=CreateEventW(NULL,TRUE,FALSE,NULL);state.verifyDone=CreateEventW(NULL,TRUE,FALSE,NULL);
        if(!state.clearReady||!state.clearDone||!state.verifyReady||!state.verifyDone)return 83;
        HANDLE worker=CreateThread(NULL,0,context_worker,&state,0,NULL);if(!worker)return 84;
        SetEvent(state.clearReady);if(WaitForSingleObject(state.clearDone,15000)!=WAIT_OBJECT_0||!state.cleared||state.failed){output("FAIL live debug clearing could not be verified\n");return 85;}
        __try{RaiseException(0xe0424354,0,0,NULL);}__except(clear_debug(GetExceptionInformation())){}
        SetEvent(state.verifyReady);if(WaitForSingleObject(state.verifyDone,15000)!=WAIT_OBJECT_0||WaitForSingleObject(worker,15000)!=WAIT_OBJECT_0||state.failed||!state.restored){output("FAIL live canonical debug restoration was not observed\n");return 86;}
        CloseHandle(worker);CloseHandle(state.mainThread);CloseHandle(state.clearReady);CloseHandle(state.clearDone);CloseHandle(state.verifyReady);CloseHandle(state.verifyDone);
        HANDLE reader=NULL,writer=NULL;DWORD written=0,read=0;char value=0;
        if(!CreatePipe(&reader,&writer,NULL,0)){output("FAIL pipe after cleared debug continuation\n");return 80;}
        BOOL success=WriteFile(writer,"q",1,&written,NULL)&&written==1&&ReadFile(reader,&value,1,&read,NULL)&&read==1&&value=='q';
        CloseHandle(reader);CloseHandle(writer);if(!success)return 81;
        output("context-continuation-repaired-ok\n");return 0;
    }
    BOOL mapped=strcmp(mode,"mapped")==0;CONTEXT *context=NULL;
    if(mapped) {
        HANDLE file=CreateFileW(L"context-canary.bin",GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);
        if(file==INVALID_HANDLE_VALUE)return 71;
        HANDLE mapping=CreateFileMappingW(file,NULL,PAGE_READWRITE,0,sizeof(CONTEXT),NULL);CloseHandle(file);
        if(!mapping)return 72;context=(CONTEXT *)MapViewOfFile(mapping,FILE_MAP_WRITE|FILE_MAP_READ,0,0,sizeof(CONTEXT));CloseHandle(mapping);
    } else context=(CONTEXT *)VirtualAlloc(NULL,sizeof(CONTEXT),MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    if(!context)return 73;memset(context,0x41,sizeof(*context));context->ContextFlags=CONTEXT_FULL;
    if(mapped&&!FlushViewOfFile(context,sizeof(*context)))return 74;
    CONTEXT expected=*context;expected.ContextFlags|=CONTEXT_DEBUG_REGISTERS;
    expected.Dr0=symbol(native,"NtCreateUserProcess");expected.Dr1=symbol(native,"NtCreateNamedPipeFile");expected.Dr2=symbol(native,"NtCreateFile");expected.Dr3=symbol(native,"NtOpenFile");expected.Dr6=0;expected.Dr7=0x55;
    DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_RESTORE_CONTEXT;
    request.args[0]=(ULONG_PTR)context;request.args[1]=symbol(facade,"RtlRestoreContext");request.caller=symbol(msys,"cygwin_internal");
    if(!request.args[1]||!request.caller)return 75;
    if(strcmp(mode,"readonly")==0){DWORD previous=0;if(!VirtualProtect(context,sizeof(*context),PAGE_READONLY,&previous))return 76;}
    if(strcmp(mode,"wrong-facade")==0)request.args[1]=symbol(native,"RtlRestoreContext");
    if(strcmp(mode,"bad-flags")==0)context->ContextFlags=0;
    if(call(DBC_OP_RESTORE_CONTEXT,&request,sizeof(request))!=0)return 77;
    if(strcmp(mode,"positive")!=0){output("FAIL malformed context request resumed\n");return 78;}
    if(memcmp(context,&expected,sizeof(expected))!=0){output("FAIL context changed outside canonical debug fields\n");return 79;}
    VirtualFree(context,0,MEM_RELEASE);output("context-canonical-only-ok\n");return 0;
}
static const char *selectedMode="positive";
__declspec(dllexport) void __cdecl ContextProbeRun(void *interpreter,void *cv) {(void)interpreter;(void)cv;ExitProcess((UINT)probe(selectedMode));}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) {
    (void)reserved;if(reason!=DLL_PROCESS_ATTACH)return TRUE;
    WCHAR path[MAX_PATH];DWORD length=GetModuleFileNameW(instance,path,MAX_PATH);if(!length||length>=MAX_PATH)return FALSE;
    selectedMode=wcsstr(path,L"-continuation.dll")?"continuation":wcsstr(path,L"-mapped.dll")?"mapped":wcsstr(path,L"-readonly.dll")?"readonly":wcsstr(path,L"-wrong-facade.dll")?"wrong-facade":wcsstr(path,L"-bad-flags.dll")?"bad-flags":"positive";
    return TRUE;
}
