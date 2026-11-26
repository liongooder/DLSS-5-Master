// Feeder's control bridge presented with the NeuralMaster panel widgets.
// Included after the bridge and event handlers in feed_panel.h.
#pragma once

#include "neuralmaster_panel_ui.hpp"

#include <atomic>
#include <cfloat>

namespace feed_panel_ui = neuralmaster::panel::ui;

// NeuralMaster T125 measures the layout in the current panel font.
static constexpr float kFeedPanelWidth = 30.5f;
static constexpr float kFeedPanelGap = 0.5f;
static constexpr float kFeedPanelCardPadding = 0.75f;
static constexpr float kFeedPanelCardPaddingY = 0.45f;
static constexpr float kFeedPanelCardHeader = 1.65f;
static constexpr float kFeedPanelRow = 1.72f;
static constexpr float kFeedPanelSwitchRow = 1.8f;
static constexpr float kFeedPanelControl = 1.32f;

struct FeedPanelPlacement
{
    ImVec2 position{};
    ImVec2 drag_origin{};
    float height = 0.0f;
    bool placed = false;
    bool dragging = false;
    int last_frame = -1;
};
static FeedPanelPlacement g_feed_panel_placement;

static float FeedPanelTextPixels(float viewport_height, int percent)
{
    const float scale = percent > 0 ? static_cast<float>(percent) / 100.0f : 1.0f;
    return std::round(std::clamp(viewport_height / 72.0f * scale, 11.0f, 64.0f));
}

// ReShade's public ImGui table uses its existing font atlas. Like NeuralMaster,
// seed Segoe UI before the first overlay initializes, only when no font was chosen.
static void FeedPanelApplyDefaultFont()
{
    constexpr const char *section = "DLSS5.Feeder.Panel";
    char chosen[8]{};
    size_t bytes = sizeof(chosen);
    if (reshade::get_config_value(nullptr, section, "FontChosen", chosen, &bytes) && strcmp(chosen, "1") == 0)
        return;

    char current[MAX_PATH * 3]{};
    bytes = sizeof(current);
    const bool has_font = reshade::get_config_value(nullptr, "STYLE", "Font", current, &bytes);
    if ((has_font && current[0] != '\0') || bytes > sizeof(current))
    {
        reshade::set_config_value(nullptr, section, "FontChosen", "1");
        return;
    }

    wchar_t windows[MAX_PATH]{};
    const UINT length = GetWindowsDirectoryW(windows, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return;
    const std::wstring path = std::wstring(windows, length) + L"\\Fonts\\segoeui.ttf";
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return;
    char utf8[MAX_PATH * 3]{};
    if (WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, utf8, static_cast<int>(sizeof(utf8)), nullptr, nullptr) == 0)
        return;
    reshade::set_config_value(nullptr, "STYLE", "Font", static_cast<const char *>(utf8));
    reshade::set_config_value(nullptr, section, "FontChosen", "1");
}

template <typename Device>
static void FeedPanelInitFont(Device *)
{
    static std::atomic_flag checked = ATOMIC_FLAG_INIT;
    if (!checked.test_and_set(std::memory_order_relaxed)) FeedPanelApplyDefaultFont();
}

static void FeedPanelBlockGap()
{
    const float gap = std::round(feed_panel_ui::Em() * kFeedPanelGap) - ImGui::GetStyle().ItemSpacing.y;
    if (gap > 0.0f) ImGui::SetCursorPosY(ImGui::GetCursorPosY() + gap);
}

static void FeedPanelText(const char *text, ImU32 color, bool wrap = false)
{
    ImGui::PushStyleColor(ImGuiCol_Text, color);
    if (wrap) ImGui::PushTextWrapPos(0.0f);
    ImGui::TextUnformatted(text);
    if (wrap) ImGui::PopTextWrapPos();
    ImGui::PopStyleColor();
}

static bool FeedPanelBeginCard(const char *id)
{
    using namespace feed_panel_ui;
    const float em = Em();
    ImGui::PushStyleColor(ImGuiCol_ChildBg, kColors.card);
    ImGui::PushStyleColor(ImGuiCol_Border, kColors.cardBorder);
    ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, std::round(em * 0.42f));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding,
        ImVec2(std::round(em * kFeedPanelCardPadding), std::round(em * kFeedPanelCardPaddingY)));
    return ImGui::BeginChild(id, ImVec2(0.0f, 0.0f),
        ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding |
        ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_AlwaysAutoResize,
        ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse);
}

static void FeedPanelEndCard()
{
    ImGui::EndChild();
    ImGui::PopStyleVar(2);
    ImGui::PopStyleColor(2);
}

static void FeedPanelSizeControl()
{
    using namespace feed_panel_ui;
    const int actual = g_panel_scale > 0 ? g_panel_scale : 100;
    const int step = ((actual + 5) / 10) * 10;
    const float h = ImGui::GetFrameHeight();
    int requested = -1;
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted("Text size");
    ImGui::SameLine();
    if (FilledButton("##br_size_minus", "-", ImVec2(h, h), false)) requested = step - 10;
    ImGui::SameLine();
    ImGui::AlignTextToFramePadding();
    ImGui::Text("%d%%", actual);
    ImGui::SameLine();
    if (FilledButton("##br_size_plus", "+", ImVec2(h, h), false)) requested = step + 10;
    ImGui::SameLine();
    if (FilledButton("##br_size_auto", "Auto", ImVec2(Em() * 3.6f, h), g_panel_scale == 0)) requested = 0;
    if (requested >= 0)
    {
        g_panel_scale = requested == 0 ? 0 : std::clamp(requested, 50, 400);
        FeedPanelSaveCfg("panel_scale", g_panel_scale);
    }
}

static bool FeedPanelDrawHeader(bool can_open_reshade)
{
    using namespace feed_panel_ui;
    const float em = Em();
    const float icon = std::max(18.0f, em * 1.3f);
    const float h = std::max(icon, em * 1.7f);
    const ImVec2 at = ImGui::GetCursorScreenPos();
    const float width = ImGui::GetContentRegionAvail().x;
    const float icon_y = at.y + (h - icon) * 0.5f;
    const float text_y = at.y + (h - em) * 0.5f;
    const float gap = em * 0.4f;
    float x = at.x + width - icon;
    ImGui::SetCursorScreenPos(ImVec2(x, icon_y));
    if (IconButton("##br_close", Icon::Close, icon, kColors.textDim, kColors.text)) FeedPanelShow(false);
    Tooltip("Close panel");

    x -= icon + gap;
    ImGui::SetCursorScreenPos(ImVec2(x, icon_y));
    if (IconButton("##br_settings", Icon::Gear, icon, kColors.textDim, kColors.text)) ImGui::OpenPopup("##br_panel_options");
    Tooltip("Panel options");
    if (ImGui::BeginPopup("##br_panel_options"))
    {
        FeedPanelSizeControl();
        ImGui::Separator();
        const char *const choices[] = {"Left", "Right"};
        if (Segmented("##br_side", &g_panel_side, choices, 2, ImVec2(em * 13.0f, em * 1.7f)))
        {
            g_feed_panel_placement.placed = false;
            FeedPanelSaveCfg("panel_side", g_panel_side);
        }
        Tooltip("Move panel to a screen edge");
        ImGui::EndPopup();
    }

    bool open_reshade = false;
    if (can_open_reshade)
    {
        x -= TextSize("ReShade").x + gap;
        ImGui::SetCursorScreenPos(ImVec2(x, text_y));
        open_reshade = TextButton("##br_reshade", "ReShade", kColors.textDim, kColors.text);
        Tooltip("Open ReShade's overlay");
    }
    const float title_width = x - at.x - gap;
    if (title_width > 0.0f)
    {
        constexpr std::string_view title = "DLSS 5 Feeder";
        const ImVec2 text_at(at.x + em * 0.15f, text_y);
        if (TextSize(title, true).x <= title_width - em * 0.15f)
            DrawText(ImGui::GetWindowDrawList(), text_at, kColors.text, title, true);
        else
            DrawTextFit(ImGui::GetWindowDrawList(), text_at, kColors.text, title, title_width);
        ImGui::SetCursorScreenPos(at);
        ImGui::InvisibleButton("##br_drag", ImVec2(title_width, h), ImGuiButtonFlags_MouseButtonLeft);
        g_feed_panel_placement.dragging = ImGui::IsItemActive();
        if (ImGui::IsItemActivated()) g_feed_panel_placement.drag_origin = ImGui::GetWindowPos();
        Tooltip("Drag to move the panel");
    }
    else g_feed_panel_placement.dragging = false;
    ImGui::SetCursorScreenPos(at);
    ImGui::Dummy(ImVec2(width, h));
    return open_reshade;
}

static bool FeedPanelDrawToggleValue(int id, const char *label, bool &value, bool disabled)
{
    using namespace feed_panel_ui;
    const ImVec2 size = ToggleSize();
    const ImVec2 at = ImGui::GetCursorScreenPos();
    const float width = ImGui::GetContentRegionAvail().x;
    const float label_width = std::max(1.0f, width - size.x - Em() * 0.7f);
    const auto text = std::string_view(label);
    const float text_height = WrappedTextHeight(text, label_width, 0.0f);
    const float row_height = std::round(Em() * kFeedPanelSwitchRow) - ImGui::GetStyle().ItemSpacing.y;
    const float height = std::max({size.y, text_height, row_height});
    DrawTextWrapped(ImGui::GetWindowDrawList(), ImVec2(at.x, at.y + (height - text_height) * 0.5f),
                    kColors.text, text, label_width);
    ImGui::SetCursorScreenPos(ImVec2(at.x + width - size.x, at.y + (height - size.y) * 0.5f));
    ImGui::PushID(id);
    ImGui::BeginDisabled(disabled);
    const bool changed = Toggle("##br_value", &value);
    ImGui::EndDisabled();
    Tooltip(label);
    ImGui::PopID();
    ImGui::SetCursorScreenPos(at);
    ImGui::Dummy(ImVec2(width, height));
    return changed;
}

static bool FeedPanelDrawToggle(int row, const char *label)
{
    bool value = g_br[row] != 0.0f;
    if (!FeedPanelDrawToggleValue(row, label, value, g_br_rowlive[row] && FeedPanelLiveControlsBlocked()))
        return false;
    g_br[row] = value ? 1.0f : 0.0f;
    FeedPanelQueue(row);
    return true;
}

static bool FeedPanelDrawStatus(bool running, bool starting, bool mixed)
{
    using namespace feed_panel_ui;
    bool commit = false;
    if (FeedPanelBeginCard("##br_status"))
    {
        const ImVec2 at = ImGui::GetCursorScreenPos();
        const float em = Em();
        const float line = em * 1.5f;
        const float width = ImGui::GetContentRegionAvail().x;
        const bool blocked = FeedPanelLiveControlsBlocked();
        const ImU32 color = running && !blocked ? kColors.highlight : kColors.amber;
        const char *status = blocked ? "Rendering stopped. Restart game." :
                             g_br_backend == BR_BACKEND_NONE ? "No NR backend found" :
                             running ? "Connected" : starting ? "Starting helper" : "Backend unavailable";
        DrawStatusDot(ImGui::GetWindowDrawList(), ImVec2(at.x + em * 0.3f, at.y + line * 0.5f), color, running && !blocked);
        DrawText(ImGui::GetWindowDrawList(), ImVec2(at.x + em * 0.95f, at.y + (line - em) * 0.5f), kColors.text, status, true);
        ImGui::Dummy(ImVec2(width, line));

        if (g_br_backend != BR_BACKEND_NONE)
        {
            const char *backend = BridgeBackendName();
            const char *location = g_panel_env.helper ? "64-bit helper" : "In-game";
            Chip(Icon::Chip, backend);
            if (ChipSize(backend).x + ChipSize(location).x + ImGui::GetStyle().ItemSpacing.x <= width)
                ImGui::SameLine();
            Chip(Icon::Monitor, location);
        }

        if (g_panel_env.output_preview)
        {
            bool output = g_panel_feed_output.load(std::memory_order_relaxed);
            if (FeedPanelDrawToggleValue(-1, "Feeder output", output, false))
                g_panel_feed_output.store(output, std::memory_order_relaxed);
        }
        else if (g_br_backend != BR_BACKEND_NONE && BridgeKey(0) != nullptr)
        {
            const char *status = FeedPanelSettingStatus(0);
            char label[96];
            if (status[0] != '\0') sprintf_s(label, "Neural rendering (%s)", status);
            else strcpy_s(label, !g_br_rowlive[0] ? "Neural rendering *" : "Neural rendering");
            commit = FeedPanelDrawToggle(0, label);
        }

        const float gpu = g_panel_env.gpu_ms != nullptr ? g_panel_env.gpu_ms() : 0.0f;
        if (gpu > 0.0f && !blocked && (!g_panel_env.output_preview || g_panel_feed_output))
        {
            char timing[100];
            sprintf_s(timing, "Feed GPU %.1f ms (DLSS + neural) / %.0f fps", gpu, ImGui::GetIO().Framerate);
            FeedPanelText(timing, kColors.highlight, true);
            Tooltip("GPU cost is measured for the feed. FPS is the overlay's frame cadence.");
        }
    }
    FeedPanelEndCard();
    return commit;
}

static void FeedPanelDrawMotionNotice()
{
    using namespace feed_panel_ui;
    if (g_panel_env.mv_mode == nullptr || g_panel_mv_notice == 0 || g_br_mv_hidden) return;
    const int mode = g_panel_env.mv_mode();
    if (mode < 0) return;
    char others[512] = "";
    const int enabled = g_panel_env.mv_enabled != nullptr ? g_panel_env.mv_enabled(others, sizeof(others)) : 0;
    if (mode == 5 && enabled == 0) return;

    if (FeedPanelBeginCard("##br_motion"))
    {
        const float width = ImGui::GetContentRegionAvail().x;
        const float h = ImGui::GetFrameHeight();
        if (mode != 5)
        {
            char description[160];
            const char *provider = g_panel_env.mv_name != nullptr ? g_panel_env.mv_name() : nullptr;
            sprintf_s(description, "Motion vector provider: %s", provider != nullptr ? provider : "External shader");
            FeedPanelText(description, kColors.amber, true);
            FeedPanelText("Switch here or set DLSS5_MV_PROVIDER to 5 in DLSS5_Feed.fx's ReShade preprocessor definitions.", kColors.textDim, true);
            if (g_panel_env.mv_use_builtin != nullptr && FilledButton("##brmv", "Use built-in vectors", ImVec2(width, h), true))
                g_br_mv_do = 1;
        }
        else
        {
            FeedPanelText("External motion shaders are still enabled", kColors.amber, true);
            FeedPanelText(others, kColors.textDim, true);
            FeedPanelText("Keep them enabled if another effect uses them.", kColors.textDim, true);
            if (g_panel_env.mv_disable_others != nullptr && FilledButton("##brmv", "Switch external shaders off", ImVec2(width, h), true))
                g_br_mv_do = 2;
        }
        const float half = (width - ImGui::GetStyle().ItemSpacing.x) * 0.5f;
        if (FilledButton("##brmvh", "Hide", ImVec2(half, h), false)) g_br_mv_hidden = true;
        ImGui::SameLine();
        if (FilledButton("##brmvn", "Never show again", ImVec2(half, h), false))
        {
            g_panel_mv_notice = 0;
            FeedPanelSaveCfg("panel_mv_notice", 0);
        }
    }
    FeedPanelEndCard();
}

static bool FeedPanelDrawSetting(int row, bool mixed)
{
    using namespace feed_panel_ui;
    const BridgeItem &item = kBridge[row];
    const BridgeSide &side = BridgeSideOf(row);
    char label[128];
    const char *status = FeedPanelSettingStatus(row);
    if (status[0] != '\0') sprintf_s(label, "%s (%s)", item.label, status);
    else sprintf_s(label, "%s%s", item.label, !g_br_rowlive[row] ? " *" : "");
    if (item.kind == BR_BOOL) return FeedPanelDrawToggle(row, label);

    const float width = ImGui::GetContentRegionAvail().x;
    const float em = Em();
    const float label_width = width * 0.43f;
    const bool one_line = width >= em * 15.0f && TextSize(label).x < label_width - em * 0.4f;
    if (one_line) ImGui::AlignTextToFramePadding();
    FeedPanelText(label, kColors.text, !one_line);
    if (one_line) ImGui::SameLine(ImGui::GetCursorPosX() + label_width);
    const float control_width = ImGui::GetContentRegionAvail().x;
    const float h = std::max(1.0f, std::round(em * kFeedPanelControl));
    bool commit = false;
    ImGui::PushID(row);
    ImGui::BeginDisabled(g_br_rowlive[row] && FeedPanelLiveControlsBlocked());
    if (item.kind == BR_COMBO)
    {
        const char *const *names = side.items != nullptr ? side.items : item.items;
        const int count = side.items != nullptr ? side.item_count : item.item_count;
        const int current = std::clamp(static_cast<int>(g_br[row]), 0, count - 1);
        if (DropBox("##br_value", names[current], {}, kColors.textDim, false, ImVec2(control_width, h)))
            ImGui::OpenPopup("##br_choices");
        ImGui::SetNextWindowPos(ImGui::GetItemRectMax(), ImGuiCond_Appearing, ImVec2(1.0f, 0.0f));
        ImGui::SetNextWindowSizeConstraints(ImVec2(control_width, 0.0f), ImVec2(FLT_MAX, FLT_MAX));
        if (ImGui::BeginPopup("##br_choices"))
        {
            for (int choice = 0; choice < count; ++choice)
            {
                if (ImGui::Selectable(names[choice], choice == current) && choice != current)
                {
                    g_br[row] = static_cast<float>(choice);
                    commit = true;
                }
                if (choice == current) ImGui::SetItemDefaultFocus();
            }
            ImGui::EndPopup();
        }
    }
    else if (item.whole)
    {
        int maximum = static_cast<int>(side.hi);
        if (g_br_backend == BR_BACKEND_OPTI)
            for (int i = 0; i < kBridgeCount; ++i)
                if (kBridge[i].opti.key != nullptr && strcmp(kBridge[i].opti.key, "UnlockPasses") == 0 && g_br[i] != 0.0f)
                    maximum = 10;
        const int minimum = static_cast<int>(side.lo);
        int value = static_cast<int>(g_br[row]);
        char number[16];
        sprintf_s(number, "%d", value);
        const float value_width = TextSize("10").x + em * 0.4f;
        const auto edit = Slider("##br_value", &value, minimum, maximum, static_cast<int>(side.def),
                                 ImVec2(std::max(em * 3.0f, control_width - value_width - ImGui::GetStyle().ItemSpacing.x), h));
        if (edit.changed) g_br[row] = static_cast<float>(value);
        if (ImGui::IsItemActive() && !g_br_rowlive[row] && g_panel_env.rebuild_debounce_ms != 0)
            g_br_rebuild_after = GetTickCount64() + g_panel_env.rebuild_debounce_ms;
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        if (edit.changed) sprintf_s(number, "%d", value);
        FeedPanelText(number, kColors.highlight);
        commit = edit.released || (edit.changed && FeedPanelStreamsSlider(row));
    }
    else
    {
        constexpr int ticks = 10000;
        const bool logarithmic = item.log && side.lo > 0.0f;
        const float range = logarithmic ? std::log(side.hi / side.lo) : side.hi - side.lo;
        const auto position = [&](float value)
        {
            if (range <= 0.0f) return 0;
            value = std::clamp(value, side.lo, side.hi);
            const float fraction = logarithmic ? std::log(value / side.lo) / range : (value - side.lo) / range;
            return static_cast<int>(std::lround(fraction * ticks));
        };
        const int reset = position(side.def);
        int value = position(g_br[row]);
        char number[32];
        const char *format = item.format != nullptr ? item.format : "%.2f";
        sprintf_s(number, format, g_br[row]);
        const float value_width = em * 4.0f;
        const auto edit = Slider("##br_value", &value, 0, ticks, reset,
            ImVec2(std::max(1.0f, control_width - value_width - ImGui::GetStyle().ItemSpacing.x), h));
        if (edit.changed)
        {
            const float fraction = static_cast<float>(value) / ticks;
            const float next = value == reset ? side.def :
                logarithmic ? side.lo * std::exp(fraction * range) : side.lo + fraction * range;
            g_br[row] = std::clamp(next, side.lo, side.hi);
            sprintf_s(number, format, g_br[row]);
        }
        if (ImGui::IsItemActive() && !g_br_rowlive[row] && g_panel_env.rebuild_debounce_ms != 0)
            g_br_rebuild_after = GetTickCount64() + g_panel_env.rebuild_debounce_ms;
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        FeedPanelText(number, kColors.highlight);
        commit = edit.released || (edit.changed && FeedPanelStreamsSlider(row));
    }
    ImGui::EndDisabled();
    ImGui::PopID();
    if (commit) FeedPanelQueue(row);
    return commit;
}

static bool FeedPanelDrawGroups(bool mixed)
{
    using namespace feed_panel_ui;
    bool commit = false;
    // Row zero is drawn in the status card. Keep the catalog order and its conditional gates.
    for (int first = 1; first < kBridgeCount;)
    {
        const char *group = kBridge[first].group;
        int last = first;
        bool visible = false;
        do
        {
            visible |= BridgeShown(last);
            ++last;
        }
        while (last < kBridgeCount && strcmp(group, kBridge[last].group) == 0);

        if (visible)
        {
            ImGui::PushID(first);
            if (FeedPanelBeginCard("##br_group"))
            {
                const std::string_view heading(group);
                const Icon icon = heading == "LOOK" || heading == "FINAL PICTURE" ? Icon::Picture :
                                  heading == "MODEL" ? Icon::Chip : heading == "WHITE POINT" ? Icon::Sun :
                                  heading == "PASSES" || heading == "PASSES AND COST" ? Icon::Pulse : Icon::Wand;
                const float em = Em();
                const float height = em * kFeedPanelCardHeader - ImGui::GetStyle().ItemSpacing.y;
                const float width = ImGui::GetContentRegionAvail().x;
                const ImVec2 at = ImGui::GetCursorScreenPos();
                const ImVec2 text_at(at.x + em * 1.3f, at.y + (height - em) * 0.5f);
                DrawIcon(ImGui::GetWindowDrawList(), icon, ImVec2(at.x + em * 0.52f, at.y + height * 0.5f), em * 0.95f, kColors.text);
                if (TextSize(heading, true).x <= width - em * 1.3f)
                    DrawText(ImGui::GetWindowDrawList(), text_at, kColors.text, heading, true);
                else
                    DrawTextFit(ImGui::GetWindowDrawList(), text_at, kColors.text, heading, width - em * 1.3f);
                ImGui::Dummy(ImVec2(width, height));
                ImGui::Separator();
                for (int row = first; row < last; ++row)
                    if (BridgeShown(row)) commit |= FeedPanelDrawSetting(row, mixed);
            }
            FeedPanelEndCard();
            ImGui::PopID();
            FeedPanelBlockGap();
        }
        first = last;
    }
    return commit;
}

static void FeedPanelDrawTabs()
{
    using namespace feed_panel_ui;
    bool specific = false;
    for (int row = 0; row < kBridgeCount; ++row)
        specific |= BridgeKey(row) != nullptr && BridgeSpecific(row);
    if (!specific)
    {
        g_br_tab = 0;
        return;
    }

    char backend[64];
    sprintf_s(backend, "%s only", BridgeBackendShort());
    const char *const labels[] = {"Common", backend};
    Segmented("##br_tabs", &g_br_tab, labels, 2, ImVec2(ImGui::GetContentRegionAvail().x, Em() * kFeedPanelControl));
}

static void FeedPanelDrawFooter(bool running, bool starting)
{
    using namespace feed_panel_ui;
    if (g_br_backend == BR_BACKEND_NONE)
        FeedPanelText("Feeder controls RenoDX DLSS5 Generic, DLSS NR (Lecram), or OptiScaler DLSS-NR.", kColors.textDim, true);
    else if (FeedPanelLiveControlsBlocked())
        FeedPanelText("Live controls are paused until you restart the game.", kColors.amber, true);
    else if (FeedPanelNeedsService())
        FeedPanelText("Pending backend confirmation.", kColors.amber, true);
    else if (!running && !starting && g_panel_env.helper)
        FeedPanelText("The helper is unavailable.", kColors.textDim, true);
    else
        FeedPanelText(g_panel_env.restart_tool != nullptr ? "* Rebuilds the helper automatically." :
            "Next launch settings take effect after restarting the game.", kColors.textDim, true);

    if (g_panel_key > 0)
    {
        char key[96];
        FeedPanelKeyName(key, sizeof(key));
        KeyChip("##br_shortcut", key, false, false);
        Tooltip("Toggle panel. Esc closes it.");
    }

}

// From reshade_overlay. Binding the renderer here keeps the stock hotkey, input ownership,
// live edits, and deferred helper restart path shared across all three in-game builds.
static void FeedPanelDraw(reshade::api::effect_runtime *rt)
{
    using namespace feed_panel_ui;
    ImGuiIO &io = ImGui::GetIO();
    auto &placement = g_feed_panel_placement;
    if (!g_panel_wanted)
    {
        placement.dragging = false;
        return;
    }
    if (io.DisplaySize.x <= 0.0f || io.DisplaySize.y <= 0.0f) return;
    io.MouseDrawCursor = true;

    const ImVec2 display = io.DisplaySize;
    const float base = ImGui::GetStyle().FontSizeBase;
    const float shown = ImGui::GetFontSize();
    const float font_scale = base > 0.0f && shown > 0.0f ? shown / base : 1.0f;
    ImGui::PushFont(nullptr, FeedPanelTextPixels(display.y, g_panel_scale) / font_scale);
    const float em = Em();
    const float width = std::min(display.x, std::max(em * 12.0f, std::min(em * kFeedPanelWidth, display.x - em)));
    const float max_height = std::min(display.y, std::max(em * 10.0f, display.y - em * 2.0f));
    const auto clamp_to_viewport = [&](ImVec2 pos, float height) {
        return ImVec2(std::clamp(pos.x, 0.0f, std::max(0.0f, display.x - width)),
                      std::clamp(pos.y, 0.0f, std::max(0.0f, display.y - std::min(max_height, height))));
    };
    const int frame = ImGui::GetFrameCount();
    const bool held = ImGui::IsMouseDown(ImGuiMouseButton_Left);
    if (placement.last_frame != frame - 1) placement.dragging = false;
    placement.last_frame = frame;
    const bool moved = placement.dragging && (held || ImGui::IsMouseReleased(ImGuiMouseButton_Left)) &&
                       ImGui::IsMousePosValid();
    if (!placement.placed)
    {
        const ImVec2 anchor(g_panel_side != 0 ? display.x - width - em : em, em);
        ImGui::SetNextWindowPos(clamp_to_viewport(anchor, std::max(placement.height, em * 3.0f)), ImGuiCond_Always);
    }
    else
    {
        ImVec2 position = placement.position;
        if (moved)
        {
            const ImVec2 delta = ImGui::GetMouseDragDelta(ImGuiMouseButton_Left);
            position = ImVec2(placement.drag_origin.x + delta.x, placement.drag_origin.y + delta.y);
        }
        if (!held) position = clamp_to_viewport(position, std::max(placement.height, em * 3.0f));
        const bool clamped = position.x != placement.position.x || position.y != placement.position.y;
        ImGui::SetNextWindowPos(position, moved || clamped ? ImGuiCond_Always : ImGuiCond_Appearing);
    }
    if (!held) placement.dragging = false;
    ImGui::SetNextWindowSize(ImVec2(width, 0.0f), ImGuiCond_Always);
    ImGui::SetNextWindowSizeConstraints(ImVec2(width, 0.0f), ImVec2(width, max_height));
    PushStyle(0.96f);
    // Keep window movement on our dedicated header item only.
    ImGuiWindowFlags flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
        ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoNav;
#ifdef IMGUI_HAS_DOCK
    flags |= ImGuiWindowFlags_NoDocking;
#endif
    bool open_reshade = false;
    if (ImGui::Begin("##dlss5_feed_panel", nullptr, flags))
    {
        placement.position = ImGui::GetWindowPos();
        placement.height = ImGui::GetWindowHeight();
        placement.placed = true;
        open_reshade = FeedPanelDrawHeader(rt != nullptr);
        FeedPanelBlockGap();
        const bool running = g_br_backend != BR_BACKEND_NONE && (g_panel_env.running == nullptr || g_panel_env.running());
        const bool starting = g_panel_env.starting != nullptr && g_panel_env.starting();
        const bool mixed = !BridgeLive() && BridgeAnyLive();
        FeedPanelDrawStatus(running, starting, mixed);
        FeedPanelBlockGap();
        const int previous_tab = g_br_tab;
        if (g_br_backend != BR_BACKEND_NONE) FeedPanelDrawTabs();
        if (g_br_tab != previous_tab) ImGui::SetScrollY(0.0f);
        FeedPanelDrawMotionNotice();
        if (g_br_backend != BR_BACKEND_NONE) FeedPanelDrawGroups(mixed);
        ImGui::Separator();
        FeedPanelDrawFooter(running, starting);
    }
    ImGui::End();
    PopStyle();
    ImGui::PopFont();
    if (open_reshade && rt->open_overlay(true, reshade::api::input_source::mouse))
        FeedPanelShow(false);
}
