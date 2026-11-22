// Send native move/resize messages while the scrcpy SDL event loop is suspended.
#include <stdio.h>
#include <string.h>
#include <SDL3/SDL.h>
#define SDL_MAIN_HANDLED
#include <SDL3/SDL_main.h>
#include <libavcodec/avcodec.h>
#include "screen.h"
#include "trait/packet_sink.h"
#define COBJMACROS
#include <d3d11.h>
#include <windows.h>

static int
exercise_modal_loop(struct sc_screen *screen, AVFrame *frame, HWND hwnd,
                    bool resize) {
    RECT original;
    if (!GetWindowRect(hwnd, &original)) return -1;
    int64_t before = screen->frame->pts < 0 ? 0 : screen->frame->pts;
    SendMessageW(hwnd, WM_ENTERSIZEMOVE, 0, 0);
    for (int sample = 1; sample <= 30; ++sample) {
        if (av_frame_make_writable(frame) < 0) return -1;
        for (int y = 0; y < frame->height; ++y)
            memset(frame->data[0] + y * frame->linesize[0], sample * 4 + 16, frame->width);
        for (int plane = 1; plane <= 2; ++plane)
            for (int y = 0; y < frame->height / 2; ++y)
                memset(frame->data[plane] + y * frame->linesize[plane], 128, frame->width / 2);
        ++frame->pts;
        screen->frame_sink.ops->push(&screen->frame_sink, frame);
        SDL_Delay(18);
        if (sample <= 20) {
            SetWindowPos(hwnd, NULL, original.left + (resize ? 0 : sample), original.top,
                         original.right - original.left + (resize ? sample : 0),
                         original.bottom - original.top, SWP_NOACTIVATE | SWP_NOZORDER);
        }
        // The system's sizing loop still dispatches Win32 messages, including
        // SDL's WM_TIMER, but does not consume from SDL_WaitEvent.
        MSG message;
        while (PeekMessageW(&message, hwnd, 0, 0, PM_REMOVE))
            DispatchMessageW(&message);
    }
    int64_t after = screen->frame->pts < 0 ? 0 : screen->frame->pts;
    SendMessageW(hwnd, WM_EXITSIZEMOVE, 0, 0);
    return (int)(after - before);
}

static bool
toggle_fullscreen_from_toolbar(struct sc_screen *screen, HWND hwnd,
                               bool expected) {
    UINT message = RegisterWindowMessageW(L"Morror.Scrcpy.ToggleFullscreen");
    if (!message || !PostMessageW(hwnd, message, 0, 0)) return false;
    Uint64 deadline = SDL_GetTicks() + 1500;
    do {
        SDL_Event event;
        while (SDL_PollEvent(&event)) sc_screen_handle_event(screen, &event);
        bool fullscreen = (SDL_GetWindowFlags(screen->window) & SDL_WINDOW_FULLSCREEN) != 0;
        if (fullscreen == expected) return true;
        SDL_Delay(10);
    } while (SDL_GetTicks() < deadline);
    return false;
}

static bool
window_is_pinned(struct sc_screen *screen, HWND hwnd, bool expected) {
    bool sdl_pinned = (SDL_GetWindowFlags(screen->window) & SDL_WINDOW_ALWAYS_ON_TOP) != 0;
    bool hwnd_pinned = (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    return sdl_pinned == expected && hwnd_pinned == expected;
}

static bool
set_pinned_from_toolbar(struct sc_screen *screen, HWND hwnd, bool expected) {
    UINT message = RegisterWindowMessageW(L"Morror.Scrcpy.SetAlwaysOnTop");
    if (!message || !PostMessageW(hwnd, message, expected, 0)) return false;
    Uint64 deadline = SDL_GetTicks() + 1500;
    do {
        SDL_Event event;
        while (SDL_PollEvent(&event)) sc_screen_handle_event(screen, &event);
        if (window_is_pinned(screen, hwnd, expected)) return true;
        SDL_Delay(10);
    } while (SDL_GetTicks() < deadline);
    return false;
}

static bool
read_rendered_pixel(SDL_Renderer *renderer, SDL_Texture *texture, Uint8 rgba[4]) {
    if (!SDL_RenderTexture(renderer, texture, NULL, NULL)) return false;
    SDL_Rect rect = {20, 20, 1, 1};
    SDL_Surface *surface = SDL_RenderReadPixels(renderer, &rect);
    if (!surface) return false;
    bool ok = SDL_ReadSurfacePixel(surface, 0, 0, &rgba[0], &rgba[1], &rgba[2], &rgba[3]);
    SDL_DestroySurface(surface);
    return ok && SDL_RenderPresent(renderer);
}

static bool
upload_software_frame(struct sc_texture *tex, const AVFrame *frame, bool direct) {
    if (direct) return sc_texture_update(tex, frame);
    return SDL_UpdateYUVTexture(sc_texture_get(tex), NULL,
                                frame->data[0], frame->linesize[0],
                                frame->data[1], frame->linesize[1],
                                frame->data[2], frame->linesize[2]);
}

static double
measure_upload(SDL_Renderer *renderer, struct sc_texture *tex,
               const AVFrame *frame, bool direct) {
    const unsigned samples = 24;
    Uint64 elapsed = 0;
    for (unsigned i = 0; i < samples; ++i) {
        Uint64 start = SDL_GetPerformanceCounter();
        if (!upload_software_frame(tex, frame, direct)) return -1;
        elapsed += SDL_GetPerformanceCounter() - start;
        if (!SDL_RenderTexture(renderer, sc_texture_get(tex), NULL, NULL)
                || !SDL_RenderPresent(renderer)) return -1;
    }
    return elapsed * 1000.0 / SDL_GetPerformanceFrequency() / samples;
}

static bool
exercise_4k_software_upload(SDL_Renderer *renderer) {
    struct sc_texture tex;
    if (!sc_texture_init(&tex, renderer, false, SC_HWDEC_MODE_DISABLED)) return false;
    AVFrame *frame = av_frame_alloc();
    bool ok = false;
    if (!frame) goto done;
    frame->width = 3840;
    frame->height = 2160;
    frame->format = AV_PIX_FMT_YUV420P;
    frame->colorspace = AVCOL_SPC_BT709;
    frame->color_range = AVCOL_RANGE_MPEG;
    if (av_frame_get_buffer(frame, 0) < 0) goto done;
    memset(frame->data[0], 82, frame->linesize[0] * frame->height);
    memset(frame->data[1], 90, frame->linesize[1] * (frame->height / 2));
    memset(frame->data[2], 235, frame->linesize[2] * (frame->height / 2));

    Uint8 direct[4], reference[4];
    if (!upload_software_frame(&tex, frame, true)
            || !read_rendered_pixel(renderer, sc_texture_get(&tex), direct)
            || !upload_software_frame(&tex, frame, false)
            || !read_rendered_pixel(renderer, sc_texture_get(&tex), reference)
            || memcmp(direct, reference, sizeof(direct)) != 0) goto done;

    double staging_ms = measure_upload(renderer, &tex, frame, false);
    double direct_ms = measure_upload(renderer, &tex, frame, true);
    printf("4K software upload on this PC: SDL staging %.2f ms, direct %.2f ms per frame\n",
           staging_ms, direct_ms);
    ok = staging_ms >= 0 && direct_ms >= 0;

done:
    av_frame_free(&frame);
    sc_texture_destroy(&tex);
    return ok;
}

static bool
exercise_4k_d3d11va_textures(SDL_Renderer *renderer) {
    ID3D11Device *device = SDL_GetPointerProperty(SDL_GetRendererProperties(renderer),
                             SDL_PROP_RENDERER_D3D11_DEVICE_POINTER, NULL);
    if (!device) {
        puts("4K hardware texture test skipped (no D3D11 device)");
        return true;
    }
    const UINT width = 3840, height = 2160;
    D3D11_TEXTURE2D_DESC desc = {
        .Width = width, .Height = height, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_NV12, .SampleDesc = {1, 0}, .Usage = D3D11_USAGE_DEFAULT,
    };
    unsigned char *pixels = SDL_malloc(width * height * 3 / 2);
    if (!pixels) return false;
    memset(pixels, 82, width * height);
    memset(pixels + width * height, 128, width * height / 2);
    D3D11_SUBRESOURCE_DATA source = {.pSysMem = pixels, .SysMemPitch = width};
    ID3D11Texture2D *surface = NULL;
    HRESULT result = ID3D11Device_CreateTexture2D(device, &desc, &source, &surface);
    SDL_free(pixels);
    if (FAILED(result)) {
        puts("4K hardware texture test skipped (NV12 surface unavailable)");
        return true;
    }

    struct sc_texture tex;
    bool initialized = sc_texture_init(&tex, renderer, false, SC_HWDEC_MODE_D3D11VA);
    AVBufferRef *metadata = av_buffer_allocz(sizeof(AVHWFramesContext));
    bool ok = false;
    if (initialized && metadata) {
        AVHWFramesContext *frames = (AVHWFramesContext *) metadata->data;
        frames->width = width;
        frames->height = height;
        frames->sw_format = AV_PIX_FMT_NV12;
        AVFrame frame = {
            .width = width, .height = height, .format = AV_PIX_FMT_D3D11,
            .colorspace = AVCOL_SPC_BT709, .color_range = AVCOL_RANGE_MPEG,
            .data = {(uint8_t *) surface}, .hw_frames_ctx = metadata,
        };
        Uint8 first[4], second[4];
        if (sc_texture_update(&tex, &frame)) {
            SDL_Texture *previous = sc_texture_get(&tex);
            ok = read_rendered_pixel(renderer, previous, first)
              && sc_texture_update(&tex, &frame)
              && sc_texture_get(&tex) != previous
              && read_rendered_pixel(renderer, sc_texture_get(&tex), second)
              && memcmp(first, second, sizeof(first)) == 0
              && sc_texture_update(&tex, &frame)
              && sc_texture_get(&tex) == previous;
        }
    }
    av_buffer_unref(&metadata);
    if (initialized) sc_texture_destroy(&tex);
    ID3D11Texture2D_Release(surface);
    printf("4K hardware texture rotation: %s\n", ok ? "PASS" : "FAIL");
    return ok;
}

int
main(void) {
    SDL_SetMainReady();
    if (!SDL_Init(SDL_INIT_VIDEO)) return 1;
    struct sc_screen screen = {0};
    struct sc_screen_params params = {
        .video = true,
        .always_on_top = true,
        .window_title = "Morror live frame test",
        .window_x = SC_WINDOW_POSITION_UNDEFINED,
        .window_y = SC_WINDOW_POSITION_UNDEFINED,
        .window_width = 320,
        .window_height = 240,
        .hwdec_mode = SC_HWDEC_MODE_DISABLED,
    };
    if (!sc_screen_init(&screen, &params)) return 1;
    AVCodecContext codec = {.width = 160, .height = 120, .pix_fmt = AV_PIX_FMT_YUV420P};
    struct sc_stream_session session = {.video = {.width = 160, .height = 120}};
    if (!screen.frame_sink.ops->open(&screen.frame_sink, &codec, &session)) return 1;
    SDL_Event event;
    while (SDL_PollEvent(&event)) sc_screen_handle_event(&screen, &event);
    HWND hwnd = SDL_GetPointerProperty(SDL_GetWindowProperties(screen.window),
                                       SDL_PROP_WINDOW_WIN32_HWND_POINTER, NULL);
    if (!hwnd || !screen.window_shown) return 1;
    bool unpinned = window_is_pinned(&screen, hwnd, true)
                 && set_pinned_from_toolbar(&screen, hwnd, false);
    bool fullscreen = toggle_fullscreen_from_toolbar(&screen, hwnd, true)
                   && window_is_pinned(&screen, hwnd, false)
                   && toggle_fullscreen_from_toolbar(&screen, hwnd, false)
                   && window_is_pinned(&screen, hwnd, false);
    bool pinning = unpinned && set_pinned_from_toolbar(&screen, hwnd, true)
                && set_pinned_from_toolbar(&screen, hwnd, false);
    AVFrame *frame = av_frame_alloc();
    if (!frame) return 1;
    frame->width = 160;
    frame->height = 120;
    frame->format = AV_PIX_FMT_YUV420P;
    frame->pts = 0;
    if (av_frame_get_buffer(frame, 0) < 0) return 1;
    int moving = exercise_modal_loop(&screen, frame, hwnd, false);
    int resizing = exercise_modal_loop(&screen, frame, hwnd, true);
    sc_screen_set_paused(&screen, true);
    int paused = exercise_modal_loop(&screen, frame, hwnd, false);
    while (SDL_PollEvent(&event)) sc_screen_handle_event(&screen, &event);
    bool video_upload = true;
    const char *renderer_name = SDL_GetRendererName(screen.renderer);
    if (renderer_name && strcmp(renderer_name, "direct3d11") == 0) {
        video_upload = exercise_4k_software_upload(screen.renderer)
                    && exercise_4k_d3d11va_textures(screen.renderer);
    } else {
        puts("4K texture tests skipped (Direct3D 11 renderer unavailable)");
    }
    av_frame_free(&frame);
    screen.frame_sink.ops->close(&screen.frame_sink);
    sc_screen_destroy(&screen);
    SDL_Quit();
    printf("Frames advanced inside move: %d; inside resize: %d; manually paused: %d\n",
           moving, resizing, paused);
    printf("Fullscreen via toolbar message: %s\n", fullscreen ? "PASS" : "FAIL");
    printf("Pinning after a pinned launch: %s\n", pinning ? "PASS" : "FAIL");
    return moving < 25 || resizing < 25 || paused != 0 || !fullscreen || !pinning
        || !video_upload;
}
