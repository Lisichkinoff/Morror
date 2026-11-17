// Send native move/resize messages while the scrcpy SDL event loop is suspended.
#include <stdio.h>
#include <string.h>
#include <SDL3/SDL.h>
#define SDL_MAIN_HANDLED
#include <SDL3/SDL_main.h>
#include <libavcodec/avcodec.h>
#include "screen.h"
#include "trait/packet_sink.h"
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

int
main(void) {
    SDL_SetMainReady();
    if (!SDL_Init(SDL_INIT_VIDEO)) return 1;
    struct sc_screen screen = {0};
    struct sc_screen_params params = {
        .video = true,
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
    av_frame_free(&frame);
    screen.frame_sink.ops->close(&screen.frame_sink);
    sc_screen_destroy(&screen);
    SDL_Quit();
    printf("Frames advanced inside move: %d; inside resize: %d; manually paused: %d\n",
           moving, resizing, paused);
    return moving < 25 || resizing < 25 || paused != 0;
}
