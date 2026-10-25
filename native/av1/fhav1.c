#include <dav1d/dav1d.h>
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <errno.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <emmintrin.h>

typedef struct {
    uint32_t unused[9];
    uint8_t *planes[4];
    int strides[4];
} NativeImage;

typedef struct Packet {
    struct Packet *next;
    unsigned size;
    uint8_t data[];
} Packet;

typedef struct {
    Dav1dContext *decoder;
    Dav1dPicture picture;
    AVCodecContext *hardware;
    AVFrame *gpu, *cpu;
    Packet *pending, *tail;
    size_t pending_bytes;
    NativeImage image;
    uint8_t *pixels;
    int width, height, threads, mode, error, draining;
    unsigned frames;
} Decoder;

static void clear_pending(Decoder *d) {
    while (d->pending) {
        Packet *next = d->pending->next;
        free(d->pending); d->pending = next;
    }
    d->tail = NULL; d->pending_bytes = 0;
}

__declspec(dllexport) void __cdecl fh_av1_close(Decoder *d) {
    if (!d) return;
    av_frame_free(&d->gpu); av_frame_free(&d->cpu);
    avcodec_free_context(&d->hardware);
    dav1d_picture_unref(&d->picture);
    if (d->decoder) dav1d_close(&d->decoder);
    clear_pending(d); free(d->pixels); free(d);
}

static int software_open(Decoder *d) {
    av_frame_free(&d->gpu); av_frame_free(&d->cpu);
    avcodec_free_context(&d->hardware);
    Dav1dSettings settings;
    dav1d_default_settings(&settings);
    settings.n_threads = d->threads;
    settings.max_frame_delay = 0;
    settings.frame_size_limit = 3840u * 2160u;
    return dav1d_open(&d->decoder, &settings);
}

static enum AVPixelFormat hardware_format(AVCodecContext *context, const enum AVPixelFormat *formats) {
#ifdef FH_AV1_TEST_REJECT_FORMAT
    return AV_PIX_FMT_NONE;
#else
    Decoder *d = context->opaque;
    if (context->width != d->width || context->height != d->height) return AV_PIX_FMT_NONE;
    for (; *formats != AV_PIX_FMT_NONE; ++formats)
        if (*formats == AV_PIX_FMT_D3D11) return *formats;
    return AV_PIX_FMT_NONE;
#endif
}

static int hardware_open(Decoder *d) {
#ifdef FH_AV1_TEST_NO_DEVICE
    return -ENOSYS;
#endif
    const AVCodec *codec = avcodec_find_decoder_by_name("av1");
    if (!codec) return -ENOSYS;
    d->hardware = avcodec_alloc_context3(codec);
    d->gpu = av_frame_alloc(); d->cpu = av_frame_alloc();
    if (!d->hardware || !d->gpu || !d->cpu) return -ENOMEM;
    int result = av_hwdevice_ctx_create(&d->hardware->hw_device_ctx, AV_HWDEVICE_TYPE_D3D11VA, NULL, NULL, 0);
    if (result < 0) return result;
    d->hardware->opaque = d;
    d->hardware->get_format = hardware_format;
    d->hardware->width = d->width; d->hardware->height = d->height;
    d->hardware->thread_count = 1;
    return avcodec_open2(d->hardware, codec, NULL);
}

/* mode: 0 automatic, 1 software, 2 require hardware (native verification). */
__declspec(dllexport) Decoder *__cdecl fh_av1_open(int width, int height, int threads, int mode) {
    if (width < 2 || height < 2 || width > 3840 || height > 2160 || (width & 1) || (height & 1)
        || threads < 1 || threads > 8 || mode < 0 || mode > 2) return NULL;
    Decoder *d = calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->width = width; d->height = height; d->threads = threads; d->mode = mode;
    d->pixels = malloc((size_t)width * height * 3 / 2);
    if (!d->pixels) { fh_av1_close(d); return NULL; }
    if (mode == 1 || hardware_open(d) < 0) {
        if (mode == 2 || software_open(d) < 0) { fh_av1_close(d); return NULL; }
    }
    d->image.planes[0] = d->pixels;
    d->image.planes[1] = d->pixels + (size_t)width * height;
    d->image.planes[2] = d->image.planes[1] + (size_t)width * height / 4;
    d->image.strides[0] = width;
    d->image.strides[1] = d->image.strides[2] = width / 2;
    return d;
}

static int remember_packet(Decoder *d, const uint8_t *bytes, unsigned size) {
    if (size > 64u * 1024u * 1024u - d->pending_bytes) return -ENOMEM;
    Packet *packet = malloc(sizeof(*packet) + size);
    if (!packet) return -ENOMEM;
    packet->next = NULL; packet->size = size; memcpy(packet->data, bytes, size);
    if (d->tail) d->tail->next = packet; else d->pending = packet;
    d->tail = packet; d->pending_bytes += size;
    return 0;
}

static int software_send(Decoder *d, const uint8_t *bytes, unsigned size) {
    Dav1dData data = {0};
    uint8_t *copy = dav1d_data_create(&data, size);
    if (!copy) return -ENOMEM;
    memcpy(copy, bytes, size);
    int result = dav1d_send_data(d->decoder, &data);
    dav1d_data_unref(&data);
    return result;
}

__declspec(dllexport) int __cdecl fh_av1_decode(Decoder *d, const uint8_t *bytes, unsigned size) {
    if (!d || !bytes || !size || size > 64u * 1024u * 1024u || d->draining) return -EINVAL;
    if (d->error) return d->error;
    int result;
    if (d->hardware) {
        AVPacket *packet = av_packet_alloc();
        if (!packet) return d->error = -ENOMEM;
        result = av_new_packet(packet, (int)size);
        if (result >= 0) {
            memcpy(packet->data, bytes, size);
            result = avcodec_send_packet(d->hardware, packet);
        }
        av_packet_free(&packet);
        if (result != AVERROR(EAGAIN) && !d->frames) {
            int saved = remember_packet(d, bytes, size);
            if (saved < 0) return d->error = saved;
            if (result < 0 && d->mode == 0 && software_open(d) == 0) result = 0;
        }
    } else {
        dav1d_picture_unref(&d->picture);
        result = d->pending ? -EAGAIN : software_send(d, bytes, size);
    }
    d->error = result == -EAGAIN ? 0 : result;
    return result;
}

static int software_picture(Decoder *d) {
    dav1d_picture_unref(&d->picture);
    int result;
    for (;;) {
        result = dav1d_get_picture(d->decoder, &d->picture);
        if (result != -EAGAIN || !d->pending) break;
        Packet *packet = d->pending;
        result = software_send(d, packet->data, packet->size);
        if (result < 0) return result;
        d->pending = packet->next;
        d->pending_bytes -= packet->size; free(packet);
        if (!d->pending) d->tail = NULL;
    }
    if (result < 0) return result;
    Dav1dPicture *p = &d->picture;
    if (p->p.w != d->width || p->p.h != d->height || p->p.layout != DAV1D_PIXEL_LAYOUT_I420
        || (p->p.bpc != 8 && p->p.bpc != 10)) return -EINVAL;
    for (int plane = 0; plane < 3; plane++) {
        int width = plane ? d->width / 2 : d->width;
        int height = plane ? d->height / 2 : d->height;
        for (int y = 0; y < height; y++) {
            const uint8_t *source = (const uint8_t *)p->data[plane] + y * p->stride[plane != 0];
            uint8_t *target = d->image.planes[plane] + y * width;
            if (p->p.bpc == 8) memcpy(target, source, width);
            else for (int x = 0; x < width; x++) target[x] = (uint8_t)(((const uint16_t *)source)[x] >> 2);
        }
    }
    return 0;
}

static int hardware_picture(Decoder *d) {
    av_frame_unref(d->gpu); av_frame_unref(d->cpu);
    int result = avcodec_receive_frame(d->hardware, d->gpu);
    if (result < 0) return result;
    if (d->gpu->format != AV_PIX_FMT_D3D11 || d->gpu->width != d->width || d->gpu->height != d->height) return -EINVAL;
    result = av_hwframe_transfer_data(d->cpu, d->gpu, 0);
    if (result < 0) return result;
    AVFrame *p = d->cpu;
    if (p->width != d->width || p->height != d->height
        || (p->format != AV_PIX_FMT_NV12 && p->format != AV_PIX_FMT_P010LE)) return -EINVAL;
    int wide = p->format == AV_PIX_FMT_P010LE;
    for (int y = 0; y < d->height; ++y) {
        const uint8_t *source = p->data[0] + y * p->linesize[0];
        uint8_t *target = d->pixels + y * d->width;
        if (!wide) memcpy(target, source, d->width);
        else {
            int x = 0;
            for (; x + 16 <= d->width; x += 16) {
                __m128i a = _mm_srli_epi16(_mm_loadu_si128((const __m128i *)(source + 2*x)), 8);
                __m128i b = _mm_srli_epi16(_mm_loadu_si128((const __m128i *)(source + 2*x + 16)), 8);
                _mm_storeu_si128((__m128i *)(target + x), _mm_packus_epi16(a, b));
            }
            for (; x < d->width; ++x) target[x] = (uint8_t)(((const uint16_t *)source)[x] >> 8);
        }
    }
    for (int y = 0; y < d->height / 2; ++y) {
        const uint8_t *source = p->data[1] + y * p->linesize[1];
        int x = 0;
        uint8_t *u = d->image.planes[1] + y * (d->width / 2);
        uint8_t *v = d->image.planes[2] + y * (d->width / 2);
        __m128i mask = _mm_set1_epi16(255), zero = _mm_setzero_si128();
        for (; x + 8 <= d->width / 2; x += 8) {
            __m128i pairs;
            if (wide) {
                __m128i a = _mm_srli_epi16(_mm_loadu_si128((const __m128i *)(source + 4*x)), 8);
                __m128i b = _mm_srli_epi16(_mm_loadu_si128((const __m128i *)(source + 4*x + 16)), 8);
                pairs = _mm_packus_epi16(a, b);
            } else pairs = _mm_loadu_si128((const __m128i *)(source + 2*x));
            _mm_storel_epi64((__m128i *)(u + x), _mm_packus_epi16(_mm_and_si128(pairs, mask), zero));
            _mm_storel_epi64((__m128i *)(v + x), _mm_packus_epi16(_mm_srli_epi16(pairs, 8), zero));
        }
        for (; x < d->width / 2; ++x) {
            d->image.planes[1][y * (d->width / 2) + x] = wide ? (uint8_t)(((const uint16_t *)source)[2*x] >> 8) : source[2*x];
            d->image.planes[2][y * (d->width / 2) + x] = wide ? (uint8_t)(((const uint16_t *)source)[2*x+1] >> 8) : source[2*x+1];
        }
    }
    return 0;
}

__declspec(dllexport) NativeImage *__cdecl fh_av1_get_frame(Decoder *d) {
    if (!d || d->error) return NULL;
    int result = d->hardware ? hardware_picture(d) : software_picture(d);
    if (result < 0 && result != -EAGAIN && result != AVERROR_EOF && d->hardware && !d->frames && d->mode == 0) {
        /* Replay only before output; replaying later would duplicate presented frames. */
        result = software_open(d);
        if (result >= 0) result = software_picture(d);
    }
    if (result == -EAGAIN || result == AVERROR_EOF) return NULL;
    if (result < 0) { d->error = result; return NULL; }
    if (d->hardware) clear_pending(d);
    d->frames++;
    return &d->image;
}

__declspec(dllexport) int __cdecl fh_av1_flush(Decoder *d) {
    if (!d) return -EINVAL;
    if (d->error) return d->error;
    if (d->draining) return 0;
    int result = d->hardware ? avcodec_send_packet(d->hardware, NULL) : 0;
    if (result == AVERROR_EOF) result = 0;
    if (!result) d->draining = 1;
    d->error = result == -EAGAIN ? 0 : result;
    return result;
}

__declspec(dllexport) int __cdecl fh_av1_error(Decoder *d) { return d ? d->error : -EINVAL; }
__declspec(dllexport) unsigned __cdecl fh_av1_frames(Decoder *d) { return d ? d->frames : 0; }
__declspec(dllexport) unsigned __cdecl fh_av1_backend(Decoder *d) { return d && d->hardware ? 1 : 0; }
__declspec(dllexport) unsigned __cdecl fh_av1_abi(void) { return sizeof(void *) == 4 ? 2 : 0; }
