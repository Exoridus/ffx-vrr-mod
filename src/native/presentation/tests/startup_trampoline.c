#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <intrin.h>
#include "MinHook.h"

#if !defined(_M_IX86)
#error Startup trampoline probe requires MSVC x86.
#endif

typedef void (__cdecl *entry_fn)(void);
typedef int (__cdecl *notify_fn)(int phase);
static unsigned char* entry_stub;
static entry_fn entry_original;
static entry_fn cookie_original;
static notify_fn notify;
static unsigned int cookie_count, continuation_count, boot_count, event_count;
static unsigned int patch_marker, continuation_flags;
static unsigned int events[16];

__declspec(dllexport) void __cdecl startup_probe_mark(int value) {
    if (event_count < 16) events[event_count++] = (unsigned int)value;
    if (value == 4) patch_marker = 1;
}

__declspec(noinline) static void __cdecl cookie_dummy(void) {
    ++cookie_count;
    startup_probe_mark(2);
}

__declspec(noinline) static void __cdecl continuation(void) {
    __declspec(align(16)) unsigned char state[512];
    ++continuation_count;
    startup_probe_mark(6);
    _fxsave(state);
    continuation_flags = (unsigned int)notify(2);
    _fxrstor(state);
}

__declspec(noinline) static void __cdecl stage1_entry(void) {
    __declspec(align(16)) unsigned char state[512];
    ++boot_count;
    startup_probe_mark(1);
    _fxsave(state);
    notify(1);
    _fxrstor(state);
    entry_original();
}

__declspec(dllexport) uintptr_t __cdecl startup_probe_get(int field) {
    switch (field) {
        case 0: return (uintptr_t)entry_stub;
        case 1: return (uintptr_t)entry_original;
        case 2: return (uintptr_t)cookie_dummy;
        case 3: return (uintptr_t)cookie_original;
        case 4: return continuation_count;
        case 5: return cookie_count;
        case 6: return boot_count;
        case 7: return event_count;
        case 8: return patch_marker;
        case 9: return continuation_flags;
        default: return field >= 100 && field < 116 ? events[field - 100] : 0;
    }
}

__declspec(dllexport) int __cdecl startup_probe_stop(void) {
    int ok = MH_DisableHook(MH_ALL_HOOKS) == MH_OK;
    ok = MH_Uninitialize() == MH_OK && ok;
    if (entry_stub != NULL) {
        ok = VirtualFree(entry_stub, 0, MEM_RELEASE) != 0 && ok;
        entry_stub = NULL;
    }
    return ok;
}

__declspec(dllexport) int __cdecl startup_probe_setup(entry_fn callee_detour, notify_fn observer) {
    int32_t displacement;
    if (callee_detour == NULL || observer == NULL || entry_stub != NULL) return 0;
    if (MH_Initialize() != MH_OK) return 0;
    notify = observer;
    entry_stub = (unsigned char*)VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    if (entry_stub == NULL) { MH_Uninitialize(); return 0; }
    memset(entry_stub, 0x90, 4096);
    entry_stub[0] = 0xe8;
    displacement = (int32_t)((uintptr_t)cookie_dummy - (uintptr_t)(entry_stub + 5));
    memcpy(entry_stub + 1, &displacement, 4);
    entry_stub[5] = 0xe9;
    displacement = (int32_t)((uintptr_t)continuation - (uintptr_t)(entry_stub + 10));
    memcpy(entry_stub + 6, &displacement, 4);
    if (!FlushInstructionCache(GetCurrentProcess(), entry_stub, 10)
        || MH_CreateHook(entry_stub, stage1_entry, (void**)&entry_original) != MH_OK
        || MH_CreateHook(cookie_dummy, callee_detour, (void**)&cookie_original) != MH_OK
        || MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
        startup_probe_stop();
        return 0;
    }
    return 1;
}
