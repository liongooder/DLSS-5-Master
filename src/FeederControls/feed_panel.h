// feed_panel.h -- the feeder's own in-game panel, the same in every build of the add-on.
//
// One panel, drawn in the game by ReShade with Dear ImGui, in front of
// whichever neural tool does the work: the "RenoDX DLSS5 Generic" add-on or the OptiScaler
// DLSS-NR fork. It opens over the game on a
// shortcut (bare End unless dlss5-feed.cfg says otherwise), leaves the rest of the picture as it is, and takes
// the mouse and keyboard while it is up.
//
// It is included by three builds and knows nothing about how any of them works:
//
//   dlss5-feed.addon32          a 32-bit game; the tool runs in host64's helper process
//   dlss5-feed-helper.addon64   a 64-bit game in helper mode; the same
//   dlss5-feed.addon64          a 64-bit game; the tool runs in the game's own process
//
// Each fills in a FeedPanelEnv: where the tool's files are, whether it is running, and how to
// reach it. Everything else -- the vocabulary of settings, where each one lives in each tool's
// file and how it is spelled there, the drawing -- is here, once.
//
// How a change gets to the tool, by tool:
//   OptiScaler reads OptiScaler.ini. A build of the fork that re-reads [DlssNr] while it runs
//     (it carries OPTI_LIVE_LITERAL) takes the change live; an older build at its next start.
//   The RenoDX add-on reads its ini at start only: the helper is restarted when there is one,
//     and otherwise the change waits for the next start of the game. From v10 it also has a
//     control interface (feed_renodx_api.h) that takes some of its settings while it runs:
//     those rows are submitted live. The in-game panel automatically starts helper rebuilds.
//   ShortFuse's renodx-dlss.addon64 is NOT one of the tools (user's decision, 2026-11-12): it is
//     built to run without the feeder, and in the feeder's own process its neural pass either
//     faults or does not start. The panel takes no notice of it.
//
// Requires <windows.h>, <imgui.h> + <reshade.hpp> (the add-on's own includes) before it.
// Header-only and static, like its siblings: every add-on is a single-file build.

#pragma once

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <string>

enum class FeedPanelSettingResult { applied, pending, rejected, unverified };
#include "feed_opti_panel_ack.h"

struct FeedPanelEnv
{
    bool helper;                 // the tool runs in host64's helper process, not in this one
    bool output_preview;         // let the game show its own image while keeping the helper warm
    unsigned rebuild_debounce_ms;
    char tool_dir[MAX_PATH];     // the folder the tool's files are in, with its trailing backslash
    char reshade_ini[MAX_PATH];  // helper only: the ReShade.ini of the helper's ReShade (the RenoDX add-ons save there).
                                 // In-process the tool shares this ReShade, and its config is reached through the API.
    bool opti_nr;                // an OptiScaler DLSS-NR build is the tool
    bool opti_live;              // ... one that re-reads its ini while it runs
    bool lecram;                 // the native Lecram add-on is loaded in this process
    char opti_ini[MAX_PATH];
    char cfg_path[MAX_PATH];     // dlss5-feed.cfg, where the panel keeps its own few keys (its size)

    void (*refresh)();                                   // fill in the fields above (called when the panel first opens)
    bool (*running)();                                   // the tool can be handed a setting right now
    bool (*opti_frame_size)(unsigned int &width, unsigned int &height); // known DLAA dimensions for OptiScaler NR
    bool (*live_controls_blocked)();                     // consumer fault requires a game restart
    bool (*starting)();                                  // a helper is on its way up
    void (*schedule_apply)();                            // have FeedPanelApply() called where the helper may be stopped
                                                         // (nullptr: it is called on the spot)
    void (*stop_tool)();                                 // helper only: stop it, so its files can be written under it
    void (*begin_stop_tool)();                           // nonblocking stop for the production helper
    bool (*stop_tool_done)();                            // safe to write after the helper has exited
    void (*restart_tool)();                              // helper only: let it start again
    bool  (*send_setting)(const char *key, float value);  // hand the tool one setting while it runs (RenoDX's control interface)
    FeedPanelSettingResult (*submit_setting)(const char *key, float value); // send from the backend's safe callback
    FeedPanelSettingResult (*poll_setting)(const char *key, float value); // observe acceptance by the backend
    FeedPanelSettingResult (*verify_rebuild)(const char *key, float value); // read the recreated helper's active setting
    int   (*live_keys)(char *out, size_t cb);             // tool in THIS process: the names it takes that way, one per line
    bool  (*get_setting)(const char *key, float *value); // read a native control that the backend advertises
    float (*gpu_ms)();                                    // GPU time of the feed's work on one frame (DLSS + neural pass); <= 0 unknown
    // Motion vectors (feed_panel_mv.h fills these in). The panel warns at its top when the
    // built-in estimator is not the one in use.
    int         (*mv_mode)();                       // DLSS5_MV_PROVIDER DLSS5_Feed.fx is compiled for (5 = built in); < 0 not known
    const char *(*mv_name)();                       // that provider's name
    int         (*mv_enabled)(char *out, size_t cb);// the motion-vector shaders switched on in ReShade, as the user sees them; returns how many
    void        (*mv_use_builtin)();                // set DLSS5_MV_PROVIDER = 5 on DLSS5_Feed.fx
    void        (*mv_disable_others)();             // switch those shaders off
    void (*log)(const char *fmt, ...);
};
static FeedPanelEnv g_panel_env;

// The shortcut and the edge: parse-only keys of dlss5-feed.cfg (CfgSave carries unknown keys
// over, so a value set by hand survives). panel_mods: 1 Alt, 2 Ctrl, 4 Shift.
static constexpr int kFeedPanelDefaultKey = VK_END;
static constexpr int kFeedPanelDefaultMods = 0;
static int g_panel_key = kFeedPanelDefaultKey;
static int g_panel_mods = kFeedPanelDefaultMods;
static std::atomic_bool g_panel_feed_output { true };
static int g_panel_side = 1;      // 0 left, 1 right
// Relative text size in percent. 0 selects the default 100% of viewport height / 72.
// ReShade's global font scale is factored out when the panel pushes its own font size.
static int g_panel_scale = 0;
// The motion-vector notice at the top of the panel: 0 = never shown ("Never show again").
static int g_panel_mv_notice = 1;

static bool FeedPanelCfg(const char *key, int value)
{
    if      (_stricmp(key, "panel_key") == 0)  g_panel_key  = (value >= 0 && value < 256) ? value : kFeedPanelDefaultKey;
    else if (_stricmp(key, "panel_mods") == 0) g_panel_mods = value & 7;
    else if (_stricmp(key, "panel_side") == 0) g_panel_side = value != 0 ? 1 : 0;
    else if (_stricmp(key, "panel_scale") == 0) g_panel_scale = value <= 0 ? 0 : value < 50 ? 50 : value > 400 ? 400 : value;
    else if (_stricmp(key, "panel_mv_notice") == 0) g_panel_mv_notice = value != 0 ? 1 : 0;
    else return false;
    return true;
}

// One of the panel's own keys, written into dlss5-feed.cfg in place: every other line of the
// file is kept as it is. The add-on reads the file again by itself and hands the key back to
// FeedPanelCfg.
static void FeedPanelSaveCfg(const char *key, int value)
{
    if (g_panel_env.cfg_path[0] == '\0') return;
    std::string kept;
    const size_t n = strlen(key);
    FILE *r = nullptr;
    if (fopen_s(&r, g_panel_env.cfg_path, "r") == 0 && r != nullptr)
    {
        char line[512];
        while (fgets(line, sizeof(line), r) != nullptr)
        {
            const char *k = line;
            while (*k == ' ' || *k == '\t') ++k;
            if (_strnicmp(k, key, n) == 0)
            {
                const char *e = k + n;
                while (*e == ' ' || *e == '\t') ++e;
                if (*e == '=') continue;   // the old value
            }
            kept += line;
            if (kept.back() != '\n') kept += '\n';
        }
        fclose(r);
    }
    FILE *f = nullptr;
    if (fopen_s(&f, g_panel_env.cfg_path, "w") != 0 || f == nullptr) return;
    fputs(kept.c_str(), f);
    fprintf(f, "%s=%d\n", key, value);
    fclose(f);
    if (g_panel_env.log != nullptr) g_panel_env.log("[panel] %s=%d saved to dlss5-feed.cfg", key, value);
}

static void FeedPanelKeyName(char *out, size_t n)
{
    char key[64] = "";
    const UINT scan = MapVirtualKeyA(static_cast<UINT>(g_panel_key), MAPVK_VK_TO_VSC_EX);
    const LONG key_data = static_cast<LONG>(((scan & 0xffu) << 16) | ((scan & 0xff00u) != 0u ? 1u << 24 : 0u));
    if (g_panel_key == VK_END) strcpy_s(key, "End");
    else if (g_panel_key <= 0 || GetKeyNameTextA(key_data, key, sizeof(key)) == 0) sprintf_s(key, "key %d", g_panel_key);
    sprintf_s(out, n, "%s%s%s%s", (g_panel_mods & 1) != 0 ? "Alt+" : "", (g_panel_mods & 2) != 0 ? "Ctrl+" : "",
              (g_panel_mods & 4) != 0 ? "Shift+" : "", key);
}

// ---------------------------------------------------------------------------
// The settings. One row is one idea; each tool's side of it says where it lives.
// ---------------------------------------------------------------------------
enum FeedPanelKind { BR_BOOL, BR_COMBO, BR_FLOAT };
enum { BR_BACKEND_NONE = 0, BR_BACKEND_RENODX, BR_BACKEND_OPTI };

// key == nullptr: this tool has no such setting, and the row is not shown.
struct BridgeSide
{
    const char        *key;
    float              def, lo, hi;   // in the panel's units
    float              scale;         // the tool's number = the panel's times this (0 = the same number)
    float              on;            // a tick box: the tool's number for "ticked" (0 = 1)
    const char *const *items;         // a combo whose choices this tool names differently (nullptr = the row's)
    int                item_count;
};

struct BridgeItem
{
    const char        *group;
    const char        *label;
    FeedPanelKind      kind;
    const char        *format;     // sliders
    bool               whole;      // a count: whole steps
    bool               log;        // a range of several decades: logarithmic slider
    const char *const *items;      // combos
    int                item_count;
    const int         *values;     // combos: the number written for each item; nullptr = the item's index
    int                need_row;   // shown only while combo row `need_row` has item `need_item` selected; -1 = always
    int                need_item;
    BridgeSide         reno;       // "RenoDX DLSS5 Generic": [RenoDX.DLSS5] in its ReShade.ini
    BridgeSide         opti;       // OptiScaler DLSS-NR: [DlssNr] in OptiScaler.ini
    BridgeSide         reno8;      // RenoDX from v8 on (it writes ConfigVersion), where it differs from `reno`:
                                   // another range, or a setting older builds do not have (then reno is BR_NONE)
};

static const char *const kBrPresets[]   = { "Default", "Preset #1", "Preset #2", "Preset #3" };
static const char *const kBrStyles[]    = { "Default", "Natural", "Cinematic" };
static const char *const kBrWhiteSource[]  = { "Manual", "Game exposure", "Automatic HDR exposure" };
static const int         kBrWhiteSourceV[] = { 0, 1, 3 };   // OptiScaler's WhitePointSource values for those three
enum { BR_ROW_WHITE_SOURCE = 16 };   // index of "White point source" below
enum { BR_ROW_RENO_LOOK = 29 };     // index of "Adjust the model's result" below
// RenoDX 10.8.0's choices, in the order of the numbers it takes. Detail stability really is
// Auto / Off / Always: its log says "(Auto)" for 0, nothing for 1 and "(Always)" for 2.
static const char *const kBrRenoStabilize[] = { "Off", "Static", "Motion" };
static const char *const kBrRenoDetail[]    = { "Auto", "Off", "Always" };
#define BR_NONE { nullptr, 0.0f, 0.0f, 0.0f }

// Ranges. OptiScaler's are its own: Config.h defaults and the slider limits in
// DlssNr_MenuControls.cpp (fork bb484163). The Generic add-on publishes none: its keys are the
// ones v6.1 writes into ReShade.ini, its ranges the ones the 32-bit add-on's settings mirror
// uses.
static const BridgeItem kBridge[] = {
    { "LOOK", "Neural rendering", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      { "NeuralUplift", 1.0f, 0.0f, 1.0f },        { "Enabled", 0.0f, 0.0f, 1.0f } },
    { "LOOK", "Preset", BR_COMBO, nullptr, false, false, kBrPresets, 4, nullptr, -1, 0,
      { "NRPreset", 0.0f, 0.0f, 3.0f },            { "Preset", 0.0f, 0.0f, 3.0f } },
    { "LOOK", "Style", BR_COMBO, nullptr, false, false, kBrStyles, 3, nullptr, -1, 0,
      { "NRStyle", 0.0f, 0.0f, 2.0f },             { "Style", 0.0f, 0.0f, 2.0f } },
    { "LOOK", "Intensity", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRIntensity", 1.0f, 0.0f, 2.0f },         { "Intensity", 1.0f, 0.0f, 2.0f },
      { "NRIntensity", 1.0f, 0.0f, 1.0f } },                                          // 10.8.0 stops at 1

    // What the model is asked for.
    { "MODEL", "Tone", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRLocalTone", 1.0f, 0.0f, 2.0f },         { "LocalTone", 1.0f, 0.0f, 2.0f } },
    { "MODEL", "Global tone", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRGlobalTone", 1.0f, 0.0f, 2.0f },        BR_NONE },
    { "MODEL", "Structure", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRLocalStructure", 1.0f, 0.0f, 2.0f },    { "LocalStructure", 1.0f, 0.0f, 2.0f } },
    { "MODEL", "Skin", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRSkinStructure", -1.0f, -1.0f, 1.0f },   { "SkinStructure", -1.0f, -1.0f, 2.0f } },
    { "MODEL", "Character mask", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      { "NRAutoMask", 1.0f, 0.0f, 1.0f },          { "AutoMask", 1.0f, 0.0f, 1.0f } },
    { "MODEL", "Keep the HUD as it is", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      { "NRUICorrection", 1.0f, 0.0f, 1.0f },      BR_NONE },

    // How often, and on how many pixels.
    { "PASSES AND COST", "Passes", BR_FLOAT, nullptr, true, false, nullptr, 0, nullptr, -1, 0,
      { "NRPasses", 1.0f, 1.0f, 4.0f },            { "Passes", 1.0f, 1.0f, 2.0f } },   // 2 = OptiScaler's limit without its "unlock up to 10"
    { "PASSES AND COST", "Working resolution", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRResolutionScale", 1.0f, 0.33f, 1.0f },  { "WorkingScale", 1.0f, 0.25f, 1.0f } },   // OptiScaler itself goes to 2.0

    // How much of the model's picture is kept when it is put back over the game's.
    { "FINAL PICTURE", "Detail strength", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRTransferStrength", 1.0f, 0.0f, 1.0f },  { "TransferStrength", 1.0f, 0.0f, 2.0f } },
    { "FINAL PICTURE", "Colour strength", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRColorStrength", 1.0f, 0.0f, 1.0f },     { "ColourStrength", 1.0f, 0.0f, 4.0f } },
    { "FINAL PICTURE", "Highlight guard", BR_FLOAT, "%.1fx", false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE,                                     { "MaxRatio", 2.0f, 1.0f, 8.0f } },

    // What the model is told white is.
    { "WHITE POINT", "Paper white", BR_FLOAT, "%.2fx", false, true, nullptr, 0, nullptr, -1, 0,
      { "NRPaperWhiteScale", 1.0f, 0.1f, 10.0f },  { "WhitePointScale", 1.0f, 0.25f, 2000.0f } },
    { "WHITE POINT", "White point source", BR_COMBO, nullptr, false, false, kBrWhiteSource, 3, kBrWhiteSourceV, -1, 0,
      BR_NONE,                                     { "WhitePointSource", 0.0f, 0.0f, 2.0f } },
    { "WHITE POINT", "Exposure trim", BR_FLOAT, "%.3fx", false, true, nullptr, 0, nullptr, BR_ROW_WHITE_SOURCE, 1,
      BR_NONE,                                     { "WhitePointTrim", 1.0f, 0.001f, 1000.0f } },
    { "WHITE POINT", "Exposure trim", BR_FLOAT, "%.3fx", false, true, nullptr, 0, nullptr, BR_ROW_WHITE_SOURCE, 2,
      BR_NONE,                                     { "AutoExposureTrim", 5.0f, 0.001f, 1000.0f } },
    { "WHITE POINT", "Highlight protection (%)", BR_FLOAT, "%.0f", false, false, nullptr, 0, nullptr, BR_ROW_WHITE_SOURCE, 2,
      BR_NONE,                                     { "AutoExposureHighlightProtection", 0.0f, 0.0f, 100.0f } },
    { "WHITE POINT", "Diffuse white (nits)", BR_FLOAT, "%.0f", false, false, nullptr, 0, nullptr, -1, 0,
      { "NRDiffuseWhiteNits", 203.0f, 80.0f, 1000.0f }, BR_NONE },
    // ---- From here on: settings only ONE tool has. They are shown on the panel's second tab,
    // named after the tool (a row is "common" when both tools have it). Appended, never
    // inserted: BR_ROW_WHITE_SOURCE above is an index into this table.

    // OptiScaler DLSS-NR (its menu: DlssNr_MenuControls.cpp).
    { "PASSES", "Unlock up to 10 passes", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "UnlockPasses", 0.0f, 0.0f, 1.0f } },
    { "SKIN AND ENVIRONMENT (FINAL EDIT)", "Separate skin / environment controls", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "SkinProtection", 0.0f, 0.0f, 1.0f } },
    { "SKIN AND ENVIRONMENT (FINAL EDIT)", "Skin detail / lighting", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "SkinDetail", 1.0f, 0.0f, 1.0f } },
    { "SKIN AND ENVIRONMENT (FINAL EDIT)", "Skin colour", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "SkinColour", 1.0f, 0.0f, 1.0f } },
    { "SKIN AND ENVIRONMENT (FINAL EDIT)", "Environment detail / lighting", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "EnvironmentDetail", 1.0f, 0.0f, 1.0f } },
    { "SKIN AND ENVIRONMENT (FINAL EDIT)", "Environment colour", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, { "EnvironmentColour", 1.0f, 0.0f, 1.0f } },

    // RenoDX DLSS5 Generic (keys as v6.1 saves them; labels from its own page).
    { "STACKING", "Denoise before upscaling (pre-SR)", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      { "NRPreUpscale", 0.0f, 0.0f, 1.0f } },
    { "STACKING", "Chained temporal history", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      { "NRChainedHistory", 1.0f, 0.0f, 1.0f } },

    // RenoDX DLSS5 from v8 on (read off 10.8.0: names from the ini it saves, limits from what it
    // clamps an out-of-range value to, the order of each choice from what its log says it did).
    // "Adjust NR's look" reshapes what the model returned; the rows under it apply while it is on.
    { "LOOK OF THE RESULT", "Adjust the model's result", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, -1, 0,
      BR_NONE, BR_NONE, { "NRLookMode", 0.0f, 0.0f, 1.0f } },
    { "LOOK OF THE RESULT", "Edit strength", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookStrength", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Brightening", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookBrighten", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Darkening", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookDarken", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Colour change", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookColour", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Hue shift", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookHue", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Shadows", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookShadows", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Midtones", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookMidtones", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Highlights", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookHighlights", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Large-scale tone", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookTone", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Fine detail", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookDetail", 1.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Detail radius", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookDetailRadius", 1.0f, 0.25f, 4.0f } },
    { "LOOK OF THE RESULT", "Max brightening (stops, 0 = none)", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookMaxBrighten", 0.0f, 0.0f, 4.0f } },
    { "LOOK OF THE RESULT", "Max darkening (stops, 0 = none)", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookMaxDarken", 0.0f, 0.0f, 4.0f } },
    { "LOOK OF THE RESULT", "Max colour change (0 = none)", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookMaxColour", 0.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Halo suppression", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookHalo", 0.0f, 0.0f, 1.0f } },
    { "LOOK OF THE RESULT", "Halo next to dark objects", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookHaloBrightSide", 0.0f, 0.0f, 1.0f } },
    { "LOOK OF THE RESULT", "Stabilize", BR_COMBO, nullptr, false, false, kBrRenoStabilize, 3, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookStabilize", 0.0f, 0.0f, 2.0f } },
    { "LOOK OF THE RESULT", "Stabilize time (ms)", BR_FLOAT, "%.0f", false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookStabilizeMs", 60.0f, 10.0f, 250.0f } },
    { "LOOK OF THE RESULT", "Stabilize fine detail too", BR_BOOL, nullptr, false, false, nullptr, 0, nullptr, BR_ROW_RENO_LOOK, 1,
      BR_NONE, BR_NONE, { "NRLookStabilizeDetail", 0.0f, 0.0f, 1.0f } },
    { "STABILITY", "Detail stability", BR_COMBO, nullptr, false, false, kBrRenoDetail, 3, nullptr, -1, 0,
      BR_NONE, BR_NONE, { "NRDetailStability", 0.0f, 0.0f, 2.0f } },
    { "HIGHLIGHTS AND COLOUR", "Style highlight guard (stops)", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 1,
      BR_NONE, BR_NONE, { "NRStyleHighlightGuard", 0.0f, 0.0f, 2.0f } },
    { "HIGHLIGHTS AND COLOUR", "Chroma clamp (stops)", BR_FLOAT, "%.2f", false, false, nullptr, 0, nullptr, -1, 1,
      BR_NONE, BR_NONE, { "NRChromaClamp", 1.0f, 0.25f, 2.0f } },
};
static const int kBridgeCount = static_cast<int>(sizeof(kBridge) / sizeof(kBridge[0]));
static_assert(sizeof(kBridge) / sizeof(kBridge[0]) <= 64, "g_br and its companions hold 64 rows");

static int       g_br_backend;
static bool      g_br_lecram;
static float     g_br[64], g_br_saved[64];
static bool      g_br_reno8;          // the RenoDX build behind the panel is v8 or newer (see BridgeLoad)
static bool      g_br_rowlive[64];    // the tool takes this row's setting while it runs (see BridgeLoad)
static bool      g_br_mv_hidden;      // the motion-vector notice was hidden for this run of the game ("Hide")
static int       g_br_mv_do;         // asked for on the panel, done at the next tick: 1 use the built-in vectors, 2 switch the other shaders off
static bool      g_br_apply_full;     // the Apply button was pressed: rows that need a restart go too
static char      g_br_livekeys[8192]; // RenoDX: the names its control interface lists, each between line ends
static bool      g_br_absent[64];     // this build of the tool has no such setting (see BridgeLoad)
static bool      g_br_loaded;
static int       g_br_tab;            // 0 = the settings tools share, 1 = the ones only this tool has
static char      g_br_note[160];      // the last thing that happened, shown at the foot of the panel
static bool      g_panel_wanted;      // the user's intent
static float     g_panel_t;           // the slide: 0 = out of sight, 1 = fully in
static ULONGLONG g_panel_tick;        // when g_panel_t was last stepped; 0 = not animating

enum class FeedPanelEditState { idle, pending, applied, restart, rejected, unverified };
enum class FeedPanelEditPath { none, renodx, optiscaler, helper_rebuild };
struct FeedPanelEdit
{
    FeedPanelEditState state = FeedPanelEditState::idle;
    FeedPanelEditPath path = FeedPanelEditPath::none;
    bool queued = false;
    float requested = 0.0f, submitted = 0.0f;
    ULONGLONG checkAfter = 0, deadline = 0, queueDeadline = 0, nextDispatch = 0;
    FeedOptiAck opti;
};
static FeedPanelEdit g_br_edit[kBridgeCount];
static bool g_br_stopping;
static ULONGLONG g_br_rebuild_after;

static bool FeedPanelStreamsSlider(int row)
{
    return g_panel_env.output_preview && g_br_backend == BR_BACKEND_RENODX && g_br_rowlive[row];
}

static bool FeedPanelNeedsService()
{
    if (g_br_stopping) return true;
    for (const auto &edit : g_br_edit) if (edit.queued || edit.path != FeedPanelEditPath::none) return true;
    return false;
}

static bool FeedPanelHasUnresolved()
{
    if (FeedPanelNeedsService()) return true;
    for (const auto &edit : g_br_edit)
        if (edit.state == FeedPanelEditState::restart || edit.state == FeedPanelEditState::rejected ||
            edit.state == FeedPanelEditState::unverified) return true;
    return false;
}

static bool FeedPanelLiveControlsBlocked()
{
    return g_panel_env.live_controls_blocked != nullptr && g_panel_env.live_controls_blocked();
}

static const char *FeedPanelSettingStatus(int row)
{
    const auto &edit = g_br_edit[row];
    if (g_br_rowlive[row] && FeedPanelLiveControlsBlocked()) return "Restart game";
    if (edit.queued || edit.path != FeedPanelEditPath::none) return "Pending";
    switch (edit.state)
    {
    case FeedPanelEditState::restart: return "Restart game";
    case FeedPanelEditState::rejected: return "Rejected";
    case FeedPanelEditState::unverified: return "Unverified";
    default: return !g_br_rowlive[row] && !g_panel_env.helper ? "Next launch" : "";
    }
}

static bool FeedPanelVisible() { return g_panel_wanted || g_panel_t > 0.0f; }
static bool BridgeDirty();
// Opening reads the tool's settings again (the tool may have loaded since, or its own page may
// have changed them) -- unless there are changes here that were not applied yet.
static void FeedPanelShow(bool show)
{
    if (show && !g_panel_wanted &&
        !(g_br_loaded && (FeedPanelLiveControlsBlocked() || BridgeDirty() || FeedPanelHasUnresolved()))) g_br_loaded = false;
    g_panel_wanted = show;
}

static const char *BridgeBackendName()
{
    if (g_br_lecram) return "DLSS NR (Lecram)";
    return g_br_backend == BR_BACKEND_RENODX ? "RenoDX DLSS5 Generic" : g_br_backend == BR_BACKEND_OPTI ? "OptiScaler DLSS-NR"
         : "no supported tool";
}
static const char *BridgeBackendShort()
{
    if (g_br_lecram) return "Lecram";
    return g_br_backend == BR_BACKEND_OPTI ? "OptiScaler" : "Generic";   // short: it labels half-width tabs
}
static const BridgeSide &BridgeSideOf(int i)
{
    if (g_br_backend == BR_BACKEND_OPTI) return kBridge[i].opti;
    return g_br_reno8 && kBridge[i].reno8.key != nullptr ? kBridge[i].reno8 : kBridge[i].reno;
}
static const char *BridgeKey(int i) { return g_br_absent[i] ? nullptr : BridgeSideOf(i).key; }
static const char *BridgeSection()  { return g_br_backend == BR_BACKEND_OPTI ? "DlssNr" : "RenoDX.DLSS5"; }

// A setting only one of the tools has: it goes on the tool's own tab.
static bool BridgeSpecific(int i)
{
    const BridgeItem &it = kBridge[i];
    return (it.reno.key != nullptr || it.reno8.key != nullptr) + (it.opti.key != nullptr) < 2;
}
// A row is on show when the tool has the setting, it belongs to the tab in front, and the
// choice it depends on is the one made.
static bool BridgeShown(int i)
{
    const BridgeItem &it = kBridge[i];
    return BridgeKey(i) != nullptr && BridgeSpecific(i) == (g_br_tab == 1) &&
           (it.need_row < 0 || static_cast<int>(g_br[it.need_row]) == it.need_item);
}
// The number the tool is given for a row: its spelling of a tick, its combo value, its units.
static float BridgeOut(int i, float value)
{
    const BridgeItem &it = kBridge[i];
    const BridgeSide &s = BridgeSideOf(i);
    if (it.kind == BR_BOOL)  return value != 0.0f ? (s.on != 0.0f ? s.on : 1.0f) : 0.0f;
    if (it.kind == BR_COMBO) return it.values != nullptr ? static_cast<float>(it.values[static_cast<int>(value)]) : value;
    return s.scale != 0.0f ? value * s.scale : value;
}

// One value of the tool's saved settings. OptiScaler's are a plain ini; the RenoDX add-on
// saves through its ReShade -- the helper's ini when the tool is over there, this process's
// own ReShade (asked through the add-on API, so nothing is written behind its back) when not.
static bool BridgeRead(const char *section, const char *key, char *out, size_t cb)
{
    out[0] = '\0';
    if (g_br_backend == BR_BACKEND_OPTI || g_panel_env.helper)
    {
        const char *file = g_br_backend == BR_BACKEND_OPTI ? g_panel_env.opti_ini : g_panel_env.reshade_ini;
        GetPrivateProfileStringA(section, key, "\x01", out, static_cast<DWORD>(cb), file);
        if (out[0] == '\x01') { out[0] = '\0'; return false; }
        return true;
    }
    size_t size = cb;
    return reshade::get_config_value(nullptr, section, key, out, &size) && out[0] != '\0';
}
static bool BridgeWrite(const char *section, const char *key, const char *value)
{
    if (g_br_backend == BR_BACKEND_OPTI || g_panel_env.helper)
        return WritePrivateProfileStringA(section, key, value,
            g_br_backend == BR_BACKEND_OPTI ? g_panel_env.opti_ini : g_panel_env.reshade_ini) != 0;
    reshade::set_config_value(nullptr, section, key, value);
    return true;
}

static void BridgeLoad()
{
    if (g_panel_env.refresh != nullptr) g_panel_env.refresh();
    // OptiScaler's neural fork if it is there; else the Generic RenoDX add-on (any name that
    // starts renodx-dlss5 -- ShortFuse's renodx-dlss.addon64 does not, and is not a tool here).
    // Deep Fried Chicken keeps its own file and its own page, and is not bridged by this prototype.
    char path[MAX_PATH];
    sprintf_s(path, "%srenodx-dlss5*.addon64", g_panel_env.tool_dir);
    WIN32_FIND_DATAA fd;
    const HANDLE fh = FindFirstFileA(path, &fd);
    const bool have_reno = fh != INVALID_HANDLE_VALUE;
    if (have_reno) FindClose(fh);
    g_br_lecram = !g_panel_env.opti_nr && !have_reno && g_panel_env.lecram;
    g_br_backend = g_panel_env.opti_nr ? BR_BACKEND_OPTI : (have_reno || g_br_lecram) ? BR_BACKEND_RENODX : BR_BACKEND_NONE;

    char buf[64];
    // A RenoDX build from v8 on writes ConfigVersion and its whole set of settings into its
    // section. Once it has, a name that is not there is one this build does not have (10.8.0
    // has neither NRGlobalTone nor NRPreUpscale): the row is left out, not shown doing nothing.
    const bool full_set = g_br_backend == BR_BACKEND_RENODX && !g_br_lecram && BridgeRead(BridgeSection(), "ConfigVersion", buf, sizeof(buf));
    const bool unlocked = g_br_backend == BR_BACKEND_OPTI &&
        BridgeRead(BridgeSection(), "UnlockPasses", buf, sizeof(buf)) &&
        (_stricmp(buf, "true") == 0 || strcmp(buf, "1") == 0);
    g_br_reno8 = full_set || g_br_lecram;
    for (int i = 0; i < kBridgeCount; ++i)
    {
        const BridgeItem &it = kBridge[i];
        const BridgeSide &side = BridgeSideOf(i);
        g_br[i] = side.def;
        g_br_absent[i] = false;
        g_br_edit[i] = {};
        if (g_br_backend == BR_BACKEND_NONE || g_br_lecram || side.key == nullptr) { g_br_saved[i] = g_br[i]; continue; }
        const bool have = BridgeRead(BridgeSection(), side.key, buf, sizeof(buf));
        if (!have && full_set) { g_br_absent[i] = true; g_br_saved[i] = g_br[i]; continue; }
        // OptiScaler writes "auto" for "my own default", and booleans as words.
        if (have && _stricmp(buf, "auto") != 0 && buf[0] != '\0')
        {
            float v = _stricmp(buf, "true") == 0 ? 1.0f : _stricmp(buf, "false") == 0 ? 0.0f : static_cast<float>(atof(buf));
            if (it.kind == BR_COMBO && it.values != nullptr)   // the number in the file -> which item that is
            {
                int item = 0;
                for (int k = 0; k < it.item_count; ++k) if (it.values[k] == static_cast<int>(v)) item = k;
                v = static_cast<float>(item);
            }
            else if (it.kind == BR_BOOL) v = v != 0.0f ? 1.0f : 0.0f;      // any "on" the tool has is a tick
            else if (it.kind == BR_FLOAT && side.scale != 0.0f) v /= side.scale;
            const float limit = unlocked && strcmp(side.key, "Passes") == 0 ? 10.0f : side.hi;
            g_br[i] = std::clamp(v, side.lo, limit);
        }
        g_br_saved[i] = g_br[i];
    }
    // Which rows the tool takes while it runs. OptiScaler with live settings: all of them.
    // RenoDX: the names its control interface lists -- asked directly when it is in this
    // process, read from the file the helper writes at start when it is over there.
    g_br_livekeys[0] = '\n';
    g_br_livekeys[1] = '\0';
    if (g_br_backend == BR_BACKEND_RENODX && (g_panel_env.submit_setting != nullptr || g_panel_env.send_setting != nullptr))
    {
        const int available = g_panel_env.live_keys != nullptr
            ? g_panel_env.live_keys(g_br_livekeys + 1, sizeof(g_br_livekeys) - 1) : -1;
        if (g_panel_env.helper && available < 0)
        {
            char lp[MAX_PATH];
            sprintf_s(lp, "%sdlss5-feed-live.txt", g_panel_env.tool_dir);
            FILE *lf = nullptr;
            if (fopen_s(&lf, lp, "r") == 0 && lf != nullptr)
            {
                const size_t got = fread(g_br_livekeys + 1, 1, sizeof(g_br_livekeys) - 2, lf);
                g_br_livekeys[1 + got] = '\0';
                fclose(lf);
            }
        }
    }
    int live_rows = 0;
    for (int i = 0; i < kBridgeCount; ++i)
    {
        g_br_rowlive[i] = false;
        const char *k = BridgeKey(i);
        if (k == nullptr) continue;
        if (g_br_backend == BR_BACKEND_OPTI) g_br_rowlive[i] = g_panel_env.opti_live && FeedOptiIsLive(k);
        else if (g_br_backend == BR_BACKEND_RENODX)
        {
            char pat[96];
            sprintf_s(pat, "\n%s\n", k);
            g_br_rowlive[i] = strstr(g_br_livekeys, pat) != nullptr;
        }
        if (g_br_lecram)
        {
            const auto &item = kBridge[i];
            const auto &side = BridgeSideOf(i);
            float value = 0.0f;
            bool available = g_br_rowlive[i] && g_panel_env.get_setting != nullptr && g_panel_env.get_setting(k, &value);
            if (available && item.kind == BR_COMBO && item.values != nullptr)
            {
                int choice = 0;
                while (choice < item.item_count && value != static_cast<float>(item.values[choice])) ++choice;
                available = choice < item.item_count;
                value = static_cast<float>(choice);
            }
            else if (available && item.kind == BR_BOOL) value = value != 0.0f ? 1.0f : 0.0f;
            else if (available && item.kind == BR_FLOAT && side.scale != 0.0f) value /= side.scale;
            available = available && value >= side.lo && value <= side.hi;
            g_br_absent[i] = !available;
            g_br_rowlive[i] = available;
            if (available) g_br[i] = g_br_saved[i] = value;
        }
        if (g_br_rowlive[i]) ++live_rows;
    }
    g_br_loaded = true;
    if (g_br_backend == BR_BACKEND_RENODX && g_panel_env.log != nullptr)
        g_panel_env.log("[panel] %s control interface: %d of this panel's settings are taken while it runs%s", BridgeBackendName(), live_rows,
                        live_rows == 0 ? " (none: an older build, or the helper has not listed them yet)" : "");
    if (g_panel_env.log != nullptr)
        g_panel_env.log("[panel] tool behind the panel: %s (%s); %s", BridgeBackendName(),
            g_panel_env.helper ? "in the helper process" : "in this process",
            g_br_backend == BR_BACKEND_OPTI && g_panel_env.opti_live ? "this build re-reads its file while running: changes apply live"
            : g_br_lecram ? "only the controls confirmed by this build's live interface are shown"
            : g_panel_env.helper ? "changes that need a new helper start its rebuild automatically"
                                 : "startup-only changes wait for the next game launch");
}

// Every row is taken while the tool runs, so nothing ever has to restart (OptiScaler with live settings).
static bool BridgeLive()
{
    return g_br_backend == BR_BACKEND_OPTI && g_panel_env.opti_live;
}
// RenoDX from v10 advertises which rows it accepts while running.
static bool BridgeAnyLive()
{
    for (int i = 0; i < kBridgeCount; ++i) if (g_br_rowlive[i] && BridgeKey(i) != nullptr) return true;
    return false;
}
static bool BridgeDirty()
{
    for (int i = 0; i < kBridgeCount; ++i) if (g_br[i] != g_br_saved[i]) return true;
    return false;
}
static bool BridgeDirtyLive()
{
    for (int i = 0; i < kBridgeCount; ++i) if (g_br_rowlive[i] && g_br[i] != g_br_saved[i]) return true;
    return false;
}
static bool BridgeDirtyRest()
{
    for (int i = 0; i < kBridgeCount; ++i) if (!g_br_rowlive[i] && g_br[i] != g_br_saved[i]) return true;
    return false;
}

static void BridgeFormat(int i, float value, char *buf, size_t cb)
{
    const BridgeItem &it = kBridge[i];
    const float out = BridgeOut(i, value);
    if (it.kind == BR_FLOAT && !it.whole) sprintf_s(buf, cb, "%g", out);
    else if (it.kind == BR_BOOL && g_br_backend == BR_BACKEND_OPTI) strcpy_s(buf, cb, out != 0.0f ? "true" : "false");
    else sprintf_s(buf, cb, "%d", static_cast<int>(out));
}

// Make what changed count, in the tool's own spelling.
//   Rows the tool takes while it runs go to it at once: OptiScaler through its ini, which it
//     re-reads; RenoDX through its control interface, which also saves the value itself.
//   The others are written to the tool's file -- only when the Apply button asked for it
//     (g_br_apply_full): the helper is stopped first (its ReShade saves its ini on exit and
//     would write over ours) and started again after; with no helper they wait for a restart.
#ifdef FEED_DESKTOP_PANEL
static void FeedPanelApply()
{
    const bool running = g_panel_env.running == nullptr || g_panel_env.running();
    const bool full = g_br_apply_full;
    g_br_apply_full = false;
    char buf[64], line[512];

    int n = sprintf_s(line, "[panel] applied LIVE to %s:", BridgeBackendName()), sent = 0;
    if (running)
        for (int i = 0; i < kBridgeCount; ++i)
        {
            const char *key = BridgeKey(i);
            if (key == nullptr || !g_br_rowlive[i] || g_br[i] == g_br_saved[i]) continue;
            BridgeFormat(i, g_br[i], buf, sizeof(buf));
            if (g_br_backend == BR_BACKEND_OPTI) BridgeWrite(BridgeSection(), key, buf);
            else if (g_panel_env.send_setting == nullptr || !g_panel_env.send_setting(key, BridgeOut(i, g_br[i]))) continue;   // stays changed: Apply can still take it
            g_br_saved[i] = g_br[i];
            ++sent;
            if (n > 0 && n < static_cast<int>(sizeof(line)) - 80) n += sprintf_s(line + n, sizeof(line) - n, " %s=%s", key, buf);
        }
    if (sent > 0 && g_panel_env.log != nullptr) g_panel_env.log("%s (%d setting(s), no restart)", line, sent);
    if (!BridgeDirty()) { if (sent > 0) g_br_note[0] = '\0'; return; }
    if (!full) return;   // what is left needs the Apply button

    if (g_panel_env.stop_tool != nullptr) g_panel_env.stop_tool();
    n = sprintf_s(line, "[panel] applied to %s:", BridgeBackendName());
    int wrote = 0;
    for (int i = 0; i < kBridgeCount; ++i)
    {
        const char *key = BridgeKey(i);
        if (key == nullptr || g_br[i] == g_br_saved[i]) continue;   // only what the user moved
        BridgeFormat(i, g_br[i], buf, sizeof(buf));
        BridgeWrite(BridgeSection(), key, buf);
        g_br_saved[i] = g_br[i];
        ++wrote;
        if (n > 0 && n < static_cast<int>(sizeof(line)) - 80) n += sprintf_s(line + n, sizeof(line) - n, " %s=%s", key, buf);
    }
    if (g_panel_env.log != nullptr) g_panel_env.log("%s (%d key(s) in [%s])", line, wrote, BridgeSection());
    if (g_panel_env.restart_tool != nullptr)
    {
        g_panel_env.restart_tool();
        sprintf_s(g_br_note, "Applied %d setting%s. The helper is restarting.", wrote, wrote == 1 ? "" : "s");
    }
    else
        sprintf_s(g_br_note, "Saved %d setting%s. %s reads them when the game is next started.", wrote, wrote == 1 ? "" : "s", BridgeBackendShort());
}

#include "feed_panel_desktop.h"
#else
static void FeedPanelQueue(int row)
{
    if (BridgeKey(row) == nullptr || !std::isfinite(g_br[row])) return;
    auto &edit = g_br_edit[row];
    if (g_br_rowlive[row] && FeedPanelLiveControlsBlocked())
    {
        g_br[row] = g_br_saved[row];
        return;
    }
    // If an earlier edit is in flight, retain just the latest unsent value.
    if (edit.path != FeedPanelEditPath::none && g_br[row] == edit.submitted)
    {
        edit.queued = false;
        return;
    }
    if (edit.path == FeedPanelEditPath::none && g_br[row] == g_br_saved[row] &&
        edit.state != FeedPanelEditState::rejected && edit.state != FeedPanelEditState::unverified)
    {
        edit.queued = false;
        if (edit.state == FeedPanelEditState::pending) edit.state = FeedPanelEditState::idle;
        return;
    }
    edit.requested = g_br[row];
    edit.state = FeedPanelEditState::pending;
    edit.queued = true;
    const ULONGLONG now = GetTickCount64();
    edit.queueDeadline = now + (g_panel_env.helper ? 125000 : 15000);
    if (!g_br_rowlive[row]) g_br_rebuild_after = now + g_panel_env.rebuild_debounce_ms;
}

static void FeedPanelFinish(int row, FeedPanelEditState state)
{
    auto &edit = g_br_edit[row];
    edit.path = FeedPanelEditPath::none;
    edit.opti = {};
    edit.state = edit.queued ? FeedPanelEditState::pending : state;
    if (state == FeedPanelEditState::applied || state == FeedPanelEditState::restart)
        g_br_saved[row] = edit.submitted;
    else if (state == FeedPanelEditState::rejected && !edit.queued && g_br[row] == edit.submitted)
        g_br[row] = g_br_saved[row];
    if (g_panel_env.log == nullptr) return;
    const char *status = state == FeedPanelEditState::applied ? "confirmed by backend" :
        state == FeedPanelEditState::restart ? "saved for next game launch" :
        state == FeedPanelEditState::rejected ? "rejected" : "live effect unverified";
    g_panel_env.log("[panel] %s=%g: %s", BridgeKey(row), BridgeOut(row, edit.submitted), status);
}

static void FeedPanelObserve(int row, FeedPanelSettingResult result, ULONGLONG now)
{
    if (result == FeedPanelSettingResult::pending && now < g_br_edit[row].deadline) return;
    FeedPanelFinish(row, result == FeedPanelSettingResult::applied ? FeedPanelEditState::applied :
        result == FeedPanelSettingResult::rejected ? FeedPanelEditState::rejected : FeedPanelEditState::unverified);
}

static void FeedPanelBegin(int row, FeedPanelEditPath path, ULONGLONG now)
{
    auto &edit = g_br_edit[row];
    edit.submitted = edit.requested;
    edit.queued = false;
    edit.path = path;
    edit.checkAfter = now;
    edit.deadline = now + (path == FeedPanelEditPath::helper_rebuild ? 125000 : 15000);
}

static bool FeedPanelOptiScaleExtent(unsigned int &width, unsigned int &height)
{
    return g_panel_env.opti_frame_size != nullptr && g_panel_env.opti_frame_size(width, height) &&
        FeedOptiUsesOrdinaryPostSr(g_panel_env.opti_ini);
}

// Run backend calls after drawing. The x86/helper build drains this routine
// inside its existing frame lock; the native build drains it from Present.
static void FeedPanelApply()
{
    const ULONGLONG now = GetTickCount64();
    const bool starting = g_panel_env.starting != nullptr && g_panel_env.starting();
    const bool running = g_panel_env.running == nullptr || g_panel_env.running();
    const bool blocked = FeedPanelLiveControlsBlocked();
    for (int i = 0; i < kBridgeCount; ++i)
    {
        auto &edit = g_br_edit[i];
        if (blocked && g_br_rowlive[i] && (edit.queued || edit.path != FeedPanelEditPath::none))
        {
            if (edit.queued) FeedPanelBegin(i, FeedPanelEditPath::none, now);
            FeedPanelFinish(i, FeedPanelEditState::unverified);
            g_br[i] = g_br_saved[i];
            continue;
        }
        // The OptiScaler file transport is last-writer-wins. Supersede a
        // pending log watch when another committed value replaces it.
        if (edit.path == FeedPanelEditPath::optiscaler && edit.queued)
        {
            edit.path = FeedPanelEditPath::none;
            edit.opti = {};
        }
        if (edit.queued && edit.path == FeedPanelEditPath::none && !g_br_stopping && now >= edit.queueDeadline)
        {
            FeedPanelBegin(i, FeedPanelEditPath::none, now);
            FeedPanelFinish(i, FeedPanelEditState::unverified);
            continue;
        }
        if (edit.path == FeedPanelEditPath::none || now < edit.checkAfter) continue;
        edit.checkAfter = now + (FeedPanelStreamsSlider(i) && edit.path == FeedPanelEditPath::renodx ? 25 : 100);
        const char *key = BridgeKey(i);
        const float value = BridgeOut(i, edit.submitted);
        auto result = FeedPanelSettingResult::pending;
        if (edit.path == FeedPanelEditPath::optiscaler)
        {
            if (edit.opti.frameWidth != 0)
            {
                unsigned int width = 0, height = 0;
                if (!FeedPanelOptiScaleExtent(width, height) || width != edit.opti.frameWidth || height != edit.opti.frameHeight)
                    edit.opti.frameWidth = edit.opti.frameHeight = 0;
            }
            result = FeedOptiPoll(edit.opti, key, value);
        }
        else if (edit.path == FeedPanelEditPath::renodx && g_panel_env.poll_setting != nullptr)
            result = g_panel_env.poll_setting(key, value);
        else if (edit.path == FeedPanelEditPath::helper_rebuild && running && !starting)
            result = g_panel_env.verify_rebuild != nullptr
                ? g_panel_env.verify_rebuild(key, value) : FeedPanelSettingResult::unverified;
        FeedPanelObserve(i, result, now);
    }
    if (starting && !g_br_stopping) return;

    bool rebuild = false;
    bool outstanding = false;
    for (int i = 0; i < kBridgeCount; ++i)
    {
        const auto &edit = g_br_edit[i];
        outstanding |= edit.path != FeedPanelEditPath::none;
        rebuild |= edit.queued && edit.path == FeedPanelEditPath::none && !g_br_rowlive[i];
    }
    rebuild = g_br_stopping || (rebuild && g_panel_env.restart_tool != nullptr && !outstanding && now >= g_br_rebuild_after);
    if (rebuild)
    {
        if (g_panel_env.begin_stop_tool != nullptr && g_panel_env.stop_tool_done != nullptr)
        {
            if (!g_br_stopping)
            {
                g_panel_env.begin_stop_tool();
                g_br_stopping = true;
            }
            if (!g_panel_env.stop_tool_done()) return;
        }
        else if (g_panel_env.stop_tool != nullptr) g_panel_env.stop_tool();
    }

    for (int i = 0; i < kBridgeCount; ++i)
    {
        auto &edit = g_br_edit[i];
        if (!edit.queued || edit.path != FeedPanelEditPath::none || BridgeKey(i) == nullptr) continue;
        if (g_br_rowlive[i] && (!running || rebuild || now < edit.nextDispatch)) continue;
        if (!g_br_rowlive[i] && g_panel_env.restart_tool != nullptr && !rebuild) continue;
        const auto path = g_br_rowlive[i] ? (g_br_backend == BR_BACKEND_OPTI ? FeedPanelEditPath::optiscaler : FeedPanelEditPath::renodx)
            : rebuild ? FeedPanelEditPath::helper_rebuild : FeedPanelEditPath::none;
        FeedPanelBegin(i, path, now);
        const char *key = BridgeKey(i);
        if (path == FeedPanelEditPath::renodx)
        {
            if (FeedPanelStreamsSlider(i)) edit.nextDispatch = now + 50;
            const auto result = g_panel_env.submit_setting != nullptr
                ? g_panel_env.submit_setting(key, BridgeOut(i, edit.submitted)) : FeedPanelSettingResult::unverified;
            FeedPanelObserve(i, result, now);
            continue;
        }
        if (path == FeedPanelEditPath::optiscaler)
        {
            edit.opti = FeedOptiBeginAck(g_panel_env.opti_ini);
            unsigned int width = 0, height = 0;
            if (std::string_view(key) == "WorkingScale" && FeedPanelOptiScaleExtent(width, height))
            {
                edit.opti.frameWidth = width;
                edit.opti.frameHeight = height;
            }
        }
        char value[64];
        BridgeFormat(i, edit.submitted, value, sizeof(value));
        if (!BridgeWrite(BridgeSection(), key, value)) FeedPanelFinish(i, FeedPanelEditState::rejected);
        else if (path == FeedPanelEditPath::none) FeedPanelFinish(i, FeedPanelEditState::restart);
        else if (path == FeedPanelEditPath::optiscaler && edit.opti.file.empty()) FeedPanelFinish(i, FeedPanelEditState::unverified);
    }
    if (rebuild)
    {
        g_br_stopping = false;
        g_panel_env.restart_tool();
    }
}

static void FeedPanelHideNow()
{
    FeedPanelShow(false);
    g_panel_t = 0.0f;
    g_panel_tick = 0;
    g_br_mv_do = 0;
}

static void FeedPanelStep(bool in)
{
    const ULONGLONG now = GetTickCount64();
    float dt = g_panel_tick == 0 ? 0.0f : static_cast<float>(now - g_panel_tick) / 180.0f;   // 180 ms end to end
    if (dt > 0.34f) dt = 0.34f;   // a hitch must not swallow the whole slide
    g_panel_tick = now;
    g_panel_t += in ? dt : -dt;
    if (g_panel_t < 0.0f) g_panel_t = 0.0f; else if (g_panel_t > 1.0f) g_panel_t = 1.0f;
}

// Once a frame, from reshade_present: the shortcut, the slide, and the input that goes with it.
// A binding only accepts its configured modifiers.
static void FeedPanelTick(reshade::api::effect_runtime *rt)
{
    if (rt == nullptr) return;
    if (FeedPanelNeedsService())
    {
        if (g_panel_env.schedule_apply != nullptr) g_panel_env.schedule_apply();
        else FeedPanelApply();
    }
    const int held = (rt->is_key_down(VK_MENU) ? 1 : 0) | (rt->is_key_down(VK_CONTROL) ? 2 : 0) | (rt->is_key_down(VK_SHIFT) ? 4 : 0);
    if (g_panel_key > 0 && held == (g_panel_mods & 7) &&
        !rt->is_key_down(VK_LWIN) && !rt->is_key_down(VK_RWIN) &&
        rt->is_key_pressed(static_cast<uint32_t>(g_panel_key)))
    {
        FeedPanelShow(!g_panel_wanted);
        if (g_panel_env.log != nullptr) g_panel_env.log("[panel] %s (shortcut)", g_panel_wanted ? "shown" : "hidden");
    }
    else if (g_panel_wanted && rt->is_key_pressed(VK_ESCAPE))
    {
        g_panel_wanted = false;
        if (g_panel_env.log != nullptr) g_panel_env.log("[panel] hidden (Escape)");
    }
    if (!g_panel_wanted && g_panel_t <= 0.0f) { g_panel_tick = 0; return; }
    if (!g_br_loaded) BridgeLoad();
    // RenoDX in the helper: which settings it takes while running is known only once the helper
    // has started and listed them. A panel opened before that looks again every two seconds, a
    // few times, as long as nothing on it has been changed.
    {
        static ULONGLONG looked = 0;
        static int       tries = 0;
        const ULONGLONG  now = GetTickCount64();
        if (!g_panel_wanted) tries = 0;
        else if (g_br_backend == BR_BACKEND_RENODX && g_panel_env.helper && g_panel_env.send_setting != nullptr &&
                 tries < 15 && now - looked > 2000)
        {
            looked = now;
            bool any = false, dirty = false;
            for (int i = 0; i < kBridgeCount; ++i) { any = any || g_br_rowlive[i]; dirty = dirty || g_br[i] != g_br_saved[i]; }
            if (!any && !dirty && !FeedPanelHasUnresolved()) { ++tries; g_br_loaded = false; }
        }
    }
    FeedPanelStep(g_panel_wanted);
    if (g_br_mv_do == 1 && g_panel_env.mv_use_builtin != nullptr) g_panel_env.mv_use_builtin();
    else if (g_br_mv_do == 2 && g_panel_env.mv_disable_others != nullptr) g_panel_env.mv_disable_others();
    g_br_mv_do = 0;
    if (g_panel_wanted) rt->block_input_next_frame();   // the panel owns mouse and keyboard while it is up
}

#include "feed_panel_draw.h"
#endif
