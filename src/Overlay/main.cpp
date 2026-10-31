// dlss5-overlay: DLSS 5 on any window, the helper behind DLSS 5 Master's overlay.
//
// Captures one window with Windows Graphics Capture, measures its motion with the GPU's optical-flow
// engine, runs NVIDIA DLSS (DLAA) on every frame through NGX, and draws the result in a click-through
// window exactly over the original. ReShade x64 (dxgi.dll) and the RenoDX DLSS5 add-on sit next to
// this exe: to them this process is a D3D12 game, so the add-on hooks our DLSS evaluate and adds the
// neural rendering pass. No file of the captured program is touched.
//
// The NGX set-up follows DLSS5-Feeder's 64-bit helper (MIT, Jean-Laurent Rouzies):
// load ReShade before d3d12.dll, let the add-on arm its hooks, then create a DLAA feature.
//
// F9 switches between DLSS 5 and the original picture, Ctrl+F9 quits. --no-motion (by hand) turns motion off.
// Built with OVERLAY_TESTS defined (DLSS 5 Master's automated checks only), it also takes --test-any-focus (draw while the
// window is not in front, so a check can use a window placed off screen), --test-remove-device <seconds>,
// --test-dump <folder> (the pictures of five frames in a row, 8 seconds in: see TestDump), --test-sdr (the SDR path on
// an HDR screen too) and --test-no-finish (in-between frames as frame generation makes them, to compare with Finish).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <dcomp.h>
#include <dwmapi.h>
#include <share.h>
#include <clocale>
#include <cstdio>
#include <cstdint>
#include <string>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <d3dcompiler.h>

#include <nvsdk_ngx.h>
#include <nvsdk_ngx_helpers.h>
#include <nvsdk_ngx_helpers_dlssg_d3d.h>
#include <nvOpticalFlowCommon.h>
#include <nvOpticalFlowD3D11.h>

namespace wgc = winrt::Windows::Graphics::Capture;
namespace wdx = winrt::Windows::Graphics::DirectX;
namespace wd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

// ---------------------------------------------------------------------------------------------
// Log: dlss5-overlay.log next to the exe (and the console, when run by hand from one)

static FILE *g_log = nullptr;

static void Log(const char *fmt, ...)
{
    char line[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof(line), fmt, ap);
    va_end(ap);
    SYSTEMTIME t;
    GetLocalTime(&t);
    printf("%s\n", line);
    if (g_log) { fprintf(g_log, "%02d:%02d:%02d.%03d  %s\n", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, line); fflush(g_log); }
}

static std::wstring ExeDir()
{
    wchar_t p[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    if (wchar_t *s = wcsrchr(p, L'\\')) *(s + 1) = L'\0';
    return p;
}

template <class T> static void Release(T *&p) { if (p) { p->Release(); p = nullptr; } }

// ---------------------------------------------------------------------------------------------
// State

struct App
{
    HWND target = nullptr;       // the window being captured
    HWND window = nullptr;       // our output window

    // D3D12: NGX, the swapchain and the shared textures
    ID3D12Device *dev = nullptr;
    ID3D12CommandQueue *queue = nullptr;
    ID3D12CommandAllocator *alloc = nullptr;   // set-up work that is waited for (feature creation)
    ID3D12GraphicsCommandList *list = nullptr;
    ID3D12Fence *fence = nullptr;        // our own frame fence
    UINT64 fence_value = 0;
    HANDLE fence_event = nullptr;
    IDXGISwapChain3 *swap = nullptr;
    ID3D12Resource *back[6] = {};        // enough to queue a real frame and up to three generated ones ahead

    // Per-frame work is not waited for: each recording uses the next allocator of a ring, and an allocator is
    // only reused once the GPU has passed the fence value of its last recording.
    ID3D12CommandAllocator *ring[8] = {};
    UINT64 ring_value[8] = {};
    UINT ring_next = 0;
    ID3D12CommandAllocator *ring_current = nullptr;
    // One frame in flight: a new frame starts only once the GPU has finished the previous one. Without this, a
    // heavy setting (2 passes at 4K, about 25 ms a frame) queued work faster than the GPU did it, and the
    // picture fell further and further behind (92 ms a frame, then a frame a second, in Chrome).
    UINT64 frame_done_value = 0;

    ID3D12Resource *color = nullptr;     // shared with D3D11: the captured picture
    ID3D12Resource *output = nullptr;    // DLSS writes here
    ID3D12Resource *depth = nullptr;     // constant: far plane
    ID3D12Resource *mv = nullptr;        // zero motion
    ID3D12Fence *copy_fence = nullptr;   // shared with D3D11, both ways: "the picture is in color" (D3D11 signals)
    UINT64 copy_value = 0;               // and "D3D12 is done reading it" (D3D12 signals); one rising counter

    // D3D11: Windows Graphics Capture
    ID3D11Device5 *dev11 = nullptr;
    ID3D11DeviceContext4 *ctx11 = nullptr;
    ID3D11Texture2D *color11 = nullptr;
    ID3D11Texture2D *mv11 = nullptr;     // shared with D3D12: the motion DLSS reads
    ID3D11Fence *copy_fence11 = nullptr;

    // Motion: the GPU's optical-flow engine compares each frame with the one before
    HMODULE nvof_dll = nullptr;
    NV_OF_D3D11_API_FUNCTION_LIST of = {};
    NvOFHandle of_handle = nullptr;
    ID3D11Texture2D *luma[2] = {};        // half-size greyscale of this frame and the previous one
    ID3D11UnorderedAccessView *luma_uav[2] = {};
    NvOFGPUBufferHandle luma_of[2] = {};
    ID3D11Texture2D *flow = nullptr;      // the engine's answer: one vector per grid block
    ID3D11ShaderResourceView *flow_srv = nullptr;
    NvOFGPUBufferHandle flow_of = nullptr;
    ID3D11ShaderResourceView *color_srv = nullptr;
    ID3D11UnorderedAccessView *mv_uav = nullptr;
    ID3D11ComputeShader *cs_luma = nullptr, *cs_mv = nullptr;
    ID3D11Buffer *cb = nullptr;
    UINT grid = 2;
    int cur = 0;                          // which luma[] holds this frame
    bool have_prev = false;
    bool motion_ready = false;

    // Still pixels: where the captured picture is the same as in the previous frame and nothing moved over it on the way,
    // nothing moved. Those pixels get no motion, and every in-between frame takes them from the real frame (see Finish).
    // It needs measured motion (to tell what moved over a pixel): without it no pixel is kept still.
    ID3D11Texture2D *prev11 = nullptr;    // the previous frame's picture
    ID3D11ShaderResourceView *prev_srv = nullptr, *pic_srv = nullptr, *mv_srv = nullptr, *still_srv = nullptr;
    ID3D11Texture2D *still11 = nullptr;   // shared with D3D12: 1 where the picture did not change
    ID3D11UnorderedAccessView *still_uav = nullptr;
    ID3D11ComputeShader *cs_still = nullptr, *cs_cover = nullptr, *cs_calm = nullptr;
    bool have_prev_picture = false;

    // Processing size: the window is captured at its own size (cw x ch), then everything -- DLSS, the neural pass,
    // motion, frame generation -- runs on a picture scale times that size (w x h). The compositor stretches the
    // finished picture back over the window. A game drawing 1440p into a 4K-sized window gains nothing from 4K.
    float scale = 1.0f;
    UINT cw = 0, ch = 0;
    ID3D11Texture2D *cap11 = nullptr;              // the captured client area at full size (only when scaling)
    ID3D11ShaderResourceView *cap_srv = nullptr;
    ID3D11RenderTargetView *color_rtv = nullptr;
    ID3D11VertexShader *vs_full = nullptr;
    ID3D11PixelShader *ps_down = nullptr;
    ID3D11SamplerState *linear_clamp = nullptr;
    ID3D11Buffer *scale_cb = nullptr;
    wd3d::IDirect3DDevice rt_device{ nullptr };
    wgc::GraphicsCaptureItem item{ nullptr };
    wgc::Direct3D11CaptureFramePool pool{ nullptr };
    wgc::GraphicsCaptureSession session{ nullptr };
    UINT pool_w = 0, pool_h = 0;          // the capture buffers' size: it only grows (see Frame)

    // NGX
    NVSDK_NGX_Parameter *params = nullptr;
    NVSDK_NGX_Handle *feature = nullptr;

    // Frame generation (DLSS-G): fg_frames in-between frames per captured frame (0 = off, 1 = 2x, 3 = 4x)
    UINT fg_frames = 0;
    NVSDK_NGX_Handle *fg_feature = nullptr;
    ID3D12Resource *interp[3] = {};
    // Finish: frame generation warps still text and icons next to moving parts (a video's page around it) although their
    // motion is zero, and on an HDR screen it clips at 1.0 (80 nits): white text at 203 nits came out at 40 % brightness
    // on every in-between frame. So it reads the real frame squeezed into 0..1 (HDR), writes into fg_raw, and a compute
    // pass makes each in-between frame from that: expanded back (HDR), with the still pixels taken from the real frame.
    ID3D12Resource *still = nullptr;      // still11 on D3D12
    ID3D12Resource *fg_in = nullptr;      // HDR: the real frame squeezed into 0..1, which frame generation reads
    ID3D12Resource *fg_raw = nullptr;     // frame generation's answer, before Finish
    ID3D12RootSignature *finish_root = nullptr;
    ID3D12PipelineState *pso_squeeze = nullptr, *pso_finish = nullptr;
    ID3D12DescriptorHeap *finish_heap = nullptr;
    UINT finish_step = 0;                 // descriptor size
    bool fg_have_prev = false;            // DLSS-G needs a previous real frame to interpolate from
    bool fg_fed = false;                  // DLSS-G saw the previous real frame (not while resting at 1x): its history is it
    UINT64 presented = 0;                 // frames shown, real and generated
    // Smart limit: no more in-between frames than the screen can show. A 60 fps game on a 120 Hz screen
    // gets 1 (2x); asking for 4x there only slowed the real frames to 30 per second.
    double refresh_hz = 60.0;
    // Memory guard: Windows gives each process a share (budget) of the graphics memory; when the game and the
    // overlay together need more, everything is paged and slows to a crawl. Over budget for a few seconds turns
    // frame generation off (the biggest saving), and says so if that was not enough.
    IDXGIAdapter3 *adapter = nullptr;
    int over_budget_s = 0;
    bool memory_warned = false;
    double game_interval = 0.0;           // smoothed time between the game's frames, in seconds
    // The overlay's own pace, timed on the GPU (CPU timing hid GPU work inside the waits for the screen, and the
    // choice flipped between 2x and 3x every second): DLSS + the neural pass per real frame, and each in-between frame.
    ID3D12QueryHeap *timestamps = nullptr;
    ID3D12Resource *timestamp_readback = nullptr;
    double timestamp_hz = 0.0;
    double base_ms = 0.0, fg_ms = 0.0;    // smoothed
    UINT timed_generated = 0;             // in-between frames in the frame whose timestamps are pending
    bool timed_pending = false;
    INT64 last_frame_time = 0;            // the capture's timestamp of the previous frame (100 ns units)
    UINT fg_now = 0;                      // in-between frames used for this frame

    UINT w = 0, h = 0;                   // current picture size
    // DirectComposition: lets the swapchain sit in a click-through overlay window
    IDCompositionDevice *dcomp = nullptr;
    IDCompositionTarget *dcomp_target = nullptr;
    IDCompositionVisual *dcomp_visual = nullptr;

    std::wstring stop_file;              // DLSS 5 Master creates this file to stop the overlay (works across elevation)
    bool dlss_on = true;
    // HDR: when the window's screen is in HDR mode, the whole path runs in scRGB (linear FP16, Windows' own HDR
    // format): capture, DLSS, the neural pass, frame generation and the swapchain. Otherwise 8-bit SDR as before.
    bool hdr = false;
    DXGI_FORMAT fmt = DXGI_FORMAT_B8G8R8A8_UNORM;
    bool stepped_aside = false;          // the game left the overlay no GPU time: hidden, the game shows as it is, until F9
    bool exclusive = false;              // the window holds the screen in exclusive fullscreen: nothing drawn can show
#ifdef OVERLAY_TESTS
    bool test_any_focus = false;
    double test_remove_device_s = 0.0;
    std::wstring test_dump;               // --test-dump: folder the pictures of five frames in a row are written to
    bool test_sdr = false;                // --test-sdr: the SDR path on an HDR screen too
    bool test_no_finish = false;          // --test-no-finish: no Finish pass (in-between frames as frame generation makes them)
#endif
    bool motion_on = true;
    bool reset = true;
    bool quit = false;
    const char *volatile closing_step = "";   // where shutdown is, for the watchdog's log line
};
static App g;

// ---------------------------------------------------------------------------------------------
// D3D12 helpers

static void WaitGpu()
{
    const UINT64 v = ++g.fence_value;
    g.queue->Signal(g.fence, v);
    if (g.fence->GetCompletedValue() < v)
    {
        g.fence->SetEventOnCompletion(v, g.fence_event);
        WaitForSingleObject(g.fence_event, 5000);
    }
}

// Three GPU timestamps per frame: start, after DLSS + the neural pass, after the in-between frames.
static void InitTimestamps()
{
    D3D12_QUERY_HEAP_DESC qd = { D3D12_QUERY_HEAP_TYPE_TIMESTAMP, 3, 0 };
    g.dev->CreateQueryHeap(&qd, IID_PPV_ARGS(&g.timestamps));
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    rd.Width = 3 * sizeof(UINT64);
    rd.Height = rd.DepthOrArraySize = rd.MipLevels = 1;
    rd.SampleDesc.Count = 1;
    rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    g.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g.timestamp_readback));
    UINT64 hz = 0;
    if (SUCCEEDED(g.queue->GetTimestampFrequency(&hz))) g.timestamp_hz = static_cast<double>(hz);
}

// The previous frame's timings; it has finished when this runs (Frame waits for it first).
static void ReadTimestamps()
{
    if (!g.timed_pending || !g.timestamp_readback || g.timestamp_hz <= 0.0) return;
    g.timed_pending = false;
    UINT64 *t = nullptr;
    const D3D12_RANGE range = { 0, 3 * sizeof(UINT64) };
    if (FAILED(g.timestamp_readback->Map(0, &range, reinterpret_cast<void **>(&t)))) return;
    const double base = (t[1] - t[0]) * 1000.0 / g.timestamp_hz;
    const double fg = g.timed_generated > 0 ? (t[2] - t[1]) * 1000.0 / g.timestamp_hz / g.timed_generated : -1.0;
    const D3D12_RANGE none = { 0, 0 };
    g.timestamp_readback->Unmap(0, &none);
    if (base > 0.0 && base < 250.0) g.base_ms = g.base_ms > 0.0 ? g.base_ms * 0.9 + base * 0.1 : base;
    if (fg > 0.0 && fg < 100.0) g.fg_ms = g.fg_ms > 0.0 ? g.fg_ms * 0.9 + fg * 0.1 : fg;
}

// Starts recording a per-frame command list on the next allocator of the ring.
static void BeginList()
{
    const UINT i = g.ring_next;
    g.ring_next = (g.ring_next + 1) % 8;
    if (g.fence->GetCompletedValue() < g.ring_value[i])
    {
        g.fence->SetEventOnCompletion(g.ring_value[i], g.fence_event);
        WaitForSingleObject(g.fence_event, 5000);
    }
    g.ring[i]->Reset();
    g.list->Reset(g.ring[i], nullptr);
    g.ring_current = g.ring[i];
}

// Submits the list and marks its allocator as busy until the GPU gets this far. Nothing waits here.
static void EndList()
{
    g.list->Close();
    ID3D12CommandList *lists[] = { g.list };
    g.queue->ExecuteCommandLists(1, lists);
    const UINT64 v = ++g.fence_value;
    g.queue->Signal(g.fence, v);
    for (UINT k = 0; k < 8; ++k) if (g.ring[k] == g.ring_current) g.ring_value[k] = v;
}

static void Barrier(ID3D12Resource *r, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = r;
    b.Transition.StateBefore = before;
    b.Transition.StateAfter = after;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    g.list->ResourceBarrier(1, &b);
}

static ID3D12Resource *MakeTex(UINT w, UINT h, DXGI_FORMAT fmt, D3D12_RESOURCE_FLAGS flags, D3D12_HEAP_FLAGS heap,
                               D3D12_RESOURCE_STATES state)
{
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width = w;
    rd.Height = h;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.Format = fmt;
    rd.SampleDesc.Count = 1;
    rd.Flags = flags;
    ID3D12Resource *r = nullptr;
    if (FAILED(g.dev->CreateCommittedResource(&hp, heap, &rd, state, nullptr, IID_PPV_ARGS(&r))))
        Log("texture %ux%u format %d could not be created", w, h, fmt);
    return r;
}

// ---------------------------------------------------------------------------------------------
// Start-up: ReShade first, then D3D12, the window and its swapchain

using PFN_CreateDevice = HRESULT(WINAPI *)(IUnknown *, D3D_FEATURE_LEVEL, REFIID, void **);
using PFN_CreateFactory2 = HRESULT(WINAPI *)(UINT, REFIID, void **);

// True once ReShade has hooked D3D12CreateDevice: the export points outside d3d12.dll or jumps away.
static bool ReShadeOwns(HMODULE d3d12, FARPROC p)
{
    if (!d3d12 || !p) return false;
    HMODULE owner = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(p), &owner))
        return false;
    if (owner != d3d12) return true;
    const BYTE *b = reinterpret_cast<const BYTE *>(p);
    return b[0] == 0xE9 || b[0] == 0xEB || (b[0] == 0xFF && b[1] == 0x25);
}

static const DWORD kClickThrough = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

// The overlay never takes focus: a "settings mode" that let it take clicks for ReShade's panel hung its
// frames for up to 18 s in Bloodborne (and the game pauses without focus), so the neural renderer runs on
// its defaults here.
static LRESULT CALLBACK WndProc(HWND w, UINT m, WPARAM wp, LPARAM lp)
{
    switch (m)
    {
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    case WM_CLOSE:
    case WM_DESTROY:
        g.quit = true;
        return 0;
    }
    return DefWindowProcW(w, m, wp, lp);
}

// The overlay never takes focus while you play, and the game keeps its keys to itself: neither
// RegisterHotKey nor GetAsyncKeyState saw F9 in Bloodborne. A low-level keyboard hook does --
// Windows calls the most recently installed one first, so ours sees the key before the game's.
// It runs on its own thread so a slow frame never delays the keyboard.
enum : LONG { KEY_DLSS = 1, KEY_QUIT = 2 };
static volatile LONG g_keys = 0;

static LRESULT CALLBACK KeyboardHook(int code, WPARAM wp, LPARAM lp)
{
    static bool down[256] = {};
    if (code == HC_ACTION)
    {
        const auto *k = reinterpret_cast<const KBDLLHOOKSTRUCT *>(lp);
        const bool press = wp == WM_KEYDOWN || wp == WM_SYSKEYDOWN;
        if (k->vkCode < 256)
        {
            const bool first = press && !down[k->vkCode];   // not auto-repeat
            down[k->vkCode] = press;
            if (first && k->vkCode == VK_F9)
            {
                const bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0 || down[VK_LCONTROL] || down[VK_RCONTROL];
                InterlockedOr(&g_keys, ctrl ? KEY_QUIT : KEY_DLSS);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wp, lp);
}

static DWORD WINAPI KeyboardThread(void *)
{
    if (!SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardHook, GetModuleHandleW(nullptr), 0)) Log("keyboard hook failed: %lu", GetLastError());
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {}
    return 0;
}

// Keys pressed since the last frame; acted on only while the game (or the overlay) is in front.
static void PollKeys(bool in_front)
{
    const LONG keys = InterlockedExchange(&g_keys, 0);
    if (!in_front || keys == 0) return;
    if (keys & KEY_QUIT) { g.quit = true; return; }
    if (keys & KEY_DLSS && g.stepped_aside) { g.stepped_aside = false; g.dlss_on = true; g.reset = true; Log("DLSS 5 on"); return; }
    if (keys & KEY_DLSS) { g.dlss_on = !g.dlss_on; g.reset = true; Log("DLSS 5 %s", g.dlss_on ? "on" : "off (original picture)"); }
}

// A game running free can take the whole GPU; the overlay's work then waits behind it and the picture drops to a
// frame a second, so the player cannot even reach the game's settings. When that holds for two 5-second readings
// (few frames AND slow GPU work for each, in-between frames included, since the wait can land on either; a paused
// game or still page also sends few frames, but quickly), the overlay steps aside and the game shows as it is until F9.
static void CheckStarved(double captured_fps)
{
    static int low = 0;
    if (!g.dlss_on || g.stepped_aside) { low = 0; return; }
    const double frame_ms = g.base_ms + (g.fg_feature ? g.fg_now : 0) * g.fg_ms;
    low = captured_fps < 10.0 && frame_ms > 50.0 ? low + 1 : 0;
    if (low < 2) return;
    low = 0;
    g.stepped_aside = true;
    InterlockedExchange(&g_keys, 0);     // an F9 pressed during the slowness would bring it straight back
    Log("stepped aside: the game is using the whole GPU (%.0f real frames a second, %.0f ms of GPU work each); F9 brings DLSS 5 back",
        captured_fps, frame_ms);
}

// Exclusive fullscreen (a game or a video player that takes the screen for itself) hides every overlay: nothing drawn
// reaches the screen, and the GPU time would only slow the window down. Windows says when a program holds the screen
// that way; while the window does, the overlay waits.
static void CheckExclusiveFullscreen()
{
    QUERY_USER_NOTIFICATION_STATE state = QUNS_ACCEPTS_NOTIFICATIONS;
    const bool now = SUCCEEDED(SHQueryUserNotificationState(&state)) && state == QUNS_RUNNING_D3D_FULL_SCREEN &&
                     GetForegroundWindow() == g.target;
    if (now == g.exclusive) return;
    g.exclusive = now;
    Log(now ? "exclusive fullscreen: the window holds the screen for itself, which hides every overlay; waiting until it is borderless or windowed"
            : "exclusive fullscreen ended: drawing again");
}

// Whether the game's window is in front of the overlay in Windows' stacking order.
static bool GameAboveOverlay()
{
    for (HWND h = GetWindow(g.window, GW_HWNDPREV); h; h = GetWindow(h, GW_HWNDPREV))
        if (h == g.target || GetAncestor(h, GA_ROOTOWNER) == g.target) return true;
    return false;
}

// Keep the overlay exactly over the game's client area, and show it only while the game
// (or the overlay itself, in settings mode) is the window in front.
static bool TrackTarget()
{
    const HWND fg = GetForegroundWindow();
    bool front = fg == g.target || fg == g.window;
#ifdef OVERLAY_TESTS
    front = front || g.test_any_focus;
#endif
    const bool show = !g.stepped_aside && !g.exclusive && !IsIconic(g.target) && IsWindowVisible(g.target) && front;
    if (!show)
    {
        if (IsWindowVisible(g.window)) ShowWindow(g.window, SW_HIDE);
        return false;
    }
    RECT rc = {};
    GetClientRect(g.target, &rc);
    POINT tl = { 0, 0 };
    ClientToScreen(g.target, &tl);
    const RECT want = { tl.x, tl.y, tl.x + rc.right, tl.y + rc.bottom };
    RECT have = {};
    GetWindowRect(g.window, &have);
    if (!IsWindowVisible(g.window) || !EqualRect(&want, &have))
        SetWindowPos(g.window, HWND_TOPMOST, want.left, want.top, want.right - want.left, want.bottom - want.top,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
    else if (GameAboveOverlay())
    {
        // Unreal Engine games (and others) make their window topmost while active: among topmost windows the one
        // raised last is in front, so the game covers the overlay, which keeps drawing unseen.
        static bool told = false;
        if (!told) { Log("the game put its window above the overlay; the overlay keeps itself on top"); told = true; }
        SetWindowPos(g.window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
    return true;
}

// [RenoDX.DLSS5] defaults for a process that calls NGX itself, written only where unset.
static void WriteAddonDefaults()
{
    const std::wstring ini = ExeDir() + L"ReShade.ini";
    auto set = [&](const wchar_t *key, const wchar_t *value)
    {
        wchar_t buf[64] = {};
        GetPrivateProfileStringW(L"RenoDX.DLSS5", key, L"", buf, 64, ini.c_str());
        if (buf[0] == L'\0') WritePrivateProfileStringW(L"RenoDX.DLSS5", key, value, ini.c_str());
    };
    set(L"EnableHooks", L"2");         // NGX only: this program calls NGX directly, no Streamline
    set(L"NeuralUplift", L"1");        // neural rendering on
    set(L"NREnableUpscaling", L"0");   // 1:1 DLAA
}

// scRGB for an HDR swapchain: linear, Rec.709 primaries, 1.0 = 80 nits; Windows shows it on the HDR screen as is.
static void SetSwapColorSpace()
{
    if (!g.hdr || !g.swap) return;
    const HRESULT hr = g.swap->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
    if (FAILED(hr)) Log("HDR: the swapchain refused scRGB: 0x%08X", hr);
}

// Whether the screen showing the target window is in HDR mode (Windows' HDR switch is on for it).
static bool ScreenIsHdr(IDXGIFactory4 *factory)
{
    const HMONITOR mon = MonitorFromWindow(g.target, MONITOR_DEFAULTTONEAREST);
    IDXGIAdapter1 *adapter = nullptr;
    bool hdr = false, found = false;
    for (UINT a = 0; !found && factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; ++a)
    {
        IDXGIOutput *out = nullptr;
        for (UINT o = 0; !found && adapter->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND; ++o)
        {
            IDXGIOutput6 *out6 = nullptr;
            DXGI_OUTPUT_DESC1 d = {};
            if (SUCCEEDED(out->QueryInterface(IID_PPV_ARGS(&out6))) && SUCCEEDED(out6->GetDesc1(&d)) && d.Monitor == mon)
            {
                found = true;
                hdr = d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            }
            Release(out6);
            out->Release();
        }
        adapter->Release();
    }
    return hdr;
}

static bool InitD3D12()
{
    WriteAddonDefaults();
    HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    HMODULE d3d12 = LoadLibraryW(L"d3d12.dll");
    wchar_t dxgi_path[MAX_PATH] = {};
    GetModuleFileNameW(dxgi, dxgi_path, MAX_PATH);
    Log("dxgi.dll: %ls", dxgi_path);

    FARPROC create = GetProcAddress(d3d12, "D3D12CreateDevice");
    for (int i = 0; i < 200 && !ReShadeOwns(d3d12, create); ++i) { Sleep(10); create = GetProcAddress(d3d12, "D3D12CreateDevice"); }
    Log("ReShade %s D3D12", ReShadeOwns(d3d12, create) ? "hooks" : "does NOT hook (no neural pass, plain DLSS only)");

    auto create_device = reinterpret_cast<PFN_CreateDevice>(create);
    auto create_factory = reinterpret_cast<PFN_CreateFactory2>(GetProcAddress(dxgi, "CreateDXGIFactory2"));
    IDXGIFactory4 *factory = nullptr;
    if (FAILED(create_factory(0, IID_PPV_ARGS(&factory)))) { Log("no DXGI factory"); return false; }
    if (FAILED(create_device(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&g.dev)))) { Log("no D3D12 device"); return false; }
    factory->EnumAdapterByLuid(g.dev->GetAdapterLuid(), IID_PPV_ARGS(&g.adapter));
    g.hdr = ScreenIsHdr(factory);
#ifdef OVERLAY_TESTS
    if (g.test_sdr) g.hdr = false;
#endif
    g.fmt = g.hdr ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
    Log(g.hdr ? "HDR: the screen is in HDR mode, the overlay works in HDR (scRGB)" : "HDR: off (the screen is in SDR mode)");

    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    g.dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&g.queue));
    g.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g.alloc));
    g.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g.alloc, nullptr, IID_PPV_ARGS(&g.list));
    g.list->Close();
    for (auto &a : g.ring) g.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a));
    g.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g.fence));
    g.fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    InitTimestamps();

    // Overlay window over the game's client area: no frame, click-through, never activated, no taskbar
    // button. NOREDIRECTIONBITMAP: DirectComposition draws it, which is what lets a layered window show
    // a flip-model swapchain.
    RECT tr = {};
    GetClientRect(g.target, &tr);
    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"dlss5desktop";
    RegisterClassW(&wc);
    g.window = CreateWindowExW(kClickThrough | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP, wc.lpszClassName,
                               L"DLSS 5 overlay", WS_POPUP, 0, 0, std::max<LONG>(tr.right, 64),
                               std::max<LONG>(tr.bottom, 64), nullptr, nullptr, wc.hInstance, nullptr);
    SetLayeredWindowAttributes(g.window, 0, 255, LWA_ALPHA);

    DXGI_SWAP_CHAIN_DESC1 sd = {};
    sd.Width = std::max<UINT>(tr.right, 64);
    sd.Height = std::max<UINT>(tr.bottom, 64);
    sd.Format = g.fmt;
    sd.SampleDesc.Count = 1;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount = 6;
    sd.Scaling = DXGI_SCALING_STRETCH;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    IDXGISwapChain1 *sc1 = nullptr;
    HRESULT hr = factory->CreateSwapChainForComposition(g.queue, &sd, nullptr, &sc1);
    if (FAILED(hr)) { Log("no swapchain: 0x%08X", hr); return false; }
    sc1->QueryInterface(IID_PPV_ARGS(&g.swap));
    SetSwapColorSpace();
    factory->Release();
    if (FAILED(hr = DCompositionCreateDevice(nullptr, IID_PPV_ARGS(&g.dcomp))) ||
        FAILED(hr = g.dcomp->CreateTargetForHwnd(g.window, TRUE, &g.dcomp_target)) ||
        FAILED(hr = g.dcomp->CreateVisual(&g.dcomp_visual)) ||
        FAILED(hr = g.dcomp_visual->SetContent(sc1)) ||
        FAILED(hr = g.dcomp_target->SetRoot(g.dcomp_visual)) ||
        FAILED(hr = g.dcomp->Commit()))
    { Log("DirectComposition set-up failed: 0x%08X", hr); return false; }
    sc1->Release();
    for (UINT i = 0; i < 6; ++i) g.swap->GetBuffer(i, IID_PPV_ARGS(&g.back[i]));

    D3D12_FEATURE_DATA_FORMAT_SUPPORT fs = { g.fmt };
    g.dev->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &fs, sizeof(fs));
    if (!(fs.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))
        Log("WARNING: this GPU cannot write the picture format from a compute shader; DLSS may refuse the output");
    return true;
}

// ---------------------------------------------------------------------------------------------
// Windows Graphics Capture on a D3D11 device of the same GPU

// What Windows Graphics Capture hands over: scRGB FP16 on an HDR screen (the window as Windows composes it,
// highlights included), 8-bit BGRA otherwise.
static wdx::DirectXPixelFormat CaptureFormat()
{
    return g.hdr ? wdx::DirectXPixelFormat::R16G16B16A16Float : wdx::DirectXPixelFormat::B8G8R8A8UIntNormalized;
}

static bool InitCapture()
{
    IDXGIDevice *dxgi_dev = nullptr;
    IDXGIFactory4 *f = nullptr;
    IDXGIAdapter1 *adapter = nullptr;
    CreateDXGIFactory2(0, IID_PPV_ARGS(&f));
    f->EnumAdapterByLuid(g.dev->GetAdapterLuid(), IID_PPV_ARGS(&adapter));
    f->Release();
    ID3D11Device *d11 = nullptr;
    ID3D11DeviceContext *c11 = nullptr;
    const D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_1;
    HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, &fl, 1,
                                   D3D11_SDK_VERSION, &d11, nullptr, &c11);
    adapter->Release();
    if (FAILED(hr)) { Log("no D3D11 device for capture: 0x%08X", hr); return false; }
    d11->QueryInterface(IID_PPV_ARGS(&g.dev11));
    c11->QueryInterface(IID_PPV_ARGS(&g.ctx11));
    d11->Release();
    c11->Release();

    g.dev11->QueryInterface(IID_PPV_ARGS(&dxgi_dev));
    winrt::com_ptr<::IInspectable> insp;
    hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi_dev, insp.put());
    dxgi_dev->Release();
    if (FAILED(hr)) { Log("capture device not available: 0x%08X", hr); return false; }
    g.rt_device = insp.as<wd3d::IDirect3DDevice>();

    auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    hr = interop->CreateForWindow(g.target, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(g.item));
    if (FAILED(hr)) { Log("this window cannot be captured: 0x%08X", hr); return false; }

    const auto size = g.item.Size();
    g.pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(g.rt_device, CaptureFormat(), 3, size);
    g.pool_w = static_cast<UINT>(size.Width);
    g.pool_h = static_cast<UINT>(size.Height);
    g.session = g.pool.CreateCaptureSession(g.item);
    try { g.session.IsCursorCaptureEnabled(false); } catch (...) {}
    try { g.session.IsBorderRequired(false); } catch (...) {}
    g.session.StartCapture();
    Log("capturing \"%ls\" (%dx%d)", std::wstring(g.item.DisplayName()).c_str(), size.Width, size.Height);
    return true;
}

// ---------------------------------------------------------------------------------------------
// NGX

static bool InitNgx()
{
    const std::wstring dir = ExeDir();
    const wchar_t *paths[1] = { dir.c_str() };
    NVSDK_NGX_FeatureCommonInfo info = {};
    info.PathListInfo.Path = paths;
    info.PathListInfo.Length = 1;
    NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init(0x1000000ULL, dir.c_str(), g.dev, &info, NVSDK_NGX_Version_API);
    Log("NGX init: 0x%08X", r);
    if (NVSDK_NGX_FAILED(r)) return false;
    r = NVSDK_NGX_D3D12_GetCapabilityParameters(&g.params);
    if (NVSDK_NGX_FAILED(r)) { Log("NGX capabilities: 0x%08X", r); return false; }
    // A released feature's memory is freed, not kept: every window resize builds new features.
    NVSDK_NGX_Parameter_SetUI(g.params, NVSDK_NGX_Parameter_FreeMemOnReleaseFeature, 1);
    int dlss = 0;
    g.params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &dlss);
    Log("DLSS available: %s", dlss ? "yes" : "no");
    if (g.fg_frames > 0)
    {
        int fg = 0;
        unsigned int fg_max = 0;
        g.params->Get(NVSDK_NGX_Parameter_FrameGeneration_Available, &fg);
        g.params->Get(NVSDK_NGX_DLSSG_Parameter_MultiFrameCountMax, &fg_max);
        Log("frame generation available: %s, up to %u in-between frame(s)", fg ? "yes" : "no", fg_max);
        if (!fg) { Log("frame generation is not available on this GPU or driver: off"); g.fg_frames = 0; }
        else if (fg_max > 0 && g.fg_frames > fg_max)
        {
            Log("asked for %u in-between frames, this GPU allows %u", g.fg_frames, fg_max);
            g.fg_frames = fg_max;
        }
    }
    return dlss != 0;
}

// Finish (see App): two small compute shaders on D3D12, run between the frame generation evaluates.
static const char kFinishShaders[] = R"(
cbuffer C : register(b0) { uint2 size; uint hdr; uint unused; };
Texture2D<float4> real : register(t0);        // this real frame
Texture2D<float> still : register(t1);        // 1 where the picture did not change since the previous real frame
Texture2D<float4> generated : register(t2);   // frame generation's in-between frame
RWTexture2D<float4> dst : register(u0);

// HDR: the real frame squeezed into 0..1, which frame generation keeps (it clips at 1.0)
[numthreads(8, 8, 1)]
void Squeeze(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    const float4 c = real[id.xy];
    dst[id.xy] = float4(c.rgb / (1.0 + abs(c.rgb)), c.a);
}

// The in-between frame shown: still pixels from the real frame, the others from frame generation (expanded back in HDR,
// up to 10,000 nits)
[numthreads(8, 8, 1)]
void Finish(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    if (still[id.xy] > 0.5) { dst[id.xy] = real[id.xy]; return; }
    float4 c = generated[id.xy];
    if (hdr != 0) c.rgb = clamp(c.rgb / max(1.0 - abs(c.rgb), 1.0 / 128.0), -125.0, 125.0);
    dst[id.xy] = c;
}
)";

// Finish's descriptor heap: the textures it reads and writes, one slot each.
enum : UINT { SLOT_OUTPUT, SLOT_COLOR, SLOT_STILL, SLOT_RAW, SLOT_IN, SLOT_INTERP, SLOT_COUNT = SLOT_INTERP + 3 };

static void ReleaseFinish()
{
    Release(g.pso_squeeze);
    Release(g.pso_finish);
    Release(g.finish_root);
    Release(g.finish_heap);
}

// Once, when frame generation is asked for. Failing leaves the in-between frames as frame generation makes them.
static void InitFinish()
{
    D3D12_FEATURE_DATA_FORMAT_SUPPORT fs = { g.fmt };
    if (FAILED(g.dev->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &fs, sizeof(fs))) || !(fs.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))
    {
        Log("this GPU cannot write the picture format from a compute shader: in-between frames are shown as frame generation makes them");
        return;
    }
    D3D12_DESCRIPTOR_RANGE ranges[4] = {};
    D3D12_ROOT_PARAMETER params[5] = {};
    for (UINT i = 0; i < 4; ++i)   // t0, t1, t2, u0: a table each, so every pass picks its own textures
    {
        ranges[i].RangeType = i < 3 ? D3D12_DESCRIPTOR_RANGE_TYPE_SRV : D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        ranges[i].NumDescriptors = 1;
        ranges[i].BaseShaderRegister = i < 3 ? i : 0;
        params[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[i].DescriptorTable.NumDescriptorRanges = 1;
        params[i].DescriptorTable.pDescriptorRanges = &ranges[i];
    }
    params[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    params[4].Constants.Num32BitValues = 4;
    const D3D12_ROOT_SIGNATURE_DESC rd = { 5, params, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
    ID3DBlob *sig = nullptr, *errors = nullptr;
    // From the d3d12.dll InitD3D12 loaded: importing it would load d3d12.dll before ReShade (see the top of this file).
    const auto serialize = reinterpret_cast<PFN_D3D12_SERIALIZE_ROOT_SIGNATURE>(
        GetProcAddress(GetModuleHandleW(L"d3d12.dll"), "D3D12SerializeRootSignature"));
    if (serialize && SUCCEEDED(serialize(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &sig, &errors)))
        g.dev->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), IID_PPV_ARGS(&g.finish_root));
    Release(sig);
    Release(errors);
    for (auto [entry, pso] : { std::pair{ "Squeeze", &g.pso_squeeze }, std::pair{ "Finish", &g.pso_finish } })
    {
        ID3DBlob *code = nullptr;
        if (!g.finish_root) break;
        if (FAILED(D3DCompile(kFinishShaders, sizeof(kFinishShaders) - 1, "finish", nullptr, nullptr, entry, "cs_5_1",
                              D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors)))
            Log("shader %s: %s", entry, errors ? static_cast<const char *>(errors->GetBufferPointer()) : "compile failed");
        else
        {
            D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
            pd.pRootSignature = g.finish_root;
            pd.CS = { code->GetBufferPointer(), code->GetBufferSize() };
            g.dev->CreateComputePipelineState(&pd, IID_PPV_ARGS(pso));
        }
        Release(code);
        Release(errors);
    }
    const D3D12_DESCRIPTOR_HEAP_DESC hd = { D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, SLOT_COUNT, D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE, 0 };
    g.dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&g.finish_heap));
    g.finish_step = g.dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    if (!g.finish_root || !g.pso_squeeze || !g.pso_finish || !g.finish_heap)
    {
        Log("the in-between frames' finish pass could not be set up: they are shown as frame generation makes them");
        ReleaseFinish();
    }
}

// The textures of this size in Finish's heap; a missing one gets an empty slot.
static void WriteFinishDescriptors()
{
    const D3D12_CPU_DESCRIPTOR_HANDLE base = g.finish_heap->GetCPUDescriptorHandleForHeapStart();
    auto at = [&](UINT slot) { return D3D12_CPU_DESCRIPTOR_HANDLE{ base.ptr + static_cast<SIZE_T>(slot) * g.finish_step }; };
    auto srv = [&](ID3D12Resource *r, DXGI_FORMAT fmt, UINT slot)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC d = {};
        d.Format = fmt;
        d.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        d.Texture2D.MipLevels = 1;
        g.dev->CreateShaderResourceView(r, &d, at(slot));
    };
    auto uav = [&](ID3D12Resource *r, UINT slot)
    {
        D3D12_UNORDERED_ACCESS_VIEW_DESC d = {};
        d.Format = g.fmt;
        d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        g.dev->CreateUnorderedAccessView(r, nullptr, &d, at(slot));
    };
    srv(g.output, g.fmt, SLOT_OUTPUT);
    srv(g.color, g.fmt, SLOT_COLOR);
    srv(g.still, DXGI_FORMAT_R8_UNORM, SLOT_STILL);
    srv(g.fg_raw, g.fmt, SLOT_RAW);
    uav(g.fg_in, SLOT_IN);
    for (UINT i = 0; i < 3; ++i) uav(g.interp[i], SLOT_INTERP + i);
}

// One Finish pass on this frame's list: the real frame in t0 (SLOT_OUTPUT or SLOT_COLOR), the result in dst_slot.
static void RunFinish(ID3D12PipelineState *pso, UINT real_slot, UINT dst_slot)
{
    // An NGX evaluate leaves its own heaps, root signature and pipeline on the list: all are set again.
    ID3D12DescriptorHeap *heaps[] = { g.finish_heap };
    g.list->SetDescriptorHeaps(1, heaps);
    g.list->SetComputeRootSignature(g.finish_root);
    g.list->SetPipelineState(pso);
    const D3D12_GPU_DESCRIPTOR_HANDLE base = g.finish_heap->GetGPUDescriptorHandleForHeapStart();
    const UINT slots[4] = { real_slot, SLOT_STILL, SLOT_RAW, dst_slot };
    for (UINT i = 0; i < 4; ++i) g.list->SetComputeRootDescriptorTable(i, D3D12_GPU_DESCRIPTOR_HANDLE{ base.ptr + static_cast<UINT64>(slots[i]) * g.finish_step });
    const UINT c[4] = { g.w, g.h, g.hdr ? 1u : 0u, 0u };
    g.list->SetComputeRoot32BitConstants(4, 4, c, 0);
    g.list->Dispatch((g.w + 7) / 8, (g.h + 7) / 8, 1);
}

// The in-between frames' textures, Finish's included.
static void ReleaseFrameGenerationTextures()
{
    for (auto &t : g.interp) Release(t);
    Release(g.fg_raw);
    Release(g.fg_in);
}

// Frame generation for a picture of w x h: the DLSS-G feature and the textures it writes the in-between
// frames into. Failing leaves the overlay running without it.
static void BuildFrameGeneration(UINT w, UINT h)
{
    if (g.fg_frames == 0 || !g.params) return;
    for (UINT i = 0; i < g.fg_frames; ++i)
    {
        g.interp[i] = MakeTex(w, h, g.fmt, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE,
                              D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (!g.interp[i]) { Log("frame generation is off at %ux%u: its textures could not be made", w, h); ReleaseFrameGenerationTextures(); return; }
    }
    // Finish only has work when there is a still mask to apply or HDR to keep.
    if (g.pso_finish && (g.still || g.hdr))
    {
        g.fg_raw = MakeTex(w, h, g.fmt, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (g.hdr) g.fg_in = MakeTex(w, h, g.fmt, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (!g.fg_raw || (g.hdr && !g.fg_in)) { Release(g.fg_raw); Release(g.fg_in); }
        else WriteFinishDescriptors();
    }
    NVSDK_NGX_DLSSG_Create_Params cp = {};
    cp.Width = w;
    cp.Height = h;
    cp.NativeBackbufferFormat = g.fmt;
    cp.RenderWidth = w;
    cp.RenderHeight = h;
    g.alloc->Reset();
    g.list->Reset(g.alloc, nullptr);
    const NVSDK_NGX_Result r = NGX_D3D12_CREATE_DLSSG(g.list, 1, 1, &g.fg_feature, g.params, &cp);
    g.list->Close();
    ID3D12CommandList *lists[] = { g.list };
    g.queue->ExecuteCommandLists(1, lists);
    WaitGpu();
    Log("frame generation %ux (%u in-between frame(s)) %ux%u: 0x%08X", g.fg_frames + 1, g.fg_frames, w, h, r);
    if (NVSDK_NGX_FAILED(r))
    {
        // Nothing reads them without the feature, and the memory guard only frees frame generation that runs.
        g.fg_feature = nullptr;
        ReleaseFrameGenerationTextures();
    }
    static bool said = false;
    if (g.fg_feature && g.fg_raw && !said)
    {
        said = true;
        const char *hdr = "HDR kept above 80 nits (frame generation reads the frame squeezed into 0..1)";
        if (g.still && g.fg_in) Log("in-between frames: still pixels kept as they are, %s", hdr);
        else Log("in-between frames: %s", g.still ? "still pixels kept as they are" : hdr);
    }
    g.fg_have_prev = false;
    g.fg_now = g.fg_frames;
}

// Each evaluate leaves its textures in the shared NGX parameter block. They are cleared before those textures are released:
// the DLSS 5 add-on reads the block when the next feature is created, and a freed texture there crashed it at a window's
// first resize (3 of 20 test sessions).
static void ForgetFrameTextures()
{
    if (!g.params) return;
    for (const char *name : { NVSDK_NGX_Parameter_Color, NVSDK_NGX_Parameter_Output, NVSDK_NGX_Parameter_Depth, NVSDK_NGX_Parameter_MotionVectors,
                              NVSDK_NGX_DLSSG_Parameter_Backbuffer, NVSDK_NGX_DLSSG_Parameter_MVecs, NVSDK_NGX_DLSSG_Parameter_Depth,
                              NVSDK_NGX_DLSSG_Parameter_OutputInterpolated })
        NVSDK_NGX_Parameter_SetD3d12Resource(g.params, name, nullptr);
}

static void ReleaseFrameGeneration()
{
    ForgetFrameTextures();
    if (g.fg_feature) { NVSDK_NGX_D3D12_ReleaseFeature(g.fg_feature); g.fg_feature = nullptr; }
    ReleaseFrameGenerationTextures();
    g.fg_have_prev = false;
}

// ---------------------------------------------------------------------------------------------
// Sharing: a texture D3D11 creates and D3D12 opens

static HRESULT Share11(UINT w, UINT h, DXGI_FORMAT fmt, UINT bind, ID3D11Texture2D **tex11, ID3D12Resource **tex12)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = fmt;
    td.SampleDesc.Count = 1;
    td.BindFlags = bind;
    td.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    HRESULT hr = g.dev11->CreateTexture2D(&td, nullptr, tex11);
    IDXGIResource1 *res = nullptr;
    HANDLE handle = nullptr;
    if (SUCCEEDED(hr)) hr = (*tex11)->QueryInterface(IID_PPV_ARGS(&res));
    if (SUCCEEDED(hr)) hr = res->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle);
    Release(res);
    if (SUCCEEDED(hr)) hr = g.dev->OpenSharedHandle(handle, IID_PPV_ARGS(tex12));
    if (handle) CloseHandle(handle);
    return hr;
}

// ---------------------------------------------------------------------------------------------
// Motion. A game tells DLSS where every pixel moved; a captured picture does not, so the GPU's
// optical-flow engine (NVIDIA Optical Flow) measures it: this frame against the previous one, on a
// half-size greyscale copy. Its answer points from each block of this frame to where that block
// was in the previous one -- the direction DLSS motion vectors use -- in 1/32 pixel steps.

static const char kShaders[] = R"(
cbuffer C : register(b0) { uint2 size; float scale; uint cell; };

Texture2D<float4> color : register(t0);
RWTexture2D<unorm float> luma : register(u0);
[numthreads(8, 8, 1)]
void Luma(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    uint2 p = id.xy * 2;
    float3 c = (color[p].rgb + color[p + uint2(1, 0)].rgb + color[p + uint2(0, 1)].rgb + color[p + uint2(1, 1)].rgb) * 0.25;
    float l = dot(c, float3(0.299, 0.587, 0.114));
    luma[id.xy] = scale > 0.0 ? l / (1.0 + l) : l;   // scale > 0: HDR (scRGB), squeezed into 0..1
}

Texture2D<int2> flow : register(t0);
RWTexture2D<float2> mv : register(u0);
[numthreads(8, 8, 1)]
void Motion(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    uint fw, fh;
    flow.GetDimensions(fw, fh);
    float2 f = (float2(id.xy) + 0.5) / cell - 0.5;
    int2 i = int2(floor(f));
    float2 t = f - i;
    int2 hi = int2(fw - 1, fh - 1);
    float2 a = flow[clamp(i, 0, hi)], b = flow[clamp(i + int2(1, 0), 0, hi)];
    float2 c = flow[clamp(i + int2(0, 1), 0, hi)], d = flow[clamp(i + int2(1, 1), 0, hi)];
    mv[id.xy] = lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y) * scale;
}

// Still pixels, in three passes. Still: 1 where this frame's picture is the same as the previous frame's. Cover: 0 again
// where something moved over such a pixel between the two frames. Calm: no motion on the still pixels that are left
// (optical flow lends a still icon or progress bar the motion of the video under it).
Texture2D<float4> picture : register(t0);
Texture2D<float4> previous : register(t1);
Texture2D<float2> moved : register(t2);
RWTexture2D<unorm float> still : register(u0);
[numthreads(8, 8, 1)]
void Still(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    still[id.xy] = all(picture[id.xy].rgb == previous[id.xy].rgb) ? 1.0 : 0.0;
}

// Outside the picture both reads give 0: such a pixel counts as unchanged.
bool Moved(int2 r) { return any(picture[r].rgb != previous[r].rgb); }
bool InRange(float3 c, float3 lo, float3 hi)
{
    const float3 margin = 0.03 * max(abs(lo), abs(hi)) + 0.004;
    return all(c >= lo - margin) && all(c <= hi + margin);
}

// What moved from q (where it was in the previous frame) to p passes over the pixels between them in the in-between
// frames. A still pixel on that path that looks like what it covered around p or uncovered around q -- the page under
// scrolling text -- lies under it and is not kept still; one that does not -- an icon over a moving video -- is in front
// of it. The motion comes a frame late (see RunMotion), so q is looked for along the motion's line, within half of it
// either way, as the nearest pixel that changed; with none there, the content came out from under something still, which
// stays in front. "Looks like" is the colour range of the changed pixels around p (as they were) and q (as they are now):
// one colour each missed the gaps in a page that is not flat.
[numthreads(8, 8, 1)]
void Cover(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    const int2 p = int2(id.xy);
    const float3 covered = previous[p].rgb;
    if (all(picture[p].rgb == covered)) return;
    const float2 m = moved[p];
    const float len = max(abs(m.x), abs(m.y));
    if (len < 1.0) return;
    const uint reach = uint(ceil(len * 0.5));
    float t = -1.0;
    for (uint j = 0; j <= reach && t < 0.0; ++j)
        for (int side = 1; side >= -1; side -= 2)
        {
            const float f = 1.0 + side * float(j) / len;
            const int2 qq = int2(round(float2(p) + m * f));
            if (any(qq < 0) || any(qq >= int2(size))) continue;
            if (Moved(qq)) { t = f; break; }
        }
    if (t < 0.0) return;
    const float2 way = m * t;
    const int2 q = int2(round(float2(p) + way));
    const float3 uncovered = picture[q].rgb;
    float3 lo1 = covered, hi1 = covered, lo2 = uncovered, hi2 = uncovered;
    for (int dy = -1; dy <= 1; ++dy)
        for (int dx = -1; dx <= 1; ++dx)
        {
            const int2 d = int2(dx, dy);
            if (Moved(p + d)) { const float3 a = previous[p + d].rgb; lo1 = min(lo1, a); hi1 = max(hi1, a); }
            if (Moved(q + d)) { const float3 b = picture[q + d].rgb; lo2 = min(lo2, b); hi2 = max(hi2, b); }
        }
    const uint steps = min(uint(ceil(len * t)), 256u);
    for (uint k = 1; k < steps; ++k)
    {
        const int2 r = int2(round(float2(p) + way * (float(k) / steps)));
        const float3 c = picture[r].rgb;
        if (InRange(c, lo1, hi1) || InRange(c, lo2, hi2)) still[r] = 0.0;
    }
}

Texture2D<float> kept : register(t0);
RWTexture2D<float2> motion : register(u0);
[numthreads(8, 8, 1)]
void Calm(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= size)) return;
    if (kept[id.xy] > 0.5) motion[id.xy] = float2(0.0, 0.0);
}

// Down to the processing size: one triangle over the whole target, sampled with a linear filter (at 50 % that
// is an even 2x2 average).
Texture2D<float4> captured : register(t0);
SamplerState linear_clamp : register(s0);
float4 FullVS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 DownPS(float4 pos : SV_Position) : SV_Target
{
    return captured.SampleLevel(linear_clamp, pos.xy / float2(size), 0);
}
)";

struct MotionConstants { UINT w, h; float scale; UINT cell; };

static ID3DBlob *CompileBlob(const char *entry, const char *profile)
{
    ID3DBlob *code = nullptr, *errors = nullptr;
    HRESULT hr = D3DCompile(kShaders, sizeof(kShaders) - 1, "overlay", nullptr, nullptr, entry, profile,
                            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    if (FAILED(hr)) Log("shader %s: %s", entry, errors ? static_cast<const char *>(errors->GetBufferPointer()) : "compile failed");
    Release(errors);
    return code;
}

static ID3D11ComputeShader *CompileCS(const char *entry)
{
    ID3D11ComputeShader *cs = nullptr;
    if (ID3DBlob *code = CompileBlob(entry, "cs_5_0"))
    {
        g.dev11->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &cs);
        code->Release();
    }
    return cs;
}

// Once: the optical-flow API from the driver, and the two small shaders.
static void InitMotion()
{
    g.nvof_dll = LoadLibraryW(L"nvofapi64.dll");
    auto create = g.nvof_dll ? reinterpret_cast<decltype(&NvOFAPICreateInstanceD3D11)>(GetProcAddress(g.nvof_dll, "NvOFAPICreateInstanceD3D11")) : nullptr;
    if (!create || create(NV_OF_API_VERSION, &g.of) != NV_OF_SUCCESS) { Log("optical flow is not available: no motion"); g.nvof_dll = nullptr; return; }
    g.cs_luma = CompileCS("Luma");
    g.cs_mv = CompileCS("Motion");
    D3D11_BUFFER_DESC bd = { 16, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER };
    g.dev11->CreateBuffer(&bd, nullptr, &g.cb);
    if (!g.cs_luma || !g.cs_mv || !g.cb) { Log("motion shaders not ready: no motion"); g.nvof_dll = nullptr; }
}

// Once: the still-pixel shaders, when there is motion to tell what moved.
static void InitStill()
{
    if (!g.nvof_dll) return;
    g.cs_still = CompileCS("Still");
    g.cs_cover = CompileCS("Cover");
    g.cs_calm = CompileCS("Calm");
    if (!g.cs_still || !g.cs_cover || !g.cs_calm)
    {
        Log("still-pixel shaders not ready: still pixels are not kept");
        Release(g.cs_still);
        Release(g.cs_cover);
        Release(g.cs_calm);
    }
}

static void ReleaseScaler()
{
    Release(g.cap_srv);
    Release(g.cap11);
    Release(g.color_rtv);
}

// The full-size copy of the capture and the pass that shrinks it into color11 (shaders made once).
static bool BuildScaler(UINT cw, UINT ch)
{
    ReleaseScaler();
    if (!g.vs_full)
    {
        ID3DBlob *vs = CompileBlob("FullVS", "vs_5_0"), *ps = CompileBlob("DownPS", "ps_5_0");
        if (vs) g.dev11->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &g.vs_full);
        if (ps) g.dev11->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &g.ps_down);
        Release(vs);
        Release(ps);
        D3D11_SAMPLER_DESC sd = {};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        g.dev11->CreateSamplerState(&sd, &g.linear_clamp);
        D3D11_BUFFER_DESC bd = { 16, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER };
        g.dev11->CreateBuffer(&bd, nullptr, &g.scale_cb);
    }
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = cw;
    td.Height = ch;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = g.fmt;
    td.SampleDesc.Count = 1;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    g.dev11->CreateTexture2D(&td, nullptr, &g.cap11);
    if (g.cap11) g.dev11->CreateShaderResourceView(g.cap11, nullptr, &g.cap_srv);
    if (g.color11) g.dev11->CreateRenderTargetView(g.color11, nullptr, &g.color_rtv);
    const bool ok = g.vs_full && g.ps_down && g.linear_clamp && g.scale_cb && g.cap_srv && g.color_rtv;
    if (!ok) Log("the scaling pass could not be set up: processing at full size");
    return ok;
}

// captured client area (cap11) -> color11 at the processing size
static void ScaleDown()
{
    const MotionConstants c = { g.w, g.h, 0.0f, 0 };
    g.ctx11->UpdateSubresource(g.scale_cb, 0, nullptr, &c, 0, 0);
    const D3D11_VIEWPORT vp = { 0.0f, 0.0f, static_cast<float>(g.w), static_cast<float>(g.h), 0.0f, 1.0f };
    g.ctx11->RSSetViewports(1, &vp);
    g.ctx11->OMSetRenderTargets(1, &g.color_rtv, nullptr);
    g.ctx11->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g.ctx11->IASetInputLayout(nullptr);
    g.ctx11->VSSetShader(g.vs_full, nullptr, 0);
    g.ctx11->PSSetShader(g.ps_down, nullptr, 0);
    g.ctx11->PSSetConstantBuffers(0, 1, &g.scale_cb);
    g.ctx11->PSSetShaderResources(0, 1, &g.cap_srv);
    g.ctx11->PSSetSamplers(0, 1, &g.linear_clamp);
    g.ctx11->Draw(3, 0);
    ID3D11ShaderResourceView *no_srv = nullptr;
    ID3D11RenderTargetView *no_rtv = nullptr;
    g.ctx11->PSSetShaderResources(0, 1, &no_srv);
    g.ctx11->OMSetRenderTargets(1, &no_rtv, nullptr);
}

// The compositor stretches the processing-size picture over the window.
static void StretchToWindow()
{
    D2D_MATRIX_3X2_F m = {};
    m._11 = static_cast<float>(g.cw) / g.w;
    m._22 = static_cast<float>(g.ch) / g.h;
    g.dcomp_visual->SetBitmapInterpolationMode(DCOMPOSITION_BITMAP_INTERPOLATION_MODE_LINEAR);
    g.dcomp_visual->SetTransform(m);
    g.dcomp->Commit();
}

static void ReleaseMotion()
{
    g.motion_ready = false;
    if (g.of_handle)
    {
        for (auto &b : g.luma_of) if (b) { g.of.nvOFUnregisterResourceD3D11(b); b = nullptr; }
        if (g.flow_of) { g.of.nvOFUnregisterResourceD3D11(g.flow_of); g.flow_of = nullptr; }
        g.of.nvOFDestroy(g.of_handle);
        g.of_handle = nullptr;
    }
    for (int i = 0; i < 2; ++i) { Release(g.luma_uav[i]); Release(g.luma[i]); }
    Release(g.flow_srv);
    Release(g.flow);
    Release(g.color_srv);
    Release(g.mv_uav);
}

static ID3D11Texture2D *Tex11(UINT w, UINT h, DXGI_FORMAT fmt, UINT bind)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = fmt;
    td.SampleDesc.Count = 1;
    td.BindFlags = bind;
    ID3D11Texture2D *t = nullptr;
    g.dev11->CreateTexture2D(&td, nullptr, &t);
    return t;
}

// Per picture size: an optical-flow session at half size, and the textures around it.
static void BuildMotion(UINT w, UINT h)
{
    ReleaseMotion();
    if (g.mv11) g.dev11->CreateUnorderedAccessView(g.mv11, nullptr, &g.mv_uav);
    if (g.mv_uav) { const float zero[4] = {}; g.ctx11->ClearUnorderedAccessViewFloat(g.mv_uav, zero); }   // no motion until measured
    if (!g.nvof_dll) return;

    const UINT lw = w / 2, lh = h / 2;
    NV_OF_STATUS s = g.of.nvCreateOpticalFlowD3D11(g.dev11, g.ctx11, &g.of_handle);
    for (UINT grid : { 2u, 4u })
    {
        if (s != NV_OF_SUCCESS) break;
        NV_OF_INIT_PARAMS ip = {};
        ip.width = lw;
        ip.height = lh;
        ip.outGridSize = static_cast<NV_OF_OUTPUT_VECTOR_GRID_SIZE>(grid);
        ip.mode = NV_OF_MODE_OPTICALFLOW;
        ip.perfLevel = NV_OF_PERF_LEVEL_MEDIUM;
        ip.predDirection = NV_OF_PRED_DIRECTION_FORWARD;
        ip.inputBufferFormat = NV_OF_BUFFER_FORMAT_GRAYSCALE8;
        s = g.of.nvOFInit(g.of_handle, &ip);
        if (s == NV_OF_SUCCESS) { g.grid = grid; break; }
    }
    if (s != NV_OF_SUCCESS) { Log("optical flow could not start (%d): no motion", s); ReleaseMotion(); return; }

    for (int i = 0; i < 2; ++i)
    {
        g.luma[i] = Tex11(lw, lh, DXGI_FORMAT_R8_UNORM, D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE);
        if (g.luma[i]) g.dev11->CreateUnorderedAccessView(g.luma[i], nullptr, &g.luma_uav[i]);
        if (g.luma[i] && g.of.nvOFRegisterResourceD3D11(g.of_handle, g.luma[i], &g.luma_of[i]) != NV_OF_SUCCESS) g.luma_of[i] = nullptr;
    }
    g.flow = Tex11((lw + g.grid - 1) / g.grid, (lh + g.grid - 1) / g.grid, DXGI_FORMAT_R16G16_SINT,
                   D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE);
    if (g.flow)
    {
        g.dev11->CreateShaderResourceView(g.flow, nullptr, &g.flow_srv);
        if (g.of.nvOFRegisterResourceD3D11(g.of_handle, g.flow, &g.flow_of) != NV_OF_SUCCESS) g.flow_of = nullptr;
    }
    g.dev11->CreateShaderResourceView(g.color11, nullptr, &g.color_srv);
    if (!g.luma_of[0] || !g.luma_of[1] || !g.flow_of || !g.flow_srv || !g.color_srv || !g.mv_uav || !g.luma_uav[0] || !g.luma_uav[1])
    { Log("optical flow textures not ready: no motion"); ReleaseMotion(); return; }
    g.have_prev = false;
    g.cur = 0;
    g.motion_ready = true;
    Log("motion: optical flow at %ux%u, one vector per %ux%u pixels", lw, lh, g.grid * 2, g.grid * 2);
}

static void Dispatch(ID3D11ComputeShader *cs, ID3D11ShaderResourceView *srv, ID3D11UnorderedAccessView *uav, UINT w, UINT h,
                     float scale, UINT cell)
{
    const MotionConstants c = { w, h, scale, cell };
    g.ctx11->UpdateSubresource(g.cb, 0, nullptr, &c, 0, 0);
    g.ctx11->CSSetShader(cs, nullptr, 0);
    g.ctx11->CSSetConstantBuffers(0, 1, &g.cb);
    g.ctx11->CSSetShaderResources(0, 1, &srv);
    g.ctx11->CSSetUnorderedAccessViews(0, 1, &uav, nullptr);
    g.ctx11->Dispatch((w + 7) / 8, (h + 7) / 8, 1);
    ID3D11ShaderResourceView *no_srv = nullptr;
    ID3D11UnorderedAccessView *no_uav = nullptr;
    g.ctx11->CSSetShaderResources(0, 1, &no_srv);
    g.ctx11->CSSetUnorderedAccessViews(0, 1, &no_uav, nullptr);
}

// After the new picture is in color11: measure its motion into mv11. False when nothing was measured (no motion).
static bool RunMotion()
{
    if (!g.motion_ready) return false;
    Dispatch(g.cs_luma, g.color_srv, g.luma_uav[g.cur], g.w / 2, g.h / 2, g.hdr ? 1.0f : 0.0f, 0);
    bool measured = false;
    if (g.have_prev && g.motion_on)
    {
        NV_OF_EXECUTE_INPUT_PARAMS in = {};
        in.inputFrame = g.luma_of[g.cur];
        in.referenceFrame = g.luma_of[1 - g.cur];
        NV_OF_EXECUTE_OUTPUT_PARAMS out = {};
        out.outputBuffer = g.flow_of;
        const NV_OF_STATUS s = g.of.nvOFExecute(g.of_handle, &in, &out);
        static bool said = false;
        if (s != NV_OF_SUCCESS && !said) { Log("optical flow failed (%d)", s); said = true; }
        if (s == NV_OF_SUCCESS)
        {
            // Known limit: on D3D11 the engine's answer reaches flow only at a later flush ("asynchronous", says its header;
            // neither a D3D11 query nor a read-back waits for it), so this reads the previous frame's answer and the motion
            // is a frame late (measured on the scrolling test page). The D3D12 interface, with its fences, would fix it.
            // half-size flow in 1/32 pixel -> full-size pixels: x 2 / 32
            Dispatch(g.cs_mv, g.flow_srv, g.mv_uav, g.w, g.h, 2.0f / 32.0f, g.grid * 2);
            measured = true;
        }
    }
    if (!measured) { const float zero[4] = {}; g.ctx11->ClearUnorderedAccessViewFloat(g.mv_uav, zero); }
    g.have_prev = true;
    g.cur = 1 - g.cur;
    return measured;
}

static void ReleaseStill()
{
    Release(g.prev_srv);
    Release(g.pic_srv);
    Release(g.mv_srv);
    Release(g.still_srv);
    Release(g.prev11);
    Release(g.still_uav);
    Release(g.still11);
    Release(g.still);
    g.have_prev_picture = false;
}

// Per picture size: a copy of the previous picture, and the still mask D3D11 writes and D3D12 reads.
static void BuildStill(UINT w, UINT h)
{
    ReleaseStill();
    if (!g.cs_still || !g.mv_uav) return;
    g.prev11 = Tex11(w, h, g.fmt, D3D11_BIND_SHADER_RESOURCE);
    if (g.prev11) g.dev11->CreateShaderResourceView(g.prev11, nullptr, &g.prev_srv);
    g.dev11->CreateShaderResourceView(g.color11, nullptr, &g.pic_srv);
    g.dev11->CreateShaderResourceView(g.mv11, nullptr, &g.mv_srv);
    if (SUCCEEDED(Share11(w, h, DXGI_FORMAT_R8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, &g.still11, &g.still)))
    {
        g.dev11->CreateUnorderedAccessView(g.still11, nullptr, &g.still_uav);
        g.dev11->CreateShaderResourceView(g.still11, nullptr, &g.still_srv);
    }
    if (g.still_uav) { const float zero[4] = {}; g.ctx11->ClearUnorderedAccessViewFloat(g.still_uav, zero); }
    if (!g.prev_srv || !g.pic_srv || !g.mv_srv || !g.still_uav || !g.still_srv)
    { Log("still-pixel textures not ready: still pixels are not kept"); ReleaseStill(); }
}

// After RunMotion: which pixels did not change since the previous frame and had nothing move over them (they get no
// motion), then this picture becomes the previous one. Without measured motion none is kept still.
static void RunStill(bool measured)
{
    if (!g.still_uav) return;
    const UINT gx = (g.w + 7) / 8, gy = (g.h + 7) / 8;
    if (g.have_prev_picture && measured)
    {
        const MotionConstants c = { g.w, g.h, 0.0f, 0 };
        g.ctx11->UpdateSubresource(g.cb, 0, nullptr, &c, 0, 0);
        g.ctx11->CSSetConstantBuffers(0, 1, &g.cb);
        ID3D11ShaderResourceView *srvs[3] = { g.pic_srv, g.prev_srv, g.mv_srv };
        g.ctx11->CSSetShaderResources(0, 3, srvs);
        g.ctx11->CSSetUnorderedAccessViews(0, 1, &g.still_uav, nullptr);
        g.ctx11->CSSetShader(g.cs_still, nullptr, 0);
        g.ctx11->Dispatch(gx, gy, 1);
        g.ctx11->CSSetShader(g.cs_cover, nullptr, 0);
        g.ctx11->Dispatch(gx, gy, 1);
        ID3D11ShaderResourceView *no_srvs[3] = {};
        ID3D11UnorderedAccessView *no_uav = nullptr;
        g.ctx11->CSSetShaderResources(0, 3, no_srvs);
        g.ctx11->CSSetUnorderedAccessViews(0, 1, &no_uav, nullptr);
        g.ctx11->CSSetShaderResources(0, 1, &g.still_srv);
        g.ctx11->CSSetUnorderedAccessViews(0, 1, &g.mv_uav, nullptr);
        g.ctx11->CSSetShader(g.cs_calm, nullptr, 0);
        g.ctx11->Dispatch(gx, gy, 1);
        g.ctx11->CSSetShaderResources(0, 1, no_srvs);
        g.ctx11->CSSetUnorderedAccessViews(0, 1, &no_uav, nullptr);
    }
    else
    {
        const float zero[4] = {};
        g.ctx11->ClearUnorderedAccessViewFloat(g.still_uav, zero);
    }
    g.ctx11->CopyResource(g.prev11, g.color11);
    g.have_prev_picture = true;
}

static void ReleaseFrameResources()
{
    WaitGpu();
    ForgetFrameTextures();
    if (g.feature) { NVSDK_NGX_D3D12_ReleaseFeature(g.feature); g.feature = nullptr; }
    ReleaseFrameGeneration();
    ReleaseMotion();
    ReleaseStill();
    ReleaseScaler();
    g.cw = g.ch = 0;
    Release(g.mv11);
    Release(g.color11);
    Release(g.copy_fence11);
    Release(g.color);
    Release(g.output);
    Release(g.depth);
    Release(g.mv);
    Release(g.copy_fence);
}

// Textures and the DLSS feature for a picture of w x h.
static bool BuildFrameResources(UINT w, UINT h)
{
    ReleaseFrameResources();
    g.w = w;
    g.h = h;
    // g.color is not made here: D3D11 creates the captured picture and D3D12 opens it below (Share11). Making it here
    // too left that texture behind on every rebuild, one full picture of graphics memory per window resize.
    g.output = MakeTex(w, h, g.fmt, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE,
                       D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    g.depth = MakeTex(w, h, DXGI_FORMAT_R32_FLOAT, D3D12_RESOURCE_FLAG_NONE, D3D12_HEAP_FLAG_NONE,
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    if (!g.output || !g.depth) return false;

    // The captured picture and the motion cross from D3D11 to D3D12 through shared textures and a shared
    // fence. D3D11 creates them: this driver refuses to open D3D12-created textures on D3D11 (E_INVALIDARG).
    const char *step = "share the picture";
    HRESULT hr = Share11(w, h, g.fmt, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, &g.color11, &g.color);
    if (SUCCEEDED(hr)) { step = "share the motion"; hr = Share11(w, h, DXGI_FORMAT_R16G16_FLOAT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, &g.mv11, &g.mv); }
    HANDLE hf = nullptr;
    g.copy_value = 0;
    if (SUCCEEDED(hr)) { step = "create the fence"; hr = g.dev11->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g.copy_fence11)); }
    if (SUCCEEDED(hr)) { step = "share the fence"; hr = g.copy_fence11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &hf); }
    if (SUCCEEDED(hr)) { step = "open the fence on D3D12"; hr = g.dev->OpenSharedHandle(hf, IID_PPV_ARGS(&g.copy_fence)); }
    if (hf) CloseHandle(hf);
    if (FAILED(hr))
    {
        Log("sharing with D3D12 failed at \"%s\": 0x%08X", step, hr);
        return false;
    }
    BuildMotion(w, h);
    BuildStill(w, h);

    NVSDK_NGX_DLSS_Create_Params cp = {};
    cp.Feature.InWidth = w;
    cp.Feature.InHeight = h;
    cp.Feature.InTargetWidth = w;
    cp.Feature.InTargetHeight = h;
    cp.Feature.InPerfQualityValue = NVSDK_NGX_PerfQuality_Value_DLAA;
    cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure |
                              NVSDK_NGX_DLSS_Feature_Flags_DepthInverted | (g.hdr ? NVSDK_NGX_DLSS_Feature_Flags_IsHDR : 0);
    // The previous size's evaluate left its render size in the block too: the new size, so the add-on reads a picture
    // that matches the feature.
    NVSDK_NGX_Parameter_SetUI(g.params, NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Width, w);
    NVSDK_NGX_Parameter_SetUI(g.params, NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Height, h);
    g.alloc->Reset();
    g.list->Reset(g.alloc, nullptr);
    NVSDK_NGX_Result r = NGX_D3D12_CREATE_DLSS_EXT(g.list, 1, 1, &g.feature, g.params, &cp);
    g.list->Close();
    ID3D12CommandList *lists[] = { g.list };
    g.queue->ExecuteCommandLists(1, lists);
    WaitGpu();
    Log("DLSS feature %ux%u DLAA: 0x%08X", w, h, r);
    g.reset = true;
    BuildFrameGeneration(w, h);
    return NVSDK_NGX_SUCCEED(r) && g.feature != nullptr;
}

// ---------------------------------------------------------------------------------------------
// One frame: newest capture -> color, DLSS -> output, output -> back buffer

static void ResizeSwapchain(UINT w, UINT h)
{
    WaitGpu();
    for (auto &b : g.back) Release(b);
    g.swap->ResizeBuffers(6, w, h, g.fmt, 0);
    SetSwapColorSpace();
    for (UINT i = 0; i < 6; ++i) g.swap->GetBuffer(i, IID_PPV_ARGS(&g.back[i]));
}

// Copies a finished frame into the next back buffer and queues it to be shown at a screen refresh. The
// queue holds several, so this only blocks when it is full; the next frame is processed meanwhile.
static void PresentTexture(ID3D12Resource *src, D3D12_RESOURCE_STATES state)
{
    ID3D12Resource *bb = g.back[g.swap->GetCurrentBackBufferIndex()];
    BeginList();
    if (state != D3D12_RESOURCE_STATE_COMMON) Barrier(src, state, D3D12_RESOURCE_STATE_COPY_SOURCE);
    Barrier(bb, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
    g.list->CopyResource(bb, src);
    Barrier(bb, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
    if (state != D3D12_RESOURCE_STATE_COMMON) Barrier(src, D3D12_RESOURCE_STATE_COPY_SOURCE, state);
    EndList();
    g.swap->Present(1, 0);
}

// The screen's refresh rate where the game window is.
static double RefreshRate()
{
    MONITORINFOEXW mi = {};
    mi.cbSize = sizeof(mi);
    DEVMODEW dm = {};
    dm.dmSize = sizeof(dm);
    if (GetMonitorInfoW(MonitorFromWindow(g.target, MONITOR_DEFAULTTONEAREST), &mi) &&
        EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm) && dm.dmDisplayFrequency > 1)
        return dm.dmDisplayFrequency;
    return 60.0;
}

// How many in-between frames: real frames times (1 + generated) may not exceed the screen's refresh rate, or the
// queue fills and holds the real frames back. For each multiplier, real frames come at the slower of the game's
// pace and the overlay's own (GPU cost of DLSS + the neural pass, plus each in-between frame), and the screen shows
// at most its refresh rate; the multiplier that shows the most frames wins.
// The measured costs move with the choice itself (the game, the neural pass and the in-between frames share the
// GPU: 13 ms a frame at 2x became 25 ms at 4x in Bloodborne), so the choice is kept calm: another multiplier must
// promise at least 10 % more frames on screen, hold for a second, and at most one change every 10 seconds.
// Fewer in-between frames for as many frames on screen is always worth it: more of the real frames are shown and
// fewer are guessed. Without that, the start at the chosen 4x stayed there for a 60 fps YouTube video on a 120 Hz
// screen (2x shows the same 120 frames), and half of the video's real frames were dropped: it juddered and flickered.
static void ChooseFrameGeneration()
{
    if (!g.fg_feature || g.game_interval <= 0.0) { g.fg_now = g.fg_frames; return; }
    auto shown_with = [](UINT n, double *real_interval)
    {
        const double work_s = (g.base_ms + n * g.fg_ms) / 1000.0;
        *real_interval = std::max(g.game_interval, work_s);
        return std::min(g.refresh_hz, (n + 1) / *real_interval);
    };
    double interval = g.game_interval, ignored = 0.0;
    UINT want = 0;
    double best_shown = 0.0;
    for (UINT n = 0; n <= g.fg_frames; ++n)
    {
        double ri = 0.0;
        const double shown = shown_with(n, &ri);
        if (shown > best_shown * 1.05) { best_shown = shown; want = n; interval = ri; }
    }
    const double shown_now = shown_with(g.fg_now, &ignored);
    const bool fewer_for_as_many = want < g.fg_now && best_shown >= shown_now * 0.95;
    if (want != g.fg_now && !fewer_for_as_many && best_shown < shown_now * 1.10) want = g.fg_now;   // not worth a change
    static UINT pending = UINT_MAX;
    static int held = 0;
    static ULONGLONG last_change = 0;
    if (want == g.fg_now) { held = 0; return; }
    if (want != pending) { pending = want; held = 0; }
    if (++held < 30 || GetTickCount64() - last_change < 10000) return;
    last_change = GetTickCount64();
    Log("frame generation now %ux: the game runs at %.0f fps on a %.0f Hz screen%s%s", want + 1, 1.0 / interval, g.refresh_hz,
        interval > g.game_interval * 1.01 ? " (the overlay's own pace)" : "", want < g.fg_frames ? " (more would not fit on the screen)" : "");
    g.fg_now = want;
    held = 0;
}

// Called once a second while the overlay draws.
static void CheckMemory()
{
    if (!g.adapter) return;
    DXGI_QUERY_VIDEO_MEMORY_INFO mi = {};
    if (FAILED(g.adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mi))) return;
    static bool said = false;
    if (!said) { said = true; Log("memory: budget %llu MB, the overlay uses %llu MB", mi.Budget >> 20, mi.CurrentUsage >> 20); }
    if (mi.CurrentUsage <= mi.Budget) { g.over_budget_s = 0; return; }
    ++g.over_budget_s;
    if (g.over_budget_s == 3 && g.fg_feature)
    {
        WaitGpu();
        ReleaseFrameGeneration();
        g.fg_frames = g.fg_now = 0;
        Log("memory: graphics memory is full (budget %llu MB, the overlay needs %llu MB): frame generation turned off",
            mi.Budget >> 20, mi.CurrentUsage >> 20);
        g.over_budget_s = 0;
    }
    else if (g.over_budget_s >= 5 && !g.memory_warned)
    {
        g.memory_warned = true;
        Log("memory: graphics memory is still full (budget %llu MB, the overlay needs %llu MB): lower the game's settings or use Performance",
            mi.Budget >> 20, mi.CurrentUsage >> 20);
    }
}

// A graphics driver reset (TDR) removes both devices: nothing drawn after it reaches the screen, and the last picture
// would stay frozen over the window (the GPU timings stop too, so the step-aside guard never sees it). Close instead.
static bool DeviceLost()
{
    const HRESULT r12 = g.dev->GetDeviceRemovedReason();
    const HRESULT r11 = g.dev11 ? g.dev11->GetDeviceRemovedReason() : S_OK;
    if (SUCCEEDED(r12) && SUCCEEDED(r11)) return false;
    Log("error: the graphics driver restarted (0x%08X / 0x%08X), so the overlay closed; start it again", r12, r11);
    return true;
}

#ifdef OVERLAY_TESTS
// --test-dump: from 8 seconds after the first frame, kDumpFrames frames in a row are written to the folder as they are sent
// to the screen: <n>-in.raw (the picture DLSS reads), <n>-mv.raw (its motion), <n>-still.raw (the still mask), <n>-out.raw
// (DLSS 5's answer) and <n>-fg1.raw, <n>-fg2.raw ... (the in-between frames shown before it). Each file: "DMP1", width,
// height, DXGI format (four 32-bit numbers), then the rows. The GPU copies each texture as the frame is made; the files are
// written after the last frame, so writing them does not hold up the frames in between (a held-up frame shows the page twice
// as far along).
struct PendingDump
{
    std::wstring name;
    ID3D11Texture2D *staging = nullptr;   // or
    ID3D12Resource *readback = nullptr;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
};
static const int kDumpFrames = 5;
static PendingDump g_dumps[kDumpFrames * 8];
static UINT g_dump_count = 0;

static void WriteRaw(const std::wstring &name, UINT w, UINT h, DXGI_FORMAT fmt, const BYTE *rows, UINT64 pitch)
{
    const UINT bpp = fmt == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : fmt == DXGI_FORMAT_R8_UNORM ? 1 : 4;   // or BGRA8, R16G16_FLOAT
    FILE *f = _wfsopen((g.test_dump + L"\\" + name).c_str(), L"wb", _SH_DENYNO);
    if (!f) { Log("test: cannot write %ls", name.c_str()); return; }
    const UINT32 head[4] = { 0x31504D44, w, h, static_cast<UINT32>(fmt) };
    fwrite(head, sizeof(head), 1, f);
    for (UINT y = 0; y < h; ++y) fwrite(rows + y * pitch, static_cast<size_t>(w) * bpp, 1, f);
    fclose(f);
}

static void Dump11(ID3D11Texture2D *tex, const std::wstring &name)
{
    D3D11_TEXTURE2D_DESC td = {};
    tex->GetDesc(&td);
    td.Usage = D3D11_USAGE_STAGING;
    td.BindFlags = 0;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    td.MiscFlags = 0;
    ID3D11Texture2D *staging = nullptr;
    if (g_dump_count == _countof(g_dumps) || FAILED(g.dev11->CreateTexture2D(&td, nullptr, &staging)))
    { Log("test: no staging texture for %ls", name.c_str()); return; }
    g.ctx11->CopyResource(staging, tex);
    g_dumps[g_dump_count].name = name;
    g_dumps[g_dump_count++].staging = staging;
}

static void Dump12(ID3D12Resource *tex, D3D12_RESOURCE_STATES state, const std::wstring &name)
{
    const D3D12_RESOURCE_DESC rd = tex->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
    UINT64 total = 0;
    g.dev->GetCopyableFootprints(&rd, 0, 1, 0, &fp, nullptr, nullptr, &total);
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width = total;
    bd.Height = bd.DepthOrArraySize = bd.MipLevels = 1;
    bd.SampleDesc.Count = 1;
    bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ID3D12Resource *buf = nullptr;
    if (g_dump_count == _countof(g_dumps) ||
        FAILED(g.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&buf))))
    { Log("test: no readback buffer for %ls", name.c_str()); return; }
    BeginList();
    Barrier(tex, state, D3D12_RESOURCE_STATE_COPY_SOURCE);
    D3D12_TEXTURE_COPY_LOCATION dst = {}, src = {};
    dst.pResource = buf;
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint = fp;
    src.pResource = tex;
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    g.list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    Barrier(tex, D3D12_RESOURCE_STATE_COPY_SOURCE, state);
    EndList();
    g_dumps[g_dump_count].name = name;
    g_dumps[g_dump_count].readback = buf;
    g_dumps[g_dump_count++].fp = fp;
}

static void WriteDumps()
{
    WaitGpu();
    for (UINT i = 0; i < g_dump_count; ++i)
    {
        PendingDump &d = g_dumps[i];
        if (d.staging)
        {
            D3D11_TEXTURE2D_DESC td = {};
            d.staging->GetDesc(&td);
            D3D11_MAPPED_SUBRESOURCE m = {};
            if (SUCCEEDED(g.ctx11->Map(d.staging, 0, D3D11_MAP_READ, 0, &m)))
            {
                WriteRaw(d.name, td.Width, td.Height, td.Format, static_cast<const BYTE *>(m.pData), m.RowPitch);
                g.ctx11->Unmap(d.staging, 0);
            }
        }
        else
        {
            void *p = nullptr;
            if (SUCCEEDED(d.readback->Map(0, nullptr, &p)))
            {
                WriteRaw(d.name, d.fp.Footprint.Width, d.fp.Footprint.Height, d.fp.Footprint.Format,
                         static_cast<const BYTE *>(p) + d.fp.Offset, d.fp.Footprint.RowPitch);
                const D3D12_RANGE none = { 0, 0 };
                d.readback->Unmap(0, &none);
            }
        }
        Release(d.staging);
        Release(d.readback);
    }
    g_dump_count = 0;
}

static void TestDump(ID3D12Resource *real, D3D12_RESOURCE_STATES real_state, UINT generated)
{
    static ULONGLONG start = 0;
    static int done = 0;
    if (g.test_dump.empty() || done >= kDumpFrames) return;
    if (!start) start = GetTickCount64();
    if (GetTickCount64() - start < 8000) return;
    const std::wstring n = std::to_wstring(done++);
    Dump11(g.color11, n + L"-in.raw");
    Dump11(g.mv11, n + L"-mv.raw");
    if (g.still11) Dump11(g.still11, n + L"-still.raw");
    if (g.flow) Dump11(g.flow, n + L"-flow.raw");
    Dump12(real, real_state, n + L"-out.raw");
    for (UINT i = 0; i < generated; ++i)
        Dump12(g.interp[i], D3D12_RESOURCE_STATE_UNORDERED_ACCESS, n + L"-fg" + std::to_wstring(i + 1) + L".raw");
    if (done < kDumpFrames) return;
    WriteDumps();
    Log("test: frames 0-%d written (%u in-between)", kDumpFrames - 1, generated);
}
#endif

static bool Frame()
{
    if (DeviceLost()) { g.quit = true; return false; }
    // Wait for the previous frame first, then take the newest capture: the picture is as fresh as the GPU allows.
    if (g.frame_done_value && g.fence->GetCompletedValue() < g.frame_done_value)
    {
        g.fence->SetEventOnCompletion(g.frame_done_value, g.fence_event);
        WaitForSingleObject(g.fence_event, 2000);
    }

    // Take the newest frame and drop older ones, so the picture is never behind. Every frame passed over
    // still counts towards the game's frame rate, which decides how many in-between frames fit.
    wgc::Direct3D11CaptureFrame frame{ nullptr };
    const INT64 last_time = g.last_frame_time;
    for (auto f = g.pool.TryGetNextFrame(); f; f = g.pool.TryGetNextFrame())
    {
        const INT64 t = f.SystemRelativeTime().count();
        if (g.last_frame_time != 0)
        {
            const double dt = (t - g.last_frame_time) / 1e7;
            if (dt > 0.0 && dt < 0.25) g.game_interval = g.game_interval > 0.0 ? g.game_interval * 0.9 + dt * 0.1 : dt;
        }
        g.last_frame_time = t;
        frame = f;
    }
    if (!frame) return false;
    // The first frame after a pause in the capture (an idle page sends none): the motion, a frame late, still describes the
    // pair before the pause, so nothing is kept still on it (Cover could not see what started to move).
    const bool resumed = last_time != 0 && (g.last_frame_time - last_time) / 1e7 > std::max(0.1, 3.0 * g.game_interval);
    ReadTimestamps();
    ChooseFrameGeneration();

    // The capture is the whole window, frame and title bar included; DLSS and the overlay use only
    // the client area, found from where it sits inside the window's visible bounds.
    const auto size = frame.ContentSize();
    RECT bounds = {}, client = {};
    DwmGetWindowAttribute(g.target, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(bounds));
    GetClientRect(g.target, &client);
    POINT origin = { 0, 0 };
    ClientToScreen(g.target, &origin);
    const UINT ox = static_cast<UINT>(std::max<LONG>(origin.x - bounds.left, 0));
    const UINT oy = static_cast<UINT>(std::max<LONG>(origin.y - bounds.top, 0));
    const UINT cw = std::min<UINT>(client.right, size.Width > static_cast<int>(ox) ? size.Width - ox : 0) & ~1u;
    const UINT ch = std::min<UINT>(client.bottom, size.Height > static_cast<int>(oy) ? size.Height - oy : 0) & ~1u;
    if (cw < 64 || ch < 64) return false;
    // The processing size (w x h): the window's client area times the Quality scale.
    const UINT w = std::max<UINT>(64, static_cast<UINT>(cw * g.scale + 0.5f) & ~1u);
    const UINT h = std::max<UINT>(64, static_cast<UINT>(ch * g.scale + 0.5f) & ~1u);
    if (cw != g.cw || ch != g.ch)
    {
        // Windows' capture leaves one picture of graphics memory behind every time its buffers are made again for another
        // size (measured: 7 MB per resize of a 1280x720 HDR window, also with a new pool and session). Larger buffers
        // hold a smaller window just as well, so they are made again only when the window outgrows them, and then at
        // once for the whole screen: shrinking and growing back cost nothing.
        if (static_cast<UINT>(size.Width) > g.pool_w || static_cast<UINT>(size.Height) > g.pool_h)
        {
            MONITORINFO mi = { sizeof(mi) };
            GetMonitorInfoW(MonitorFromWindow(g.target, MONITOR_DEFAULTTONEAREST), &mi);
            g.pool_w = std::max<UINT>(size.Width, mi.rcMonitor.right - mi.rcMonitor.left);
            g.pool_h = std::max<UINT>(size.Height, mi.rcMonitor.bottom - mi.rcMonitor.top);
            frame.Close();   // it belongs to the old buffers; the next frame comes from the new ones
            frame = nullptr;
            g.pool.Recreate(g.rt_device, CaptureFormat(), 3, { static_cast<int32_t>(g.pool_w), static_cast<int32_t>(g.pool_h) });
            Log("capture buffers made for %ux%u", g.pool_w, g.pool_h);
        }
        ResizeSwapchain(w, h);
        if (!BuildFrameResources(w, h)) { Log("could not build DLSS for %ux%u", w, h); return false; }
        g.cw = cw;
        g.ch = ch;
        if (w != cw && !BuildScaler(cw, ch)) return false;
        StretchToWindow();
        if (w != cw) Log("processing at %ux%u, %.0f %% of the %ux%u window", w, h, g.scale * 100.0f, cw, ch);
        g.refresh_hz = RefreshRate();
        Log("screen refresh: %.0f Hz", g.refresh_hz);
        if (!frame) return false;   // closed with the old buffers
    }
    if (!g.color11 || !g.copy_fence11) return false;   // this size failed to build; wait for another

    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    winrt::com_ptr<ID3D11Texture2D> src;
    access->GetInterface(IID_PPV_ARGS(src.put()));
    D3D11_BOX box = { ox, oy, 0, ox + cw, oy + ch, 1 };
    // Nothing waits on the CPU: D3D11 waits on the GPU until D3D12 has finished reading the previous picture and
    // motion, then writes the new ones and lets D3D12 go on.
    if (g.copy_value > 0) g.ctx11->Wait(g.copy_fence11, g.copy_value);
    if (w == cw) g.ctx11->CopySubresourceRegion(g.color11, 0, 0, 0, 0, src.get(), 0, &box);
    else
    {
        g.ctx11->CopySubresourceRegion(g.cap11, 0, 0, 0, 0, src.get(), 0, &box);
        ScaleDown();
    }
    const bool measured = RunMotion();
    RunStill(measured && !resumed);
    g.ctx11->Signal(g.copy_fence11, ++g.copy_value);
    g.ctx11->Flush();
    g.queue->Wait(g.copy_fence, g.copy_value);

    BeginList();
    if (g.timestamps) g.list->EndQuery(g.timestamps, D3D12_QUERY_TYPE_TIMESTAMP, 0);
    // A reset is for this frame only, DLSS's and frame generation's alike (each cause sets it again). Cleared only after
    // a DLSS evaluate, it stayed on while DLSS was off with F9, and frame generation lost its history on every frame.
    // Frame generation also starts afresh after a real frame it did not see (fg_fed).
    const bool reset = g.reset;
    g.reset = false;
    bool shown_dlss = false;
    if (g.dlss_on && g.feature)
    {
        NVSDK_NGX_D3D12_DLSS_Eval_Params ep = {};
        ep.Feature.pInColor = g.color;
        ep.Feature.pInOutput = g.output;
        ep.pInDepth = g.depth;
        ep.pInMotionVectors = g.mv;
        ep.InRenderSubrectDimensions.Width = w;
        ep.InRenderSubrectDimensions.Height = h;
        ep.InReset = reset ? 1 : 0;
        ep.InMVScaleX = 1.0f;
        ep.InMVScaleY = 1.0f;
        ep.InPreExposure = 1.0f;
        ep.InExposureScale = 1.0f;
        const NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSS_EXT(g.list, g.feature, g.params, &ep);
        static bool said = false;
        if (NVSDK_NGX_FAILED(r) || !said) { Log("DLSS evaluate: 0x%08X", r); said = true; }
        shown_dlss = NVSDK_NGX_SUCCEED(r);
    }
    if (g.timestamps) g.list->EndQuery(g.timestamps, D3D12_QUERY_TYPE_TIMESTAMP, 1);
    // The real frame: DLSS 5's answer, or the captured picture itself.
    ID3D12Resource *real = shown_dlss ? g.output : g.color;
    const D3D12_RESOURCE_STATES real_state = shown_dlss ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_COMMON;

    // Frame generation: the in-between frames from the previous real frame to this one, shown before it (none while it
    // rests at 1x).
    UINT generated = 0;
    if (g.fg_feature && g.fg_have_prev && g.fg_now > 0)
    {
        if (shown_dlss) Barrier(real, real_state, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        const bool finish = g.fg_raw != nullptr;
        const UINT real_slot = shown_dlss ? SLOT_OUTPUT : SLOT_COLOR;
        ID3D12Resource *fg_source = real;
        if (finish && g.fg_in)
        {
            RunFinish(g.pso_squeeze, real_slot, SLOT_IN);
            Barrier(g.fg_in, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            fg_source = g.fg_in;
        }
        for (UINT i = 1; i <= g.fg_now; ++i)
        {
            NVSDK_NGX_D3D12_DLSSG_Eval_Params ep = {};
            ep.pBackbuffer = fg_source;
            ep.pDepth = g.depth;
            ep.pMVecs = g.mv;
            ep.pOutputInterpFrame = finish ? g.fg_raw : g.interp[i - 1];
            NVSDK_NGX_DLSSG_Opt_Eval_Params op = {};
            op.multiFrameCount = g.fg_now;
            op.multiFrameIndex = i;
            op.colorBuffersHDR = g.hdr;
            // A captured picture has no camera: identity matrices, and the motion is all on screen.
            for (int k = 0; k < 4; ++k)
                op.cameraViewToClip[k][k] = op.clipToCameraView[k][k] = op.clipToLensClip[k][k] = op.clipToPrevClip[k][k] = op.prevClipToClip[k][k] = 1.0f;
            op.mvecScale[0] = 1.0f / w;   // our motion is in pixels; DLSS-G wants it as a fraction of the picture
            op.mvecScale[1] = 1.0f / h;
            op.cameraNear = 0.1f;
            op.cameraFar = 1000.0f;
            op.cameraFOV = 1.0472f;
            op.cameraAspectRatio = static_cast<float>(w) / h;
            op.depthInverted = true;
            op.cameraMotionIncluded = true;
            op.reset = reset || !g.fg_fed;   // after a rest at 1x its last frame may be minutes old
            op.minRelativeLinearDepthObjectSeparation = 40.0f;
            const NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSSG(g.list, g.fg_feature, g.params, &ep, &op);
            static bool said = false;
            if (NVSDK_NGX_FAILED(r) || !said) { Log("frame generation evaluate %u/%u: 0x%08X", i, g.fg_now, r); said = true; }
            if (NVSDK_NGX_FAILED(r)) break;
            if (finish)
            {
                Barrier(g.fg_raw, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                RunFinish(g.pso_finish, real_slot, SLOT_INTERP + i - 1);
                Barrier(g.fg_raw, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            }
            generated = i;
        }
        if (fg_source == g.fg_in) Barrier(g.fg_in, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (shown_dlss) Barrier(real, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, real_state);
    }
    if (g.timestamps && g.timestamp_readback)
    {
        g.list->EndQuery(g.timestamps, D3D12_QUERY_TYPE_TIMESTAMP, 2);
        g.list->ResolveQueryData(g.timestamps, D3D12_QUERY_TYPE_TIMESTAMP, 0, 3, g.timestamp_readback, 0);
        g.timed_pending = true;
        g.timed_generated = generated;
    }
    EndList();
    g.fg_have_prev = g.fg_feature != nullptr;
    g.fg_fed = generated > 0;

    for (UINT i = 0; i < generated; ++i) PresentTexture(g.interp[i], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    PresentTexture(real, real_state);
    g.frame_done_value = g.fence_value;   // PresentTexture's EndList signalled this after the frame's last work
#ifdef OVERLAY_TESTS
    TestDump(real, real_state, generated);   // its copies read the picture too: before D3D11 may write the next one
#endif
    // The last read of the picture and the motion is queued: tell D3D11 when the GPU is past it.
    g.queue->Signal(g.copy_fence, ++g.copy_value);
    g.presented += generated + 1;
    return true;
}

// ---------------------------------------------------------------------------------------------

struct FindArgs { const wchar_t *text; HWND found; };

static BOOL CALLBACK FindByTitle(HWND w, LPARAM lp)
{
    auto *a = reinterpret_cast<FindArgs *>(lp);
    wchar_t title[512] = {};
    GetWindowTextW(w, title, 512);
    if (IsWindowVisible(w) && title[0] && wcsstr(title, a->text) && !wcsstr(title, L"DLSS 5 overlay")) { a->found = w; return FALSE; }
    return TRUE;
}

int wmain(int argc, wchar_t **argv)
{
    // DLLs this process loads come from its own folder and System32 only, never the current folder or PATH: it can run
    // as administrator. (DLSS 5 Master removes any program file from that folder that is not the overlay's own.)
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    // The log is UTF-8: window titles and paths are written as they are ("Bloodborne™" or an accented user name made the
    // whole line vanish). Numbers keep the C locale's dot.
    setlocale(LC_CTYPE, ".UTF8");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    g_log = _wfsopen((ExeDir() + L"dlss5-overlay.log").c_str(), L"w", _SH_DENYNO);   // readable while running
    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    // dlss5-overlay.exe --hwnd <window> [--fg <1-3>] [--scale <0.4-1>] [--no-motion] [--stop-file <path>]   (DLSS 5 Master)
    // dlss5-overlay.exe "part of a window title"                              (by hand)
    std::wstring title_part;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring a = argv[i];
        if (a == L"--hwnd" && i + 1 < argc) g.target = reinterpret_cast<HWND>(static_cast<uintptr_t>(_wcstoui64(argv[++i], nullptr, 10)));
        else if (a == L"--no-motion") g.motion_on = false;
        else if (a == L"--fg" && i + 1 < argc) g.fg_frames = std::min(3u, static_cast<UINT>(_wtoi(argv[++i])));
        else if (a == L"--scale" && i + 1 < argc) g.scale = std::clamp(static_cast<float>(_wtof(argv[++i])), 0.4f, 1.0f);
        else if (a == L"--stop-file" && i + 1 < argc) g.stop_file = argv[++i];
#ifdef OVERLAY_TESTS
        else if (a == L"--test-any-focus") g.test_any_focus = true;
        else if (a == L"--test-remove-device" && i + 1 < argc) g.test_remove_device_s = _wtof(argv[++i]);
        else if (a == L"--test-dump" && i + 1 < argc) g.test_dump = argv[++i];
        else if (a == L"--test-sdr") g.test_sdr = true;
        else if (a == L"--test-no-finish") g.test_no_finish = true;
#endif
        else title_part = a;
    }
    if (!g.target && !title_part.empty())
    {
        FindArgs a = { title_part.c_str(), nullptr };
        EnumWindows(FindByTitle, reinterpret_cast<LPARAM>(&a));
        g.target = a.found;
    }
    if (!g.target || !IsWindow(g.target)) { Log("error: that window is not open any more"); return 2; }
    wchar_t title[512] = {};
    GetWindowTextW(g.target, title, 512);
    Log("target window: \"%ls\"", title);
    if (!g.stop_file.empty()) DeleteFileW(g.stop_file.c_str());

    // RTX 20 / 30: DLSS 5 Master puts dlssg_for_sm86 (sdli1995) beside this exe when frame generation is on. It
    // enables DLSS Frame Generation on those cards and must be in the process before D3D12 and NGX start.
    if (g.fg_frames > 0)
    {
        const std::wstring sm86 = ExeDir() + L"dlssg_sm86.dll";
        if (GetFileAttributesW(sm86.c_str()) != INVALID_FILE_ATTRIBUTES)
            Log(LoadLibraryW(sm86.c_str()) ? "dlssg_for_sm86 loaded: DLSS Frame Generation on RTX 20 / 30"
                                           : "dlssg_for_sm86 did not load (error %lu): no frame generation on this card", GetLastError());
    }

    if (!InitD3D12()) { Log("error: DirectX 12 could not start"); return 3; }
    if (!InitCapture()) { Log("error: Windows cannot capture this window (exclusive fullscreen? use borderless)"); return 4; }
    InitMotion();
    InitStill();
    bool finish = g.fg_frames > 0;
#ifdef OVERLAY_TESTS
    if (g.test_no_finish) finish = false;   // in-between frames as frame generation makes them, to compare
#endif
    if (finish) InitFinish();
    CloseHandle(CreateThread(nullptr, 0, KeyboardThread, nullptr, 0, nullptr));

    // The DLSS 5 add-on arms its NGX hooks shortly after NGX starts: keep presenting meanwhile,
    // so the first DLSS feature is created with the hooks in place.
    const bool ngx = InitNgx();
    for (int i = 0; i < 120; ++i)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        g.swap->Present(1, 0);
        Sleep(8);
    }
    if (!ngx) { Log("NGX is not available: showing the original picture only"); g.dlss_on = false; }

    Log("drawing over the game: F9 DLSS 5 on/off, Ctrl+F9 quit");
    UINT frames = 0;
    ULONGLONG t0 = GetTickCount64();
    bool window_paused = false;          // the game left the front (or the overlay stepped aside) during these 5 seconds
    while (!g.quit && IsWindow(g.target))
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        const HWND fg = GetForegroundWindow();
        PollKeys(fg == g.target || fg == g.window);
        static ULONGLONG next_stop_check = 0;
        if (!g.stop_file.empty() && GetTickCount64() >= next_stop_check)
        {
            next_stop_check = GetTickCount64() + 250;
            if (GetFileAttributesW(g.stop_file.c_str()) != INVALID_FILE_ATTRIBUTES) { DeleteFileW(g.stop_file.c_str()); Log("stopped by DLSS 5 Master"); break; }
        }
        static ULONGLONG next_fullscreen_check = 0;
        if (GetTickCount64() >= next_fullscreen_check) { next_fullscreen_check = GetTickCount64() + 1000; CheckExclusiveFullscreen(); }
        if (!TrackTarget()) { g.reset = true; window_paused = true; Sleep(16); continue; }   // the game is not in front: nothing to draw
#ifdef OVERLAY_TESTS
        static const ULONGLONG test_start = GetTickCount64();
        if (g.test_remove_device_s > 0.0 && static_cast<double>(GetTickCount64() - test_start) >= g.test_remove_device_s * 1000.0)
        {
            g.test_remove_device_s = 0.0;
            ID3D12Device5 *dev5 = nullptr;
            if (SUCCEEDED(g.dev->QueryInterface(IID_PPV_ARGS(&dev5)))) { Log("test: removing the D3D12 device"); dev5->RemoveDevice(); dev5->Release(); }
            else Log("test: this device cannot be removed on purpose");
        }
#endif
        static ULONGLONG next_memory_check = 0;
        if (GetTickCount64() >= next_memory_check) { next_memory_check = GetTickCount64() + 1000; CheckMemory(); }
        if (!Frame()) { Sleep(1); continue; }
        ++frames;
        const ULONGLONG t = GetTickCount64();
        if (t - t0 >= 5000)
        {
            static UINT64 shown0 = 0;
            Log("%s, %.0f captured fps, %.0f shown fps, %ux%u, GPU %.1f ms a frame + %.1f ms per in-between frame",
                g.dlss_on ? "DLSS 5 on" : "original picture", frames * 1000.0 / (t - t0), (g.presented - shown0) * 1000.0 / (t - t0),
                g.w, g.h, g.base_ms, g.fg_ms);
            shown0 = g.presented;
            if (!window_paused) CheckStarved(frames * 1000.0 / (t - t0));
            window_paused = false;
            frames = 0;
            t0 = t;
        }
    }

    Log(IsWindow(g.target) ? "closing" : "closing: the game's window closed");
    // Taking capture, NGX and the devices down can stall when the game has just gone (it once hung for minutes),
    // so past 3 seconds the helper simply ends.
    CloseHandle(CreateThread(nullptr, 0, [](void *) -> DWORD {
        Sleep(3000);
        Log("closing took too long (stuck at: %s), ending", g.closing_step);
        TerminateProcess(GetCurrentProcess(), 0);
        return 0;
    }, nullptr, 0, nullptr));
    g.closing_step = "capture";
    if (g.session) g.session.Close();
    if (g.pool) g.pool.Close();
    g.closing_step = "frame resources";
    ReleaseFrameResources();
    ReleaseFinish();
    Release(g.cs_still);
    Release(g.cs_cover);
    Release(g.cs_calm);
    g.closing_step = "NGX";
    if (g.params) NVSDK_NGX_D3D12_DestroyParameters(g.params);
    NVSDK_NGX_D3D12_Shutdown1(g.dev);
    g.closing_step = "devices";
    // Take the swapchain and devices down in order, so ReShade and the DLSS 5 add-on see a normal
    // shutdown (the add-on records a process that just ends as an "unclean" session).
    for (auto &b : g.back) Release(b);
    for (auto &a : g.ring) Release(a);
    Release(g.adapter);
    Release(g.timestamps);
    Release(g.timestamp_readback);
    Release(g.vs_full);
    Release(g.ps_down);
    Release(g.linear_clamp);
    Release(g.scale_cb);
    Release(g.dcomp_visual);
    Release(g.dcomp_target);
    Release(g.dcomp);
    Release(g.swap);
    Release(g.list);
    Release(g.alloc);
    Release(g.fence);
    Release(g.queue);
    Release(g.ctx11);
    Release(g.dev11);
    Release(g.dev);
    DestroyWindow(g.window);
    Log("closed");
    return 0;
}
