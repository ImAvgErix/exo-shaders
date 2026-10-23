; Export stubs of the preloader (preload.cpp): stub sN jumps through g_real[N]. Each is 6 bytes padded to 8, so DllMain
; finds a stub's slot from its address; preload_<name>.def maps the export names onto them.
EXTERN g_real:QWORD

STUB MACRO k
    ALIGN 8
    s&k PROC
        jmp QWORD PTR [g_real + k * 8]
    s&k ENDP
ENDM

.code
i = 0
REPT 256
    STUB %i
    i = i + 1
ENDM
END
