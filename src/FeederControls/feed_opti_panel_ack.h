#pragma once

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <string_view>

// The live-settings fork logs values from its running Config after it reads
// [DlssNr]. Retain the file identity and end position from before each write
// so an old session's log or a previous change cannot acknowledge this edit.
struct FeedOptiAck
{
    std::filesystem::path file;
    DWORD volume = 0, indexHigh = 0, indexLow = 0;
    uint64_t offset = 0;
    std::string partial;
    bool loaded = false;
    bool modelMatches = false;
    unsigned int frameWidth = 0, frameHeight = 0;
};

static std::filesystem::path FeedOptiLogPath(const char *ini)
{
    char name[MAX_PATH]{};
    GetPrivateProfileStringA("Log", "LogFileName", "auto", name, MAX_PATH, ini);
    std::filesystem::path configured = _stricmp(name, "auto") == 0 || name[0] == '\0'
        ? std::filesystem::path("OptiScaler.log") : std::filesystem::path(name);
    auto folder = configured.has_root_path() ? configured.parent_path() : std::filesystem::path(ini).parent_path();
    const auto stem = configured.stem().wstring();
    if (GetPrivateProfileIntA("Log", "SingleFile", 0, ini) != 0)
        return folder / (stem + L".log");
    char single[8]{};
    GetPrivateProfileStringA("Log", "SingleFile", "auto", single, sizeof(single), ini);
    if (_stricmp(single, "true") == 0 || _stricmp(single, "auto") == 0)
        return folder / (stem + L".log");

    // A timestamped file is weak evidence if several game/helper instances
    // share this folder. Only bind it when the writer is unambiguous.
    std::error_code ec;
    std::filesystem::path match;
    for (std::filesystem::directory_iterator it(folder, ec), end; !ec && it != end; it.increment(ec))
    {
        const auto path = it->path();
        if (path.extension() != L".log" || !path.stem().wstring().starts_with(stem + L"_")) continue;
        if (!match.empty()) return {};
        match = path;
    }
    return ec ? std::filesystem::path{} : match;
}

static FeedOptiAck FeedOptiBeginAck(const char *ini)
{
    auto path = FeedOptiLogPath(ini);
    if (path.empty()) return {};
    const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return {};
    BY_HANDLE_FILE_INFORMATION info{};
    const bool known = GetFileInformationByHandle(file, &info) != 0;
    CloseHandle(file);
    if (!known) return {};
    return {std::move(path), info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow,
            (static_cast<uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow};
}

static bool FeedPanelMatches(float actual, float expected)
{
    return std::isfinite(actual) && std::fabs(actual - expected) <= 0.00002f * std::max(1.0f, std::fabs(expected));
}

static bool FeedOptiMatchAssignment(std::string_view fields, std::string_view key, float expected, bool *found = nullptr)
{
    if (found) *found = false;
    while (!fields.empty())
    {
        const auto split = fields.find(',');
        auto pair = fields.substr(0, split);
        while (!pair.empty() && (pair.front() == ' ' || pair.front() == '\t')) pair.remove_prefix(1);
        const auto equals = pair.find('=');
        if (equals == key.size() && pair.substr(0, equals) == key)
        {
            if (found) *found = true;
            auto value = pair.substr(equals + 1);
            while (!value.empty() && (value.back() == ' ' || value.back() == '\r')) value.remove_suffix(1);
            if (value == "true") return expected == 1.0f;
            if (value == "false") return expected == 0.0f;
            const std::string number(value);
            char *end = nullptr;
            const float actual = std::strtof(number.c_str(), &end);
            return end != number.c_str() && *end == '\0' && FeedPanelMatches(actual, expected);
        }
        if (split == std::string_view::npos) break;
        fields.remove_prefix(split + 1);
    }
    return false;
}

static const char *FeedOptiCreationKey(std::string_view key)
{
    if (key == "Preset") return "preset";
    if (key == "Style") return "style";
    if (key == "Intensity") return "intensity";
    if (key == "LocalStructure") return "structure";
    if (key == "LocalTone") return "tone";
    if (key == "SkinStructure") return "skin";
    if (key == "AutoMask") return "autoMask";
    return nullptr;
}

static bool FeedOptiNeedsBuild(std::string_view key)
{
    return FeedOptiCreationKey(key) != nullptr || key == "Passes" || key == "WorkingScale" || key == "UnlockPasses";
}

static bool FeedOptiIsLive(std::string_view key)
{
    constexpr std::string_view keys[] = {
        "Enabled", "Preset", "Style", "Intensity", "LocalStructure", "LocalTone", "SkinStructure", "AutoMask",
        "UnlockPasses", "Passes", "WorkingScale", "TransferStrength", "ColourStrength", "MaxRatio", "WhitePointScale",
        "WhitePointSource", "WhitePointTrim", "AutoExposureTrim", "AutoExposureHighlightProtection", "SkinProtection",
        "SkinDetail", "SkinColour", "EnvironmentDetail", "EnvironmentColour"};
    for (const auto live : keys) if (key == live) return true;
    return false;
}

static bool FeedOptiUsesOrdinaryPostSr(const char *ini)
{
    constexpr const char *keys[] = {
        "RunBeforeSR", "BeforeUpscale", "DeferredDLSS", "FinishedPicture", "ResidualAcrossRR", "SpatialCompression"
    };
    for (const char *key : keys)
    {
        char value[8]{};
        GetPrivateProfileStringA("DlssNr", key, "auto", value, sizeof(value), ini);
        if (_stricmp(value, "auto") != 0 && _stricmp(value, "false") != 0 && strcmp(value, "0") != 0)
            return false;
    }
    return true;
}

struct FeedOptiComposition
{
    unsigned int width = 0, height = 0, passes = 0;
};

static FeedOptiComposition FeedOptiReadComposition(std::string_view line)
{
    constexpr std::string_view composition = "DLSS-NR composition: ";
    const auto report = line.find(composition);
    if (report == std::string_view::npos) return {};
    constexpr std::string_view marker = ", model ";
    const auto modelAt = line.find(marker, report + composition.size());
    if (modelAt == std::string_view::npos) return {};

    auto fields = line.substr(modelAt + marker.size());
    const auto read = [&fields](unsigned int &value, std::string_view delimiter) {
        if (fields.empty()) return false;
        const auto result = std::from_chars(fields.data(), fields.data() + fields.size(), value);
        if (result.ec != std::errc{} || value == 0) return false;
        fields.remove_prefix(result.ptr - fields.data());
        if (!fields.starts_with(delimiter)) return false;
        fields.remove_prefix(delimiter.size());
        return true;
    };
    FeedOptiComposition model;
    if (!read(model.width, "x") || !read(model.height, ", passes ") || !read(model.passes, ", debug view ")) return {};
    return model;
}

static FeedPanelSettingResult FeedOptiObserveLine(FeedOptiAck &ack, std::string_view key, float expected,
                                                  std::string_view line)
{
    constexpr std::string_view loaded = "DLSS-NR live settings: read from OptiScaler.ini while running: ";
    constexpr std::string_view creation = "NR diagnostic creation: ";
    constexpr std::string_view built = "DLSS-NR: feature created at ";
    if (const auto at = line.find(loaded); at != std::string_view::npos)
    {
        bool found = false;
        const bool matches = FeedOptiMatchAssignment(line.substr(at + loaded.size()), key, expected, &found);
        if (found)
        {
            ack.loaded = matches;
            ack.modelMatches = false;
            if (matches && !FeedOptiNeedsBuild(key)) return FeedPanelSettingResult::applied;
        }
        return FeedPanelSettingResult::pending;
    }
    if (!ack.loaded) return FeedPanelSettingResult::pending;

    // Feeder's Vulkan transport uses OptiScaler's D3D12 NR path. On that path
    // the composition report follows an actual successful evaluation and
    // includes the actual model dimensions and the pass count after fallback.
    if (key == "Passes" || key == "WorkingScale")
    {
        const auto model = FeedOptiReadComposition(line);
        if (model.passes == 0) return FeedPanelSettingResult::pending;
        if (key == "Passes" && static_cast<float>(model.passes) == expected)
            return FeedPanelSettingResult::applied;
        if (key == "WorkingScale" && ack.frameWidth != 0 && ack.frameHeight != 0 && expected >= 0.25f && expected <= 2.0f &&
            model.width == static_cast<unsigned int>(ack.frameWidth * expected + 0.5f) &&
            model.height == static_cast<unsigned int>(ack.frameHeight * expected + 0.5f))
            return FeedPanelSettingResult::applied;
        return FeedPanelSettingResult::pending;
    }

    // Model parameters are reported at creation. UnlockPasses has no exact
    // confirmation here unless a separate edit requests a new pass count.
    if (const auto at = line.find(creation); at != std::string_view::npos)
    {
        const auto *modelKey = FeedOptiCreationKey(key);
        ack.modelMatches = modelKey != nullptr && FeedOptiMatchAssignment(line.substr(at + creation.size()), modelKey, expected);
    }
    else if (ack.modelMatches && line.find(built) != std::string_view::npos)
        return FeedPanelSettingResult::applied;
    else if (line.find("DLSS-NR: CreateFeature(18) failed") != std::string_view::npos)
        ack.modelMatches = false;
    return FeedPanelSettingResult::pending;
}

static FeedPanelSettingResult FeedOptiPoll(FeedOptiAck &ack, std::string_view key, float expected)
{
    if (ack.file.empty()) return FeedPanelSettingResult::unverified;
    const HANDLE file = CreateFileW(ack.file.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return FeedPanelSettingResult::pending;
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file, &info) || info.dwVolumeSerialNumber != ack.volume ||
        info.nFileIndexHigh != ack.indexHigh || info.nFileIndexLow != ack.indexLow)
    {
        CloseHandle(file);
        return FeedPanelSettingResult::unverified;
    }
    const uint64_t size = (static_cast<uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
    if (size < ack.offset)
    {
        CloseHandle(file);
        return FeedPanelSettingResult::unverified;
    }
    LARGE_INTEGER offset{};
    offset.QuadPart = static_cast<LONGLONG>(ack.offset);
    char bytes[16384];
    DWORD count = 0;
    const DWORD want = static_cast<DWORD>(std::min<uint64_t>(size - ack.offset, sizeof(bytes)));
    const bool read = want != 0 && SetFilePointerEx(file, offset, nullptr, FILE_BEGIN) &&
        ReadFile(file, bytes, want, &count, nullptr);
    CloseHandle(file);
    if (!read || count == 0) return FeedPanelSettingResult::pending;
    ack.offset += count;
    ack.partial.append(bytes, count);
    size_t lineAt = 0;
    for (size_t eol; (eol = ack.partial.find('\n', lineAt)) != std::string::npos; lineAt = eol + 1)
    {
        const std::string_view line(ack.partial.data() + lineAt, eol - lineAt);
        const auto result = FeedOptiObserveLine(ack, key, expected, line);
        if (result != FeedPanelSettingResult::pending) return result;
    }
    ack.partial.erase(0, lineAt);
    if (ack.partial.size() > 8192) ack.partial.clear();
    return FeedPanelSettingResult::pending;
}
