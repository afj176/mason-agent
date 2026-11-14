#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <stdint.h>
#include "CodeBrokerAbi.h"

/* Only the staged unsigned MSYS image imports this facade. VirtualAlloc and
 * RtlRestoreContext use bounded first-party calls; other names forward. */
static volatile LONG receiptPid;
static PVOID continuationRegistration;
VOID WINAPI DbcRtlRestoreContext(PCONTEXT context,PEXCEPTION_RECORD exception);
static void preserve_context(PCONTEXT context,ULONG_PTR caller) {
    DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_RESTORE_CONTEXT;
    request.args[0]=(ULONG_PTR)context;request.args[1]=(ULONG_PTR)&DbcRtlRestoreContext;request.caller=caller;
    if(DbcBrokerCall(DBC_OP_RESTORE_CONTEXT,&request,sizeof(request))!=0)__fastfail(FAST_FAIL_FATAL_APP_EXIT);
}
static LONG CALLBACK DbcContinueHandler(PEXCEPTION_POINTERS exception) {
    if(exception&&exception->ContextRecord)preserve_context(exception->ContextRecord,(ULONG_PTR)&DbcContinueHandler);
    return EXCEPTION_CONTINUE_SEARCH;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) {
    (void)instance;(void)reserved;
    if(reason==DLL_PROCESS_ATTACH){continuationRegistration=AddVectoredContinueHandler(1,DbcContinueHandler);return continuationRegistration!=NULL;}
    if(reason==DLL_PROCESS_DETACH&&continuationRegistration){RemoveVectoredContinueHandler(continuationRegistration);continuationRegistration=NULL;}
    return TRUE;
}
VOID WINAPI DbcRtlRestoreContext(PCONTEXT context,PEXCEPTION_RECORD exception) {
    preserve_context(context,(ULONG_PTR)_ReturnAddress());
    RtlRestoreContext(context,exception);
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}
LPVOID WINAPI DbcVirtualAlloc(LPVOID address,SIZE_T size,DWORD allocation,DWORD protection) {
    DWORD before=GetLastError();
    if(address==(void*)(uintptr_t)0xa00000000ULL&&allocation==MEM_RESERVE) {
        DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_USER_HEAP;
        request.args[0]=(ULONG_PTR)address;request.args[1]=size;request.args[2]=allocation;request.args[3]=protection;request.caller=(ULONG_PTR)_ReturnAddress();
        ULONG_PTR result=DbcBrokerCall(DBC_OP_USER_HEAP,&request,sizeof(request));
        if(result!=(ULONG_PTR)address)__fastfail(FAST_FAIL_FATAL_APP_EXIT);
        SetLastError(before);return address;
    }
    if(address==(void*)(uintptr_t)0x800000000ULL&&size==(SIZE_T)0x200000000ULL&&allocation==MEM_RESERVE&&protection==PAGE_NOACCESS) {
        DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_ADOPT;
        request.args[0]=(ULONG_PTR)address;request.args[1]=size;request.args[2]=allocation;request.args[3]=protection;request.caller=(ULONG_PTR)_ReturnAddress();
        ULONG_PTR result=DbcBrokerCall(DBC_OP_ADOPT,&request,sizeof(request));
        if(result!=(ULONG_PTR)address)__fastfail(FAST_FAIL_FATAL_APP_EXIT);
        InterlockedExchange(&receiptPid,(LONG)GetCurrentProcessId());
        SetLastError(before);return address;
    }
    SetLastError(before);void *result=VirtualAlloc(address,size,allocation,protection);DWORD error=GetLastError();
    if(address==(void*)(uintptr_t)0x800000000ULL&&allocation==MEM_COMMIT&&protection==PAGE_READWRITE&&InterlockedCompareExchange(&receiptPid,0,(LONG)GetCurrentProcessId())==(LONG)GetCurrentProcessId()) {
        DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_COMMIT;
        request.args[0]=(ULONG_PTR)address;request.args[1]=size;request.args[2]=allocation;request.args[3]=protection;request.args[4]=(ULONG_PTR)result;request.caller=(ULONG_PTR)_ReturnAddress();
        if((LONG_PTR)DbcBrokerCall(DBC_OP_COMMIT,&request,sizeof(request))<0)__fastfail(FAST_FAIL_FATAL_APP_EXIT);
    }
    SetLastError(error);return result;
}
