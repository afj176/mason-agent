#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "CodeBrokerAbi.h"

/* Internal, restricted root only. This does not implement Win32 creation or
 * accept host authority; the original API executes under our LPAC token. */
int wmain(int argc,WCHAR **argv) {
    if(argc!=3||!argv[1][0]||wcslen(argv[1])>1000||!argv[2][0]||wcslen(argv[2])>32766)return 2;
    DBC_REQUEST request={0};request.version=DBC_ABI_VERSION;request.size=sizeof(request);request.operation=DBC_OP_READY;
    if((LONG_PTR)DbcBrokerCall(DBC_OP_READY,&request,sizeof(request))<0)return 3;
    WCHAR *command=_wcsdup(argv[2]);if(!command)return 4;
    STARTUPINFOW inherited={0},startup={0};PROCESS_INFORMATION child={0};inherited.cb=sizeof(inherited);GetStartupInfoW(&inherited);
    startup.cb=sizeof(startup);startup.lpDesktop=inherited.lpDesktop;startup.dwFlags=STARTF_USESTDHANDLES;
    startup.hStdInput=GetStdHandle(STD_INPUT_HANDLE);startup.hStdOutput=GetStdHandle(STD_OUTPUT_HANDLE);startup.hStdError=GetStdHandle(STD_ERROR_HANDLE);
    BOOL created=CreateProcessW(argv[1],command,NULL,NULL,TRUE,CREATE_NO_WINDOW,NULL,NULL,&startup,&child);DWORD error=GetLastError();free(command);
    if(!created){fwprintf(stderr,L"Restricted Bash creation failed (%lu).\n",error);return 5;}
    CloseHandle(child.hThread);DWORD exit=1;
    if(WaitForSingleObject(child.hProcess,INFINITE)!=WAIT_OBJECT_0||!GetExitCodeProcess(child.hProcess,&exit)){CloseHandle(child.hProcess);return 6;}
    CloseHandle(child.hProcess);return (int)exit;
}
