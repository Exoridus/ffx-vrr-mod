#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <intrin.h>

#define CHECK(value) do { if (!(value)) { fprintf(stderr, "CHECK failed: %s (%s:%d)\n", #value, __FILE__, __LINE__); return 1; } } while (0)
typedef void (__cdecl *entry_fn)(void);
typedef void (__cdecl *dispatch_fn)(uintptr_t, uintptr_t);
typedef int (__cdecl *bind_fn)(dispatch_fn);
static uintptr_t expected_return;
static uintptr_t saved_sp;
static int registers_ok;
static int callback_ok;
static int callback_count;
static int startup_count;
static unsigned int original_control;

__declspec(dllexport) uintptr_t __cdecl fhpresent_test_expected_return(void) { return expected_return; }

__declspec(dllexport) unsigned int __cdecl fhpresent_test_read_control(void) {
    unsigned short control;
    unsigned int mxcsr;
    __asm fnstcw control
    __asm stmxcsr mxcsr
    return ((unsigned int)control << 16) | (mxcsr & 0xffff);
}

__declspec(dllexport) void __cdecl fhpresent_test_original(void) { original_control = fhpresent_test_read_control(); }
__declspec(dllexport) unsigned int __cdecl fhpresent_test_original_control(void) { return original_control; }

__declspec(naked) static int __cdecl invoke_registers(entry_fn entry) {
    __asm {
        mov eax, [esp + 4]
        push ebp
        push ebx
        push esi
        push edi
        mov saved_sp, esp
        mov ebp, 012345678h
        mov ebx, 023456789h
        mov esi, 03456789ah
        mov edi, 0456789abh
        lea edx, resumed
        mov expected_return, edx
        call eax
    resumed:
        mov registers_ok, 0
        cmp esp, saved_sp
        jne finished
        cmp ebp, 012345678h
        jne finished
        cmp ebx, 023456789h
        jne finished
        cmp esi, 03456789ah
        jne finished
        cmp edi, 0456789abh
        jne finished
        mov registers_ok, 1
    finished:
        mov esp, saved_sp
        pop edi
        pop esi
        pop ebx
        pop ebp
        mov eax, registers_ok
        ret
    }
}

__declspec(dllexport) int __cdecl fhpresent_test_invoke(entry_fn entry) {
    __declspec(align(16)) unsigned char original[512], before[512], after[512];
    unsigned short control = 0x067f;
    unsigned int mxcsr = 0x3f80;
    int ok, i;
    _fxsave(original);
    __asm {
        fninit
        fldcw control
        fldpi
        fld1
        ldmxcsr mxcsr
        pcmpeqd xmm0, xmm0
        pcmpeqd xmm1, xmm1
        pcmpeqd xmm2, xmm2
        pcmpeqd xmm3, xmm3
        pcmpeqd xmm4, xmm4
        pcmpeqd xmm5, xmm5
        pcmpeqd xmm6, xmm6
        pcmpeqd xmm7, xmm7
    }
    _fxsave(before);
    ok = invoke_registers(entry);
    _fxsave(after);
    _fxrstor(original);
    ok = ok && memcmp(before, after, 8) == 0 && memcmp(before + 24, after + 24, 4) == 0
        && memcmp(before + 160, after + 160, 128) == 0;
    for (i = 0; i < 8; ++i) ok = ok && memcmp(before + 32 + i * 16, after + 32 + i * 16, 10) == 0;
    return ok;
}

static void __cdecl callback(uintptr_t address, uintptr_t slot) {
    unsigned int mxcsr = 0x1f80;
    callback_count++;
    callback_ok = address == expected_return && slot != 0 && *(uintptr_t*)slot == address;
    __asm {
        fninit
        ldmxcsr mxcsr
        fldz
        pxor xmm0, xmm0
        pxor xmm1, xmm1
        pxor xmm2, xmm2
        pxor xmm3, xmm3
        pxor xmm4, xmm4
        pxor xmm5, xmm5
        pxor xmm6, xmm6
        pxor xmm7, xmm7
    }
}
static void __cdecl startup_callback(uintptr_t address, uintptr_t slot) {
    ++startup_count;
    callback(address, slot);
}
static void __cdecl other_callback(uintptr_t address, uintptr_t slot) { (void)address; (void)slot; }

#ifndef FHPRESENT_PROBE
int main(int argc, char** argv) {
    HMODULE library;
    int (__cdecl *abi)(void);
    bind_fn bind, startup_bind;
    entry_fn entry, startup_entry;
    CHECK(argc == 2);
    library = LoadLibraryA(argv[1]);
    CHECK(library != NULL);
    abi = (int (__cdecl *)(void))GetProcAddress(library, "fhpresent_abi");
    bind = (bind_fn)GetProcAddress(library, "fhpresent_bind");
    entry = (entry_fn)GetProcAddress(library, "fhpresent_entry");
    startup_bind = (bind_fn)GetProcAddress(library, "fhpresent_startup_bind");
    startup_entry = (entry_fn)GetProcAddress(library, "fhpresent_startup_entry");
    CHECK(abi && bind && entry && startup_bind && startup_entry);
    CHECK(entry != startup_entry);
    CHECK(abi() == 2);
    CHECK(fhpresent_test_invoke(entry));
    CHECK(fhpresent_test_invoke(startup_entry));
    CHECK(callback_count == 0 && startup_count == 0);
    CHECK(bind(NULL) == 0);
    CHECK(bind(callback) == 1);
    CHECK(bind(callback) == 1);
    CHECK(bind(other_callback) == 0);
    CHECK(fhpresent_test_invoke(entry));
    CHECK(callback_ok && callback_count == 1 && startup_count == 0);
    CHECK(fhpresent_test_invoke(startup_entry));
    CHECK(callback_count == 1 && startup_count == 0);
    CHECK(startup_bind(NULL) == 0);
    CHECK(startup_bind(startup_callback) == 1);
    CHECK(startup_bind(startup_callback) == 1);
    CHECK(startup_bind(callback) == 0);
    CHECK(startup_bind(other_callback) == 0);
    CHECK(fhpresent_test_invoke(startup_entry));
    CHECK(callback_ok && callback_count == 2 && startup_count == 1);
    CHECK(fhpresent_test_invoke(entry));
    CHECK(callback_ok && callback_count == 3 && startup_count == 1);
    puts("fhpresent native caller: capture, binding, ESP, nonvolatile registers, x87 and SIMD passed.");
    return 0;
}
#endif
