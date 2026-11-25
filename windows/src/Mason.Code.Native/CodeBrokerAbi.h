#pragma once
#include <windows.h>
#include <stdint.h>

/* Closed first-party compatibility ABI. The desktop treats every byte as
 * untrusted; these operation numbers confer no authority by themselves. */
#define DBC_ABI_VERSION 3
#define DBC_OP_DIRECTORY 1
#define DBC_OP_SECTION 2
#define DBC_OP_ADOPT 3
#define DBC_OP_COMMIT 4
#define DBC_OP_READY 5
#define DBC_OP_USER_HEAP 6
#define DBC_OP_RESTORE_CONTEXT 7
typedef struct {
    ULONG version,size,operation,nameBytes;
    ULONG_PTR args[5];
    ULONG_PTR caller;
    WCHAR name[221];
} DBC_REQUEST;
typedef char DBC_REQUEST_must_be_512_bytes[(sizeof(DBC_REQUEST)==512)?1:-1];
#ifdef DBC_BUILD_HELPER
ULONG_PTR __cdecl DbcBrokerCall(ULONG operation,const DBC_REQUEST *request,SIZE_T size);
#else
__declspec(dllimport) ULONG_PTR __cdecl DbcBrokerCall(ULONG operation,const DBC_REQUEST *request,SIZE_T size);
#endif
