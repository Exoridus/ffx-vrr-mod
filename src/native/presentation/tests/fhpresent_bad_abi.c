#include <stdint.h>
__declspec(dllexport) int __cdecl fhpresent_abi(void) { return 2; }
__declspec(dllexport) int __cdecl fhpresent_bind(void* callback) { (void)callback; return 1; }
__declspec(dllexport) void __cdecl fhpresent_entry(void) {}
__declspec(dllexport) int __cdecl fhpresent_startup_bind(void* callback) { (void)callback; return 1; }
__declspec(dllexport) void __cdecl fhpresent_startup_entry(void) {}
__declspec(dllexport) int __cdecl fhpresent_pad_bind(void* callback) { (void)callback; return 1; }
__declspec(dllexport) void __cdecl fhpresent_pad_entry(void) {}
