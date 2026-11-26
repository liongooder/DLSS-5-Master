// Companion panel for the original Feeder. This add-on registers UI
// callbacks only. The two original add-ons own the game's rendering.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <cstddef>
#include <string>
#include <algorithm>
#include <atomic>
#include <optional>

#define ImTextureID ImU64
#include <imgui.h>
#include <reshade.hpp>

#include "feed_renodx_api.h"
#include "feed_panel.h"
#include "feed_renodx_108_live.h"

extern "C" __declspec(dllexport) const char *NAME = "DLSS 5 Feeder Controls";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Controls RenoDX alongside the original Feeder.";

static reshade::api::effect_runtime *g_game_runtime;
static HMODULE g_panel_module;
static bool g_enabled = true;
static std::optional<bool> g_neural_enabled;
static ULONGLONG g_next_neural_check;
static bool g_paused_feeder_effects;

static void PanelLog(const char *format, ...)
{
    char line[512];
    va_list args;
    va_start(args, format);
    vsnprintf_s(line, sizeof(line), _TRUNCATE, format, args);
    va_end(args);
    reshade::log::message(reshade::log::level::info, line);
}

static std::optional<bool> HasOnlyFeederEffects(reshade::api::effect_runtime *rt)
{
    const auto feed = rt->find_technique("DLSS5_Feed.fx", "DLSS5_Feed");
    if (feed.handle == 0) return std::nullopt;
    if (!rt->get_technique_state(feed)) return false;
    const auto motion = rt->find_technique("vort_Motion.fx", "vort_MotionEffects");
    bool unrelated_effect_enabled = false;
    rt->enumerate_techniques(nullptr, [&](auto *runtime, auto technique) {
        if (technique.handle != feed.handle && technique.handle != motion.handle &&
            runtime->get_technique_state(technique))
            unrelated_effect_enabled = true;
    });
    return !unrelated_effect_enabled;
}

static void ResumeFeederEffects(reshade::api::effect_runtime *rt)
{
    if (!g_paused_feeder_effects) return;
    g_paused_feeder_effects = false;
    rt->set_effects_state(true);
    PanelLog("[panel] Feeder effects resumed");
}

static void SyncNeuralRendering(reshade::api::effect_runtime *rt, bool enabled)
{
    if (g_neural_enabled == enabled) return;
    if (enabled)
    {
        g_neural_enabled = true;
        ResumeFeederEffects(rt);
        return;
    }
    const auto feeder_only = HasOnlyFeederEffects(rt);
    if (!feeder_only.has_value()) return; // Effects are reloading. Retry when the handles are available.
    g_neural_enabled = false;
    // The stock Feeder still runs DLAA and its frame transfers with RenoDX off.
    // Pause the default Feeder-only preset without changing its saved techniques.
    if (!rt->get_effects_state() || !*feeder_only) return;
    rt->set_effects_state(false);
    g_paused_feeder_effects = !rt->get_effects_state();
    if (g_paused_feeder_effects) PanelLog("[panel] Feeder effects paused with neural rendering off");
}

static void CheckNeuralRendering(reshade::api::effect_runtime *rt)
{
    const auto now = GetTickCount64();
    if (now < g_next_neural_check) return;
    g_next_neural_check = now + 250;

    if (g_paused_feeder_effects)
    {
        // A user can turn effects back on or enable additional ReShade effects
        // while NR is off. Release our pause so those effects can run.
        if (rt->get_effects_state()) g_paused_feeder_effects = false;
        else if (HasOnlyFeederEffects(rt) == false) ResumeFeederEffects(rt);
    }
    float enabled = 0.0f;
    if (reno108::Get("NeuralUplift", &enabled)) SyncNeuralRendering(rt, enabled != 0.0f);
}

static void SetupPanel()
{
    if (g_panel_env.log != nullptr) return;
    const DWORD len = GetModuleFileNameA(g_panel_module, g_panel_env.tool_dir, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return;
    char *leaf = strrchr(g_panel_env.tool_dir, '\\');
    if (leaf == nullptr) return;
    leaf[1] = '\0';
    if (sprintf_s(g_panel_env.cfg_path, "%sdlss5-feed.cfg", g_panel_env.tool_dir) <= 0) return;

    FILE *cfg = nullptr;
    if (fopen_s(&cfg, g_panel_env.cfg_path, "r") == 0 && cfg != nullptr)
    {
        char line[512], key[80];
        int value = 0;
        std::optional<int> live_panel_key;
        while (fgets(line, sizeof(line), cfg) != nullptr)
        {
            if (sscanf_s(line, " %79[^=]=%d", key, static_cast<unsigned>(sizeof(key)), &value) != 2) continue;
            if (_stricmp(key, "enabled") == 0) g_enabled = value != 0;
            else if (_stricmp(key, "live_panel_key") == 0) live_panel_key = value;
            else FeedPanelCfg(key, value);
        }
        // The companion owns this shortcut when the original panel is disabled.
        if (live_panel_key) FeedPanelCfg("panel_key", *live_panel_key);
        fclose(cfg);
    }

    g_panel_env.log = PanelLog;
    g_panel_env.running = []() { return GetModuleHandleW(L"renodx-dlss5.addon64") != nullptr; };
    g_panel_env.live_keys = [](char *out, size_t capacity)
    {
        int count = FeedRenoApiKeys(out, capacity);
        if (capacity == 0) return count;
        const reno108::VerifiedModule module;
        if (module.base() == 0) return count;

        size_t offset = strlen(out);
        for (std::string_view key : reno108::kLiveKeys)
        {
            if (capacity - offset <= key.size() + 1) break;
            memcpy(out + offset, key.data(), key.size());
            offset += key.size();
            out[offset++] = '\n';
            out[offset] = '\0';
            ++count;
        }
        return count;
    };
    g_panel_env.get_setting = [](const char *key, float *value) {
        return reno108::IsStructuralKey(key) ? reno108::Get(key, value) : FeedRenoApiGet(key, value);
    };
    g_panel_env.submit_setting = [](const char *key, float value) {
        if (reno108::IsStructuralKey(key))
        {
            const auto result = reno108::Set(key, value);
            if (strcmp(key, "NeuralUplift") == 0 && result == FeedPanelSettingResult::applied)
                SyncNeuralRendering(g_game_runtime, value != 0.0f);
            return result;
        }
        const int result = FeedRenoApiSet(key, value);
        return result == 1 ? FeedPanelSettingResult::pending :
            (result == 0 ? FeedPanelSettingResult::rejected : FeedPanelSettingResult::unverified);
    };
    g_panel_env.poll_setting = [](const char *key, float value) {
        float active = 0.0f;
        return g_panel_env.get_setting(key, &active) && FeedPanelMatches(active, value)
            ? FeedPanelSettingResult::applied : FeedPanelSettingResult::pending;
    };
}

static bool IsFeederRuntime(reshade::api::effect_runtime *rt)
{
    return rt != nullptr &&
        rt->find_technique("DLSS5_Feed.fx", "DLSS5_Feed").handle != 0;
}

static void OnPresent(reshade::api::effect_runtime *rt)
{
    if (g_game_runtime == nullptr && IsFeederRuntime(rt))
    {
        g_game_runtime = rt;
        SetupPanel();
    }
    if (g_enabled && g_game_runtime == rt)
    {
        CheckNeuralRendering(rt);
        FeedPanelTick(rt);
    }
}

static void OnOverlay(reshade::api::effect_runtime *rt)
{
    if (g_enabled && rt != nullptr && rt == g_game_runtime) FeedPanelDraw(rt);
}

static void OnDestroyRuntime(reshade::api::effect_runtime *rt)
{
    if (rt != g_game_runtime) return;
    ResumeFeederEffects(rt);
    FeedPanelHideNow();
    g_br_loaded = false;
    g_neural_enabled.reset();
    g_next_neural_check = 0;
    g_game_runtime = nullptr;
}

#ifdef FEED_VERIFY_DIRECT
extern "C" __declspec(dllexport) bool FeederVerifyDescribeControl(
    uint32_t row, const char **key, float *lo, float *hi, bool *live)
{
    if (g_game_runtime == nullptr || row >= static_cast<uint32_t>(kBridgeCount)) return false;
    if (!g_br_loaded) BridgeLoad();
    *key = BridgeKey(row);
    *lo = BridgeOut(row, BridgeSideOf(row).lo);
    *hi = BridgeOut(row, BridgeSideOf(row).hi);
    *live = g_br_rowlive[row];
    return true;
}

extern "C" __declspec(dllexport) bool FeederVerifyQueueEdit(const char *key, float value)
{
    if (g_game_runtime == nullptr) return false;
    if (!g_br_loaded) BridgeLoad();
    for (int row = 0; row < kBridgeCount; ++row)
    {
        if (BridgeKey(row) == nullptr || strcmp(BridgeKey(row), key) != 0 || !g_br_rowlive[row]) continue;
        g_br[row] = value;
        FeedPanelQueue(row);
        return true;
    }
    return false;
}

extern "C" __declspec(dllexport) bool FeederVerifyGet(const char *key, float *value)
{
    return g_panel_env.get_setting != nullptr && g_panel_env.get_setting(key, value);
}

extern "C" __declspec(dllexport) bool FeederVerifyEffectsEnabled()
{
    return g_game_runtime != nullptr && g_game_runtime->get_effects_state();
}
#endif

extern "C" __declspec(dllexport) bool AddonInit(HMODULE module, HMODULE reshade_module)
{
    if (!reshade::register_addon(module, reshade_module)) return false;
    g_panel_module = module;
    reshade::register_event<reshade::addon_event::reshade_present>(OnPresent);
    reshade::register_event<reshade::addon_event::reshade_overlay>(OnOverlay);
    reshade::register_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    return true;
}

extern "C" __declspec(dllexport) void AddonUninit(HMODULE module, HMODULE reshade_module)
{
    reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    reshade::unregister_event<reshade::addon_event::reshade_overlay>(OnOverlay);
    reshade::unregister_event<reshade::addon_event::reshade_present>(OnPresent);
    reshade::unregister_addon(module, reshade_module);
}
