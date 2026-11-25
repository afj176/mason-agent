; Intentional first-party call boundary. No vendor code is patched. CALL and
; RET execute normally, retaining the ordinary and CET shadow-stack pairing.
option casemap:none
.code
PUBLIC DbcBrokerCall
DbcBrokerCall PROC
    int 3
    ret
DbcBrokerCall ENDP
END
