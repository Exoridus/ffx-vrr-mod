#include <windows.h>
#include <stdint.h>
#include <intrin.h>

#if !defined(_M_IX86)
#error fhpresent requires MSVC x86.
#endif

typedef void (__cdecl *dispatch_fn)(uintptr_t return_address, uintptr_t return_slot);
static void* volatile bound_dispatch;

int __cdecl fhpresent_abi(void) { return 1; }

int __cdecl fhpresent_bind(dispatch_fn dispatch) {
    void* previous;
    if (dispatch == NULL) return 0;
    previous = InterlockedCompareExchangePointer(&bound_dispatch, (void*)dispatch, NULL);
    return previous == NULL || previous == (void*)dispatch;
}

__declspec(noinline) void __cdecl fhpresent_entry(void) {
    uintptr_t address = (uintptr_t)_ReturnAddress();
    uintptr_t slot = (uintptr_t)_AddressOfReturnAddress();
    __declspec(align(16)) unsigned char state[512];
    dispatch_fn dispatch;
    _fxsave(state);
    dispatch = (dispatch_fn)InterlockedCompareExchangePointer(&bound_dispatch, NULL, NULL);
    if (dispatch != NULL) {
        dispatch(address, slot);
    }
    _fxrstor(state);
}
