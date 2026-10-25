#include <windows.h>
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stddef.h>

typedef struct { uint32_t unused[9]; uint8_t *planes[4]; int strides[4]; } NativeImage;
int main(int argc, char **argv) {
    assert(argc == 4);
    char library_path[32768];
    assert(GetFullPathNameA(argv[1], sizeof(library_path), library_path, NULL) < sizeof(library_path));
    HMODULE library = LoadLibraryExA(library_path, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    assert(library);
    unsigned (__cdecl *backend)(void *) = (void *)GetProcAddress(library, "fh_av1_backend");
    unsigned (__cdecl *abi)(void) = (void *)GetProcAddress(library, "fh_av1_abi");
    void *(__cdecl *open)(int, int, int, int) = (void *)GetProcAddress(library, "fh_av1_open");
    int (__cdecl *decode)(void *, const uint8_t *, unsigned) = (void *)GetProcAddress(library, "fh_av1_decode");
    NativeImage *(__cdecl *picture)(void *) = (void *)GetProcAddress(library, "fh_av1_get_frame");
    int (__cdecl *error)(void *) = (void *)GetProcAddress(library, "fh_av1_error");
    int (__cdecl *flush)(void *) = (void *)GetProcAddress(library, "fh_av1_flush");
    void (__cdecl *close)(void *) = (void *)GetProcAddress(library, "fh_av1_close");
    assert(backend && abi && abi() == 2 && open && decode && picture && error && flush && close);
    assert(offsetof(NativeImage, planes) == 0x24 && offsetof(NativeImage, strides) == 0x34);
    assert(!open(3841, 2160, 8, 0) && !open(1920, 1079, 8, 0));
    FILE *file = fopen(argv[2], "rb");
    assert(file);
    uint8_t header[32], frame[12];
    assert(fread(header, 1, 32, file) == 32 && !memcmp(header, "DKIF", 4) && !memcmp(header + 8, "AV01", 4));
    unsigned width = header[12] | header[13] << 8, height = header[14] | header[15] << 8;
    int mode = atoi(argv[3]);
    int mismatch = !strcmp(argv[3], "mismatch");
    void *decoder = open(width - (mismatch ? 2 : 0), height, 8, mode);
    assert(decoder);
    assert(decode(decoder, header, 0) == -22);
    assert(decode(decoder, header, 64u * 1024u * 1024u + 1) == -22);
    unsigned packets = 0, images = 0;
    uint64_t hash = 14695981039346656037ull;
    LARGE_INTEGER begin, end, frequency;
    QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&begin);
    for (;;) {
        size_t got = fread(frame, 1, 12, file);
        assert(got == 0 || got == 12);
        if (!got) break;
        unsigned size = (unsigned)frame[0] | (unsigned)frame[1] << 8 | (unsigned)frame[2] << 16 | (unsigned)frame[3] << 24;
        assert(size && size <= 64u * 1024u * 1024u);
        uint8_t *data = malloc(size);
        assert(data && fread(data, 1, size, file) == size);
        int result;
        do {
            result = decode(decoder, data, size);
            assert(result == 0 || result == -11);
            NativeImage *image;
            while ((image = picture(decoder)) != NULL) {
                assert(image->strides[0] == (int)width && image->strides[1] == (int)width / 2);
                for (size_t i = 0; i < (size_t)width * height * 3 / 2; i += 4096) hash = (hash ^ image->planes[0][i]) * 1099511628211ull;
                images++;
            }
            if (mismatch && error(decoder)) {
                assert(!images);
                printf("{\"dimension_mismatch_rejected\":true}\n");
                free(data); close(decoder); fclose(file); FreeLibrary(library);
                return 0;
            }
            assert(!error(decoder));
        } while (result == -11);
        free(data); packets++;
    }
    int result;
    do {
        result = flush(decoder);
        assert(result == 0 || result == -11);
        NativeImage *image;
        while ((image = picture(decoder)) != NULL) {
            for (size_t i = 0; i < (size_t)width * height * 3 / 2; i += 4096) hash = (hash ^ image->planes[0][i]) * 1099511628211ull;
            images++;
        }
        assert(!error(decoder));
    } while (result == -11);
    assert(feof(file) && packets == 480 && images == 480);
    assert(!picture(decoder) && !error(decoder));
    unsigned selected = backend(decoder);
    assert(mode != 1 || selected == 0);
    assert(mode != 2 || selected == 1);
    assert(strcmp(argv[3], "fallback") || selected == 0);
    QueryPerformanceCounter(&end);
    printf("{\"packets\":%u,\"frames\":%u,\"backend\":%u,\"seconds\":%.3f,\"pixel_hash\":\"%016llx\"}\n",
        packets, images, selected, (double)(end.QuadPart - begin.QuadPart) / frequency.QuadPart, (unsigned long long)hash);
    double elapsed = (double)(end.QuadPart - begin.QuadPart) / frequency.QuadPart;
    assert(mode != 2 || width != 3840 || elapsed < 8.008);
    close(decoder); fclose(file); FreeLibrary(library);
    return 0;
}
