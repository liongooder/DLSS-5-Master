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

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <dcomp.h>
#include <dwmapi.h>
#include <share.h>
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

    // NGX
    NVSDK_NGX_Parameter *params = nullptr;
    NVSDK_NGX_Handle *feature = nullptr;

    // Frame generation (DLSS-G): fg_frames in-between frames per captured frame (0 = off, 1 = 2x, 3 = 4x)
    UINT fg_frames = 0;
    NVSDK_NGX_Handle *fg_feature = nullptr;
    ID3D12Resource *interp[3] = {};
    bool fg_have_prev = false;            // DLSS-G needs a previous real frame to interpolate from
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
    INT64 last_frame_time = 0;            // the capture's timestamp of the previous frame (100 ns units)
    UINT fg_now = 0;                      // in-between frames used for this frame

    UINT w = 0, h = 0;                   // current picture size
    // DirectComposition: lets the swapchain sit in a click-through overlay window
    IDCompositionDevice *dcomp = nullptr;
    IDCompositionTarget *dcomp_target = nullptr;
    IDCompositionVisual *dcomp_visual = nullptr;

    std::wstring stop_file;              // DLSS 5 Master creates this file to stop the overlay (works across elevation)
    bool dlss_on = true;
    bool motion_on = true;
    bool reset = true;
    bool quit = false;
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
    if (keys & KEY_DLSS) { g.dlss_on = !g.dlss_on; g.reset = true; Log("DLSS 5 %s", g.dlss_on ? "on" : "off (original picture)"); }
}

// Keep the overlay exactly over the game's client area, and show it only while the game
// (or the overlay itself, in settings mode) is the window in front.
static bool TrackTarget()
{
    const HWND fg = GetForegroundWindow();
    const bool show = !IsIconic(g.target) && IsWindowVisible(g.target) && (fg == g.target || fg == g.window);
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

    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    g.dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&g.queue));
    g.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g.alloc));
    g.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g.alloc, nullptr, IID_PPV_ARGS(&g.list));
    g.list->Close();
    for (auto &a : g.ring) g.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a));
    g.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g.fence));
    g.fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

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
    sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
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

    D3D12_FEATURE_DATA_FORMAT_SUPPORT fs = { DXGI_FORMAT_B8G8R8A8_UNORM };
    g.dev->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &fs, sizeof(fs));
    if (!(fs.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))
        Log("WARNING: this GPU cannot write B8G8R8A8 from a compute shader; DLSS may refuse the output");
    return true;
}

// ---------------------------------------------------------------------------------------------
// Windows Graphics Capture on a D3D11 device of the same GPU

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
    g.pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(g.rt_device, wdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
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

// Frame generation for a picture of w x h: the DLSS-G feature and the textures it writes the in-between
// frames into. Failing leaves the overlay running without it.
static void BuildFrameGeneration(UINT w, UINT h)
{
    if (g.fg_frames == 0 || !g.params) return;
    for (UINT i = 0; i < g.fg_frames; ++i)
        g.interp[i] = MakeTex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE,
                              D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    NVSDK_NGX_DLSSG_Create_Params cp = {};
    cp.Width = w;
    cp.Height = h;
    cp.NativeBackbufferFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
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
    if (NVSDK_NGX_FAILED(r)) g.fg_feature = nullptr;
    g.fg_have_prev = false;
    g.fg_now = g.fg_frames;
}

static void ReleaseFrameGeneration()
{
    if (g.fg_feature) { NVSDK_NGX_D3D12_ReleaseFeature(g.fg_feature); g.fg_feature = nullptr; }
    for (auto &t : g.interp) Release(t);
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
    luma[id.xy] = dot(c, float3(0.299, 0.587, 0.114));
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
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
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

// After the new picture is in color11: measure its motion into mv11.
static void RunMotion()
{
    if (!g.motion_ready) return;
    Dispatch(g.cs_luma, g.color_srv, g.luma_uav[g.cur], g.w / 2, g.h / 2, 0.0f, 0);
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
            // half-size flow in 1/32 pixel -> full-size pixels: x 2 / 32
            Dispatch(g.cs_mv, g.flow_srv, g.mv_uav, g.w, g.h, 2.0f / 32.0f, g.grid * 2);
            measured = true;
        }
    }
    if (!measured) { const float zero[4] = {}; g.ctx11->ClearUnorderedAccessViewFloat(g.mv_uav, zero); }
    g.have_prev = true;
    g.cur = 1 - g.cur;
}

static void ReleaseFrameResources()
{
    WaitGpu();
    if (g.feature) { NVSDK_NGX_D3D12_ReleaseFeature(g.feature); g.feature = nullptr; }
    ReleaseFrameGeneration();
    ReleaseMotion();
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
    g.color = MakeTex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS, D3D12_HEAP_FLAG_SHARED,
                      D3D12_RESOURCE_STATE_COMMON);
    g.output = MakeTex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_HEAP_FLAG_NONE,
                       D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    g.depth = MakeTex(w, h, DXGI_FORMAT_R32_FLOAT, D3D12_RESOURCE_FLAG_NONE, D3D12_HEAP_FLAG_NONE,
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    if (!g.output || !g.depth) return false;

    // The captured picture and the motion cross from D3D11 to D3D12 through shared textures and a shared
    // fence. D3D11 creates them: this driver refuses to open D3D12-created textures on D3D11 (E_INVALIDARG).
    const char *step = "share the picture";
    HRESULT hr = Share11(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, &g.color11, &g.color);
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

    NVSDK_NGX_DLSS_Create_Params cp = {};
    cp.Feature.InWidth = w;
    cp.Feature.InHeight = h;
    cp.Feature.InTargetWidth = w;
    cp.Feature.InTargetHeight = h;
    cp.Feature.InPerfQualityValue = NVSDK_NGX_PerfQuality_Value_DLAA;
    cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure |
                              NVSDK_NGX_DLSS_Feature_Flags_DepthInverted;
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
    g.swap->ResizeBuffers(6, w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
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

// How many in-between frames fit: real frames times (1 + generated) may not exceed the screen's refresh rate,
// or the queue fills and holds the real frames back. Changes only after holding for about a second.
static void ChooseFrameGeneration()
{
    if (!g.fg_feature || g.game_interval <= 0.0) { g.fg_now = g.fg_frames; return; }
    const int fit = static_cast<int>(std::floor(g.refresh_hz * g.game_interval + 0.25)) - 1;
    const UINT want = static_cast<UINT>(std::clamp(fit, 0, static_cast<int>(g.fg_frames)));
    static UINT pending = UINT_MAX;
    static int held = 0;
    if (want == g.fg_now) { held = 0; return; }
    if (want != pending) { pending = want; held = 0; }
    if (++held < 30) return;
    Log("frame generation now %ux: the game runs at %.0f fps on a %.0f Hz screen%s", want + 1, 1.0 / g.game_interval, g.refresh_hz,
        want < g.fg_frames ? " (more would not fit on the screen)" : "");
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

static bool Frame()
{
    // Take the newest frame and drop older ones, so the picture is never behind. Every frame passed over
    // still counts towards the game's frame rate, which decides how many in-between frames fit.
    wgc::Direct3D11CaptureFrame frame{ nullptr };
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
        g.pool.Recreate(g.rt_device, wdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
        ResizeSwapchain(w, h);
        if (!BuildFrameResources(w, h)) { Log("could not build DLSS for %ux%u", w, h); return false; }
        g.cw = cw;
        g.ch = ch;
        if (w != cw && !BuildScaler(cw, ch)) return false;
        StretchToWindow();
        if (w != cw) Log("processing at %ux%u, %.0f %% of the %ux%u window", w, h, g.scale * 100.0f, cw, ch);
        g.refresh_hz = RefreshRate();
        Log("screen refresh: %.0f Hz", g.refresh_hz);
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
    RunMotion();
    g.ctx11->Signal(g.copy_fence11, ++g.copy_value);
    g.ctx11->Flush();
    g.queue->Wait(g.copy_fence, g.copy_value);

    BeginList();
    const bool reset = g.reset;
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
        ep.InReset = g.reset ? 1 : 0;
        ep.InMVScaleX = 1.0f;
        ep.InMVScaleY = 1.0f;
        ep.InPreExposure = 1.0f;
        ep.InExposureScale = 1.0f;
        const NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSS_EXT(g.list, g.feature, g.params, &ep);
        static bool said = false;
        if (NVSDK_NGX_FAILED(r) || !said) { Log("DLSS evaluate: 0x%08X", r); said = true; }
        g.reset = false;
        shown_dlss = NVSDK_NGX_SUCCEED(r);
    }
    // The real frame: DLSS 5's answer, or the captured picture itself.
    ID3D12Resource *real = shown_dlss ? g.output : g.color;
    const D3D12_RESOURCE_STATES real_state = shown_dlss ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_COMMON;

    // Frame generation: the in-between frames from the previous real frame to this one, shown before it.
    UINT generated = 0;
    if (g.fg_feature && g.fg_have_prev)
    {
        if (shown_dlss) Barrier(real, real_state, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        for (UINT i = 1; i <= g.fg_now; ++i)
        {
            NVSDK_NGX_D3D12_DLSSG_Eval_Params ep = {};
            ep.pBackbuffer = real;
            ep.pDepth = g.depth;
            ep.pMVecs = g.mv;
            ep.pOutputInterpFrame = g.interp[i - 1];
            NVSDK_NGX_DLSSG_Opt_Eval_Params op = {};
            op.multiFrameCount = g.fg_now;
            op.multiFrameIndex = i;
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
            op.reset = reset;
            op.minRelativeLinearDepthObjectSeparation = 40.0f;
            const NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSSG(g.list, g.fg_feature, g.params, &ep, &op);
            static bool said = false;
            if (NVSDK_NGX_FAILED(r) || !said) { Log("frame generation evaluate %u/%u: 0x%08X", i, g.fg_now, r); said = true; }
            if (NVSDK_NGX_FAILED(r)) break;
            generated = i;
        }
        if (shown_dlss) Barrier(real, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, real_state);
    }
    EndList();
    g.fg_have_prev = g.fg_feature != nullptr;

    for (UINT i = 0; i < generated; ++i) PresentTexture(g.interp[i], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    PresentTexture(real, real_state);
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
        if (!TrackTarget()) { g.reset = true; Sleep(16); continue; }   // the game is not in front: nothing to draw
        static ULONGLONG next_memory_check = 0;
        if (GetTickCount64() >= next_memory_check) { next_memory_check = GetTickCount64() + 1000; CheckMemory(); }
        if (!Frame()) { Sleep(1); continue; }
        ++frames;
        const ULONGLONG t = GetTickCount64();
        if (t - t0 >= 5000)
        {
            static UINT64 shown0 = 0;
            Log("%s, %.0f captured fps, %.0f shown fps, %ux%u", g.dlss_on ? "DLSS 5 on" : "original picture",
                frames * 1000.0 / (t - t0), (g.presented - shown0) * 1000.0 / (t - t0), g.w, g.h);
            shown0 = g.presented;
            frames = 0;
            t0 = t;
        }
    }

    Log("closing");
    if (g.session) g.session.Close();
    if (g.pool) g.pool.Close();
    ReleaseFrameResources();
    if (g.params) NVSDK_NGX_D3D12_DestroyParameters(g.params);
    NVSDK_NGX_D3D12_Shutdown1(g.dev);
    // Take the swapchain and devices down in order, so ReShade and the DLSS 5 add-on see a normal
    // shutdown (the add-on records a process that just ends as an "unclean" session).
    for (auto &b : g.back) Release(b);
    for (auto &a : g.ring) Release(a);
    Release(g.adapter);
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
