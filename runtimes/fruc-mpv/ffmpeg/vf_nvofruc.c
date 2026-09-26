/*
 * NVIDIA Optical Flow FRUC frame-rate up-conversion for D3D11 frames.
 *
 * This file is part of DemiMedia's experimental Generated Motion runtime and is
 * built into FFmpeg (LGPL-2.1-or-later, like the surrounding libavfilter code).
 *
 * The NVIDIA library (NvOFFRUC.dll) is loaded at run time from a caller-supplied
 * path and is never linked or redistributed. Its C ABI is declared below from the
 * Optical Flow SDK 5.0 documentation; NVIDIA's own header is C++-only.
 *
 * Frames stay on the GPU: decoded NV12 is converted to RGBA by the D3D11 video
 * processor (FRUC's NV12 output corrupts chroma on SDK 5.0.7), FRUC writes an RGBA
 * intermediate frame, and the video processor converts it back to NV12. Source
 * frames are copied unchanged. Any failure switches the filter to passthrough so
 * playback continues; every output frame reports what it is in frame metadata.
 */

#include <windows.h>
#include <initguid.h>
#include <d3d11_4.h>
#include <stdbool.h>
#include <stdint.h>

#include "libavutil/avstring.h"
#include "libavutil/hwcontext.h"
#include "libavutil/hwcontext_d3d11va.h"
#include "libavutil/mem.h"
#include "libavutil/opt.h"
#include "libavutil/pixdesc.h"
#include "libavutil/thread.h"
#include "libavutil/wchar_filename.h"

#include "filters.h"
#include "video.h"

/* ---- NvOFFRUC C ABI (Optical Flow SDK 5.0) ---- */
enum { FRUC_SUCCESS = 0 };
enum { FRUC_RES_DIRECTX11 = 1 };
enum { FRUC_SURFACE_ARGB = 1 };
enum { FRUC_CUDA_UNDEFINED = -1 };
#define FRUC_MAX_RESOURCE 10

typedef struct FrucCreateParam {
    uint32_t width, height;
    void *device;
    int32_t resource_type, surface_format, cuda_resource_type;
    uint32_t reserved[32];
} FrucCreateParam;

typedef struct FrucFrameData {
    void *frame;
    double timestamp;
    size_t cuda_pitch;
    bool *frame_repeated;
    uint32_t reserved[32];
} FrucFrameData;

typedef union FrucSync {
    struct { uint64_t value; } fence;
    struct { uint64_t render_key, interp_key; } mutex;
} FrucSync;

typedef struct FrucProcessIn {
    FrucFrameData input;
    uint32_t skip_warp : 1;
    FrucSync wait;
    uint32_t reserved[32];
} FrucProcessIn;

typedef struct FrucProcessOut {
    FrucFrameData output;
    FrucSync signal;
    uint32_t reserved[32];
} FrucProcessOut;

typedef struct FrucRegister {
    void *resources[FRUC_MAX_RESOURCE];
    void *fence;
    uint32_t count;
} FrucRegister;

typedef struct FrucOpaque *FrucHandle;
typedef int (__stdcall *FrucCreateFn)(const FrucCreateParam *, FrucHandle *);
typedef int (__stdcall *FrucRegisterFn)(FrucHandle, const FrucRegister *);
typedef int (__stdcall *FrucProcessFn)(FrucHandle, const FrucProcessIn *, const FrucProcessOut *);

/* ---- One FRUC instance per process ----
 * The SDK cannot register resources on a second instance created after Destroy in
 * the same process, and mpv rebuilds lavfi graphs on seek. The instance therefore
 * lives for the whole process and is shared by successive filter instances. */
typedef struct FrucShared {
    int created;              ///< 1 once creation succeeded, -1 once it failed
    char failure[128];
    HMODULE dll;
    FrucProcessFn process;
    FrucHandle handle;
    ID3D11Device *device;
    int width, height;
    ID3D11Texture2D *render[2], *interp;
    ID3D11Fence *fence;
    HANDLE event;
    uint64_t fence_value;
    int64_t generated, repeated, errors;
    volatile LONG64 fallbacks;  ///< sticky across seeks: times any stream fell back
    char last_fallback[128];
} FrucShared;

static FrucShared shared;
static AVMutex shared_lock = AV_MUTEX_INITIALIZER;

typedef struct NvOFrucContext {
    const AVClass *class;
    char *dll_path;
    AVRational fps;
    int simulate_failure;     ///< test hook: fail after this many source frames (0 = never)
    int fence_timeout_ms;

    AVBufferRef *device_ref;
    AVBufferRef *frames_out;
    AVD3D11VADeviceContext *hw;
    ID3D11VideoDevice *video_device;
    ID3D11VideoContext *video_context;
    ID3D11DeviceContext4 *context4;
    ID3D11VideoProcessorEnumerator *enumerator;
    ID3D11VideoProcessor *processor;
    int width, height;

    int passthrough;
    char state[160];
    int have_previous;
    int64_t previous_pts;     ///< input time base
    double anchor, previous_time;
    int64_t grid_index;
    double source_index;
    int64_t source_frames;
} NvOFrucContext;

#define RELEASE(p) do { if (p) { (p)->lpVtbl->Release(p); (p) = NULL; } } while (0)
/* The device lock is shared with the player's renderer. Hold it only around D3D11
 * context calls, never while waiting for FRUC, or presentation misses vsyncs. */
#define DEV_LOCK(s)   do { if ((s)->hw->lock) (s)->hw->lock((s)->hw->lock_ctx); } while (0)
#define DEV_UNLOCK(s) do { if ((s)->hw->unlock) (s)->hw->unlock((s)->hw->lock_ctx); } while (0)

static void go_passthrough(AVFilterContext *avctx, const char *reason)
{
    NvOFrucContext *s = avctx->priv;
    if (s->passthrough)
        return;
    s->passthrough = 1;
    snprintf(s->state, sizeof(s->state), "passthrough:%s", reason);
    InterlockedIncrement64(&shared.fallbacks);
    av_strlcpy(shared.last_fallback, reason, sizeof(shared.last_fallback));
    av_log(avctx, AV_LOG_WARNING, "Generated Motion disabled for this stream: %s\n", reason);
}

static int create_rgba(ID3D11Device *dev, int w, int h, UINT misc, ID3D11Texture2D **out)
{
    D3D11_TEXTURE2D_DESC d = { 0 };
    d.Width = w; d.Height = h; d.MipLevels = d.ArraySize = 1;
    d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    d.MiscFlags = misc;
    return FAILED(dev->lpVtbl->CreateTexture2D(dev, &d, NULL, out)) ? -1 : 0;
}

/* Called with shared_lock held. */
static void shared_create(NvOFrucContext *s, ID3D11Device5 *dev5)
{
    FrucCreateFn create;
    FrucRegisterFn reg;
    wchar_t *wpath = NULL;
    ID3D11Device *dev = s->hw->device;
    UINT shared_misc = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    FrucCreateParam cp = { 0 };
    FrucRegister rp = { 0 };
    int st;

    shared.created = -1;
    if (s->dll_path && s->dll_path[0]) {
        if (utf8towchar(s->dll_path, &wpath) < 0 || !wpath) {
            snprintf(shared.failure, sizeof(shared.failure), "invalid FRUC library path");
            return;
        }
    } else {
        /* A Windows path's drive colon collides with filter option syntax, so the
         * player's launcher normally supplies the path through the environment. */
        DWORD n = GetEnvironmentVariableW(L"DEMIMEDIA_NVOFFRUC_DLL", NULL, 0);
        if (n && (wpath = av_malloc(n * sizeof(wchar_t))) &&
            GetEnvironmentVariableW(L"DEMIMEDIA_NVOFFRUC_DLL", wpath, n) != n - 1)
            av_freep(&wpath);
    }
    /* Default: the runtime's own copy beside the player, with its CUDA runtime. */
    shared.dll = wpath ? LoadLibraryExW(wpath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH)
                       : LoadLibraryExW(L"NvOFFRUC.dll", NULL, LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    av_free(wpath);
    if (!shared.dll) {
        snprintf(shared.failure, sizeof(shared.failure), "FRUC library not loadable (%lu)", GetLastError());
        return;
    }
    create = (FrucCreateFn)GetProcAddress(shared.dll, "NvOFFRUCCreate");
    reg = (FrucRegisterFn)GetProcAddress(shared.dll, "NvOFFRUCRegisterResource");
    shared.process = (FrucProcessFn)GetProcAddress(shared.dll, "NvOFFRUCProcess");
    if (!create || !reg || !shared.process) {
        snprintf(shared.failure, sizeof(shared.failure), "FRUC entry points missing");
        return;
    }
    if (create_rgba(dev, s->width, s->height, shared_misc, &shared.render[0]) < 0 ||
        create_rgba(dev, s->width, s->height, shared_misc, &shared.render[1]) < 0 ||
        create_rgba(dev, s->width, s->height, shared_misc, &shared.interp) < 0 ||
        FAILED(dev5->lpVtbl->CreateFence(dev5, 0, D3D11_FENCE_FLAG_SHARED, &IID_ID3D11Fence, (void **)&shared.fence)) ||
        !(shared.event = CreateEventW(NULL, FALSE, FALSE, NULL))) {
        snprintf(shared.failure, sizeof(shared.failure), "GPU resources unavailable");
        return;
    }
    cp.width = s->width; cp.height = s->height; cp.device = dev5;
    cp.resource_type = FRUC_RES_DIRECTX11;
    cp.surface_format = FRUC_SURFACE_ARGB;
    cp.cuda_resource_type = FRUC_CUDA_UNDEFINED;
    if ((st = create(&cp, &shared.handle)) != FRUC_SUCCESS) {
        snprintf(shared.failure, sizeof(shared.failure), "FRUC create failed (%d)", st);
        return;
    }
    rp.resources[0] = shared.interp;
    rp.resources[1] = shared.render[0];
    rp.resources[2] = shared.render[1];
    rp.fence = shared.fence;
    rp.count = 3;
    if ((st = reg(shared.handle, &rp)) != FRUC_SUCCESS) {
        snprintf(shared.failure, sizeof(shared.failure), "FRUC resource registration failed (%d)", st);
        return;
    }
    shared.device = dev;
    shared.width = s->width;
    shared.height = s->height;
    shared.created = 1;
}

static int ensure_backend(AVFilterContext *avctx)
{
    NvOFrucContext *s = avctx->priv;
    ID3D11Device *dev = s->hw->device;
    ID3D11Device5 *dev5 = NULL;
    HRESULT hr;
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd = { 0 };

    if (FAILED(dev->lpVtbl->QueryInterface(dev, &IID_ID3D11Device5, (void **)&dev5)) ||
        FAILED(s->hw->device_context->lpVtbl->QueryInterface(s->hw->device_context, &IID_ID3D11DeviceContext4, (void **)&s->context4))) {
        RELEASE(dev5);
        go_passthrough(avctx, "D3D11 fences unavailable");
        return 0;
    }

    ff_mutex_lock(&shared_lock);
    if (!shared.created)
        shared_create(s, dev5);
    if (shared.created < 0)
        go_passthrough(avctx, shared.failure);
    else if (shared.device != dev || shared.width != s->width || shared.height != s->height)
        go_passthrough(avctx, "FRUC is bound to another device or size in this player process");
    ff_mutex_unlock(&shared_lock);
    RELEASE(dev5);
    if (s->passthrough)
        return 0;

    hr = dev->lpVtbl->QueryInterface(dev, &IID_ID3D11VideoDevice, (void **)&s->video_device);
    if (SUCCEEDED(hr))
        hr = s->hw->device_context->lpVtbl->QueryInterface(s->hw->device_context, &IID_ID3D11VideoContext, (void **)&s->video_context);
    cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    cd.InputWidth = cd.OutputWidth = s->width;
    cd.InputHeight = cd.OutputHeight = s->height;
    cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    if (SUCCEEDED(hr))
        hr = s->video_device->lpVtbl->CreateVideoProcessorEnumerator(s->video_device, &cd, &s->enumerator);
    if (SUCCEEDED(hr))
        hr = s->video_device->lpVtbl->CreateVideoProcessor(s->video_device, s->enumerator, 0, &s->processor);
    if (FAILED(hr)) {
        go_passthrough(avctx, "D3D11 video processor unavailable");
        return 0;
    }
    s->video_context->lpVtbl->VideoProcessorSetStreamAutoProcessingMode(s->video_context, s->processor, 0, FALSE);
    s->video_context->lpVtbl->VideoProcessorSetStreamFrameFormat(s->video_context, s->processor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    snprintf(s->state, sizeof(s->state), "active");
    return 0;
}

static D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv_space(const AVFrame *f)
{
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE cs = { 0 };
    cs.YCbCr_Matrix = !(f->colorspace == AVCOL_SPC_BT470BG || f->colorspace == AVCOL_SPC_SMPTE170M);
    cs.Nominal_Range = f->color_range == AVCOL_RANGE_JPEG ? D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255
                                                          : D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
    return cs;
}

/* One video-processor pass between a (possibly array) texture slice and another. */
static int blt(NvOFrucContext *s, ID3D11Texture2D *src, int src_slice, D3D11_VIDEO_PROCESSOR_COLOR_SPACE src_cs, ID3D11Texture2D *dst, int dst_slice,
               D3D11_VIDEO_PROCESSOR_COLOR_SPACE dst_cs)
{
    ID3D11VideoProcessorInputView *iv = NULL;
    ID3D11VideoProcessorOutputView *ov = NULL;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd = { 0 };
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd = { 0 };
    D3D11_VIDEO_PROCESSOR_STREAM stream = { 0 };
    RECT rect = { 0, 0, s->width, s->height };
    HRESULT hr;

    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ivd.Texture2D.ArraySlice = src_slice;
    ovd.ViewDimension = dst_slice >= 0 ? D3D11_VPOV_DIMENSION_TEXTURE2DARRAY : D3D11_VPOV_DIMENSION_TEXTURE2D;
    if (dst_slice >= 0) {
        ovd.Texture2DArray.FirstArraySlice = dst_slice;
        ovd.Texture2DArray.ArraySize = 1;
    }
    hr = s->video_device->lpVtbl->CreateVideoProcessorInputView(s->video_device, (ID3D11Resource *)src, s->enumerator, &ivd, &iv);
    if (SUCCEEDED(hr))
        hr = s->video_device->lpVtbl->CreateVideoProcessorOutputView(s->video_device, (ID3D11Resource *)dst, s->enumerator, &ovd, &ov);
    if (SUCCEEDED(hr)) {
        s->video_context->lpVtbl->VideoProcessorSetStreamColorSpace(s->video_context, s->processor, 0, &src_cs);
        s->video_context->lpVtbl->VideoProcessorSetOutputColorSpace(s->video_context, s->processor, &dst_cs);
        s->video_context->lpVtbl->VideoProcessorSetStreamSourceRect(s->video_context, s->processor, 0, TRUE, &rect);
        s->video_context->lpVtbl->VideoProcessorSetStreamDestRect(s->video_context, s->processor, 0, TRUE, &rect);
        s->video_context->lpVtbl->VideoProcessorSetOutputTargetRect(s->video_context, s->processor, TRUE, &rect);
        stream.Enable = TRUE;
        stream.pInputSurface = iv;
        hr = s->video_context->lpVtbl->VideoProcessorBlt(s->video_context, s->processor, ov, 0, 1, &stream);
    }
    RELEASE(iv);
    RELEASE(ov);
    return FAILED(hr) ? -1 : 0;
}

static void tag(AVFrame *f, const NvOFrucContext *s, const char *kind)
{
    char n[32];
    av_dict_set(&f->metadata, "lavfi.nvofruc.state", s->state, 0);
    av_dict_set(&f->metadata, "lavfi.nvofruc.frame", kind, 0);
    snprintf(n, sizeof(n), "%"PRId64, shared.generated); av_dict_set(&f->metadata, "lavfi.nvofruc.generated", n, 0);
    snprintf(n, sizeof(n), "%"PRId64, shared.repeated);  av_dict_set(&f->metadata, "lavfi.nvofruc.repeated", n, 0);
    snprintf(n, sizeof(n), "%"PRId64, shared.errors);    av_dict_set(&f->metadata, "lavfi.nvofruc.errors", n, 0);
    snprintf(n, sizeof(n), "%"PRId64, (int64_t)shared.fallbacks); av_dict_set(&f->metadata, "lavfi.nvofruc.fallbacks", n, 0);
    if (shared.fallbacks) av_dict_set(&f->metadata, "lavfi.nvofruc.last_fallback", shared.last_fallback, 0);
}

/* Returns a new NV12 pool frame holding the source slice unchanged, or NULL. */
static AVFrame *copy_source(NvOFrucContext *s, const AVFrame *in)
{
    AVFrame *out = av_frame_alloc();
    D3D11_BOX box = { 0, 0, 0, (UINT)s->width, (UINT)s->height, 1 };
    if (!out || av_hwframe_get_buffer(s->frames_out, out, 0) < 0 || av_frame_copy_props(out, in) < 0) {
        av_frame_free(&out);
        return NULL;
    }
    s->hw->device_context->lpVtbl->CopySubresourceRegion(s->hw->device_context,
        (ID3D11Resource *)out->data[0], (UINT)(intptr_t)out->data[1], 0, 0, 0,
        (ID3D11Resource *)in->data[0], (UINT)(intptr_t)in->data[1], &box);
    out->width = s->width;
    out->height = s->height;
    return out;
}

/* Runs one FRUC call and waits for its fence; returns 1 synthesized, 0 repeated, <0 failure. */
static int fruc_call(NvOFrucContext *s, ID3D11Texture2D *input, double in_ts, int skip, double out_ts)
{
    FrucProcessIn pin = { 0 };
    FrucProcessOut pout = { 0 };
    bool repeated = false;
    int st;

    pin.input.frame = input;
    pin.input.timestamp = in_ts;
    pin.skip_warp = skip;
    pin.wait.fence.value = shared.fence_value;
    pout.output.frame = shared.interp;
    pout.output.timestamp = out_ts;
    pout.output.frame_repeated = &repeated;
    pout.signal.fence.value = ++shared.fence_value;
    st = shared.process(shared.handle, &pin, &pout);
    if (st != FRUC_SUCCESS)
        return -1;
    if (FAILED(shared.fence->lpVtbl->SetEventOnCompletion(shared.fence, shared.fence_value, shared.event)) ||
        WaitForSingleObject(shared.event, s->fence_timeout_ms) != WAIT_OBJECT_0)
        return -2;
    return repeated ? 0 : 1;
}

static int filter_frame(AVFilterLink *inlink, AVFrame *in)
{
    AVFilterContext *avctx = inlink->dst;
    AVFilterLink *outlink = avctx->outputs[0];
    NvOFrucContext *s = avctx->priv;
    AVHWFramesContext *in_frames = in->hw_frames_ctx ? (AVHWFramesContext *)in->hw_frames_ctx->data : NULL;
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb = { 0 }, yuv = yuv_space(in);
    double t = in->pts == AV_NOPTS_VALUE ? NAN : in->pts * av_q2d(inlink->time_base);
    int ret = 0, slot, converted;
    AVFrame *src_out;

    if (!in_frames || in_frames->sw_format != AV_PIX_FMT_NV12)
        go_passthrough(avctx, "only 8-bit NV12 video can use Generated Motion");
    if (s->simulate_failure > 0 && s->source_frames >= s->simulate_failure)
        go_passthrough(avctx, "simulated backend failure");
    s->source_frames++;

    DEV_LOCK(s);
    src_out = copy_source(s, in);
    DEV_UNLOCK(s);
    if (!src_out) {
        av_frame_free(&in);
        return AVERROR(ENOMEM);
    }
    src_out->pts = in->pts == AV_NOPTS_VALUE ? AV_NOPTS_VALUE : in->pts * 120;

    if (!s->passthrough && !isnan(t)) {
        ff_mutex_lock(&shared_lock);
        slot = (int)s->source_frames & 1;
        DEV_LOCK(s);
        converted = blt(s, (ID3D11Texture2D *)in->data[0], (int)(intptr_t)in->data[1], yuv,
                        shared.render[slot], -1, rgb) >= 0 &&
                    SUCCEEDED(s->context4->lpVtbl->Signal(s->context4, shared.fence, ++shared.fence_value));
        DEV_UNLOCK(s);
        if (!converted) {
            shared.errors++;
            go_passthrough(avctx, "source conversion failed");
        } else {
            double dt = t - s->previous_time;
            int reset = !s->have_previous || dt <= 0 || dt > 0.25;
            s->source_index += 1;
            if (reset) {
                s->anchor = t;
                s->grid_index = 0;
                if (fruc_call(s, shared.render[slot], s->source_index, 1, s->source_index) < 0) {
                    shared.errors++;
                    go_passthrough(avctx, "FRUC state update failed");
                }
            } else {
                double step = 1.0 / av_q2d(s->fps);
                int emitted = 0;
                for (;;) {
                    double g = s->anchor + (s->grid_index + 1) * step, frac;
                    AVFrame *gen;
                    int r;
                    if (g >= t - step * 0.05)
                        break;
                    s->grid_index++;
                    frac = (g - s->previous_time) / dt;
                    if (frac <= 0.02)
                        continue;
                    r = fruc_call(s, shared.render[slot], s->source_index, 0, s->source_index - 1 + frac);
                    emitted = 1;
                    if (r < 0) {
                        shared.errors++;
                        go_passthrough(avctx, r == -2 ? "FRUC fence timeout" : "FRUC process failed");
                        break;
                    }
                    gen = av_frame_alloc();
                    if (!gen || av_hwframe_get_buffer(s->frames_out, gen, 0) < 0 || av_frame_copy_props(gen, in) < 0) {
                        av_frame_free(&gen);
                        ret = AVERROR(ENOMEM);
                        break;
                    }
                    DEV_LOCK(s);
                    converted = blt(s, shared.interp, 0, rgb,
                                    (ID3D11Texture2D *)gen->data[0], (int)(intptr_t)gen->data[1], yuv) >= 0 &&
                                SUCCEEDED(s->context4->lpVtbl->Signal(s->context4, shared.fence, ++shared.fence_value));
                    DEV_UNLOCK(s);
                    if (!converted) {
                        av_frame_free(&gen);
                        shared.errors++;
                        go_passthrough(avctx, "generated frame conversion failed");
                        break;
                    }
                    gen->width = s->width;
                    gen->height = s->height;
                    gen->pts = s->previous_pts * 120 + llrint((in->pts - s->previous_pts) * 120 * frac);
                    gen->duration = 0;
                    if (r) shared.generated++; else shared.repeated++;
                    tag(gen, s, r ? "generated" : "repeated");
                    ff_mutex_unlock(&shared_lock);
                    ret = ff_filter_frame(outlink, gen);
                    ff_mutex_lock(&shared_lock);
                    if (ret < 0)
                        break;
                }
                if (!emitted && !s->passthrough && fruc_call(s, shared.render[slot], s->source_index, 1, s->source_index) < 0) {
                    shared.errors++;
                    go_passthrough(avctx, "FRUC state update failed");
                }
            }
            s->have_previous = 1;
            s->previous_time = t;
            s->previous_pts = in->pts;
        }
        ff_mutex_unlock(&shared_lock);
    }
    if (ret < 0) {
        av_frame_free(&src_out);
        av_frame_free(&in);
        return ret;
    }
    src_out->duration = 0;
    tag(src_out, s, "source");
    av_frame_free(&in);
    return ff_filter_frame(outlink, src_out);
}

static int config_output(AVFilterLink *outlink)
{
    AVFilterContext *avctx = outlink->src;
    NvOFrucContext *s = avctx->priv;
    AVFilterLink *inlink = avctx->inputs[0];
    FilterLink *inl = ff_filter_link(inlink), *outl = ff_filter_link(outlink);
    AVHWFramesContext *in_frames, *out_frames;
    AVD3D11VAFramesContext *out_hw;
    int ret;

    if (!inl->hw_frames_ctx) {
        av_log(avctx, AV_LOG_ERROR, "Generated Motion needs D3D11 hardware frames\n");
        return AVERROR(EINVAL);
    }
    in_frames = (AVHWFramesContext *)inl->hw_frames_ctx->data;
    av_buffer_unref(&s->device_ref);
    s->device_ref = av_buffer_ref(in_frames->device_ref);
    if (!s->device_ref)
        return AVERROR(ENOMEM);
    s->hw = ((AVHWDeviceContext *)s->device_ref->data)->hwctx;
    s->width = inlink->w & ~1;
    s->height = inlink->h & ~1;

    av_buffer_unref(&s->frames_out);
    s->frames_out = av_hwframe_ctx_alloc(s->device_ref);
    if (!s->frames_out)
        return AVERROR(ENOMEM);
    out_frames = (AVHWFramesContext *)s->frames_out->data;
    out_frames->format = AV_PIX_FMT_D3D11;
    out_frames->sw_format = AV_PIX_FMT_NV12;
    out_frames->width = s->width;
    out_frames->height = s->height;
    /* NV12 render-target texture arrays are rejected (E_INVALIDARG) on the RTX 4080
     * driver, so the pool allocates one recyclable texture per frame instead. */
    out_frames->initial_pool_size = 0;
    out_hw = out_frames->hwctx;
    out_hw->BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if ((ret = av_hwframe_ctx_init(s->frames_out)) < 0)
        return ret;
    av_buffer_unref(&outl->hw_frames_ctx);
    outl->hw_frames_ctx = av_buffer_ref(s->frames_out);
    if (!outl->hw_frames_ctx)
        return AVERROR(ENOMEM);

    outlink->w = s->width;
    outlink->h = s->height;
    outlink->time_base = av_mul_q(inlink->time_base, av_make_q(1, 120));
    outl->frame_rate = s->fps;

    if (in_frames->sw_format != AV_PIX_FMT_NV12)
        go_passthrough(avctx, "only 8-bit NV12 video can use Generated Motion");
    else
        ensure_backend(avctx);
    av_log(avctx, AV_LOG_INFO, "Generated Motion %dx%d -> %d/%d fps: %s\n",
           s->width, s->height, s->fps.num, s->fps.den, s->state);
    return 0;
}

static av_cold int init(AVFilterContext *avctx)
{
    NvOFrucContext *s = avctx->priv;
    snprintf(s->state, sizeof(s->state), "initializing");
    return 0;
}

static av_cold void uninit(AVFilterContext *avctx)
{
    NvOFrucContext *s = avctx->priv;
    RELEASE(s->processor);
    RELEASE(s->enumerator);
    RELEASE(s->video_context);
    RELEASE(s->video_device);
    RELEASE(s->context4);
    av_buffer_unref(&s->frames_out);
    av_buffer_unref(&s->device_ref);
}

#define OFFSET(x) offsetof(NvOFrucContext, x)
#define FLAGS (AV_OPT_FLAG_FILTERING_PARAM | AV_OPT_FLAG_VIDEO_PARAM)
static const AVOption nvofruc_options[] = {
    { "dll", "Path to NvOFFRUC.dll", OFFSET(dll_path), AV_OPT_TYPE_STRING, { .str = NULL }, .flags = FLAGS },
    { "fps", "Output frame rate", OFFSET(fps), AV_OPT_TYPE_VIDEO_RATE, { .str = "48" }, 1, 240, FLAGS },
    { "simulate_failure", "Test hook: fail after N source frames", OFFSET(simulate_failure), AV_OPT_TYPE_INT, { .i64 = 0 }, 0, INT_MAX, FLAGS },
    { "fence_timeout", "Milliseconds to wait for one FRUC frame", OFFSET(fence_timeout_ms), AV_OPT_TYPE_INT, { .i64 = 250 }, 10, 5000, FLAGS },
    { NULL }
};

AVFILTER_DEFINE_CLASS(nvofruc);

static const AVFilterPad nvofruc_inputs[] = {
    { .name = "default", .type = AVMEDIA_TYPE_VIDEO, .filter_frame = filter_frame },
};

static const AVFilterPad nvofruc_outputs[] = {
    { .name = "default", .type = AVMEDIA_TYPE_VIDEO, .config_props = config_output },
};

const FFFilter ff_vf_nvofruc = {
    .p.name         = "nvofruc",
    .p.description  = NULL_IF_CONFIG_SMALL("NVIDIA Optical Flow frame-rate up-conversion (D3D11)"),
    .p.priv_class   = &nvofruc_class,
    .p.flags        = AVFILTER_FLAG_HWDEVICE,
    .priv_size      = sizeof(NvOFrucContext),
    .init           = init,
    .uninit         = uninit,
    FILTER_INPUTS(nvofruc_inputs),
    FILTER_OUTPUTS(nvofruc_outputs),
    FILTER_SINGLE_PIXFMT(AV_PIX_FMT_D3D11),
    .flags_internal = FF_FILTER_FLAG_HWFRAME_AWARE,
};
