#pragma once

#include <bcrypt.h>
#include <array>
#include <cmath>
#include <string_view>

// RenoDX Generic 10.8.0's own UI uses these operations to update both the
// renderer's state and ReShade's saved settings. The public V1 API does not
// expose this operation. Resolve it only for the exact supported add-on.
namespace reno108
{
struct ResolutionSettings
{
    uint32_t hook_point;
    bool follow_input;
    float scale;
};
static_assert(offsetof(ResolutionSettings, follow_input) == 4);
static_assert(offsetof(ResolutionSettings, scale) == 8);
static constexpr uintptr_t kApplyResolution = 0x0FAAF0;
static constexpr uintptr_t kHookPoint = 0x791D68;
static constexpr uintptr_t kApplyEnabled = 0x27BFD0;
static constexpr uintptr_t kCaptureActive = 0x799423;
static constexpr uintptr_t kAssignSetting = 0x28C2B0;
static constexpr uintptr_t kSaveSetting = 0x1F4D40;
static constexpr uintptr_t kRecordChange = 0x154120;
static constexpr uintptr_t kSettingsChanged = 0x79A7FB;
static constexpr uintptr_t kModelGeneration = 0x763F40;
static constexpr uintptr_t kHistoryGeneration = 0x791DB8;
static constexpr uintptr_t kFollowInput = 0x791EF8;
static constexpr uintptr_t kScale = 0x76405C;
static constexpr uintptr_t kControlExport = 0x2E1520;
static constexpr DWORD kFileSize = 7991808;
static constexpr std::array<UCHAR, 32> kFileSha256 = {
    0x79, 0x87, 0x18, 0x79, 0x32, 0x40, 0x53, 0xd4, 0x99, 0x31, 0xa9, 0xf8, 0xc1, 0x86, 0x5f, 0xcb,
    0x83, 0xe8, 0x14, 0x6d, 0xf4, 0xa5, 0x6b, 0x14, 0x6d, 0x7a, 0x8a, 0x14, 0xcc, 0x7e, 0x70, 0xfe,
};

static bool MatchesBuild(HMODULE module)
{
    const auto base = reinterpret_cast<uintptr_t>(module);
    if (reinterpret_cast<uintptr_t>(GetProcAddress(module, RENO_API_EXPORT)) != base + kControlExport) return false;

    wchar_t path[MAX_PATH] {};
    const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size {};
    HANDLE mapping = GetFileSizeEx(file, &size) && size.QuadPart == kFileSize
        ? CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr) : nullptr;
    CloseHandle(file);
    if (mapping == nullptr) return false;

    auto *view = static_cast<UCHAR *>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, kFileSize));
    CloseHandle(mapping);
    if (view == nullptr) return false;

    std::array<UCHAR, 32> hash {};
    BCRYPT_ALG_HANDLE sha = nullptr;
    bool match = false;
    if (BCryptOpenAlgorithmProvider(&sha, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0)
    {
        match = BCryptHash(sha, nullptr, 0, view, kFileSize, hash.data(), static_cast<ULONG>(hash.size())) == 0 &&
            hash == kFileSha256;
        BCryptCloseAlgorithmProvider(sha, 0);
    }
    UnmapViewOfFile(view);
    return match;
}

class VerifiedModule
{
public:
    VerifiedModule()
    {
        static HMODULE checked = nullptr;
        static bool supported = false;
        if (!GetModuleHandleExW(0, L"renodx-dlss5.addon64", &module_))
        {
            checked = nullptr;
            return;
        }
        if (module_ != checked)
        {
            supported = MatchesBuild(module_);
            checked = module_;
        }
        if (!supported)
        {
            FreeLibrary(module_);
            module_ = nullptr;
        }
    }

    ~VerifiedModule() { if (module_ != nullptr) FreeLibrary(module_); }
    VerifiedModule(const VerifiedModule &) = delete;
    VerifiedModule &operator=(const VerifiedModule &) = delete;

    uintptr_t base() const { return reinterpret_cast<uintptr_t>(module_); }

private:
    HMODULE module_ = nullptr;
};

enum class Storage : uint8_t { real = 1, integer = 2, toggle = 3 };
enum class Commit : uint8_t { save, reset_history, rebuild };
enum class Setter : uint8_t { descriptor, enabled, resolution };

struct Setting
{
    std::string_view key;
    uintptr_t key_rva;
    uintptr_t descriptor;
    uintptr_t storage;
    Storage type;
    Commit commit;
    float min;
    float max;
    Setter setter = Setter::descriptor;
};

// Descriptor layout matches the stock slider, combo and checkbox controls.
// Its final byte tells the renderer whether this edit requires invalidation.
static constexpr std::array kSettings {
    Setting { "NeuralUplift",           0x6EAC20, 0x6ED148, 0x763EB9, Storage::toggle,  Commit::save,           0.0f,    1.0f, Setter::enabled },
    Setting { "NRPasses",              0x6D2CD0, 0x6EDBE8, 0x763FD4, Storage::integer, Commit::rebuild,        1.0f,    4.0f },
    Setting { "NRResolutionScale",     0x6EDBD0, 0x6EDBA8, kScale,   Storage::real,    Commit::save,           0.33f,   1.0f, Setter::resolution },
    Setting { "NRTransferStrength",   0x6EEB08, 0x6EEAE0, 0x764070, Storage::real,    Commit::save,           0.0f,    1.0f },
    Setting { "NRColorStrength",      0x6EEB48, 0x6EEB20, 0x764074, Storage::real,    Commit::save,           0.0f,    1.0f },
    Setting { "NRPaperWhiteScale",    0x6EEAC8, 0x6EEAA0, 0x764068, Storage::real,    Commit::reset_history,  0.1f,   10.0f },
    Setting { "NRDiffuseWhiteNits",   0x6E1508, 0x6EEA78, 0x76406C, Storage::real,    Commit::reset_history, 80.0f, 1000.0f },
    Setting { "NRChainedHistory",     0x6EEEB8, 0x6EEE90, 0x764058, Storage::toggle,  Commit::reset_history,  0.0f,    1.0f },
    Setting { "NRLookHaloBrightSide", 0x6D2DF8, 0x6EE7F8, 0x791E1C, Storage::real,    Commit::save,           0.0f,    1.0f },
    Setting { "NRDetailStability",    0x6EE938, 0x6EE910, 0x791E2C, Storage::integer, Commit::save,           0.0f,    2.0f },
    Setting { "NRStyleHighlightGuard",0x6EEBF0, 0x6EEBC8, 0x791F10, Storage::real,    Commit::save,           0.0f,    2.0f },
    Setting { "NRChromaClamp",        0x6EEB80, 0x6EEB58, 0x764078, Storage::real,    Commit::save,           0.25f,   2.0f },
};

static constexpr auto kLiveKeys = [] {
    std::array<std::string_view, kSettings.size()> keys {};
    for (size_t i = 0; i < kSettings.size(); ++i) keys[i] = kSettings[i].key;
    return keys;
}();

static const Setting *FindSetting(std::string_view key)
{
    for (const auto &setting : kSettings)
        if (setting.key == key) return &setting;
    return nullptr;
}

static bool IsStructuralKey(std::string_view key) { return FindSetting(key) != nullptr; }

static bool ReadValue(uintptr_t base, const Setting &setting, float &out)
{
    __try
    {
        const auto address = base + setting.storage;
        switch (setting.type)
        {
        case Storage::toggle:
            out = *reinterpret_cast<volatile const uint8_t *>(address) != 0 ? 1.0f : 0.0f;
            return true;
        case Storage::integer:
            out = static_cast<float>(*reinterpret_cast<volatile const uint32_t *>(address));
            return out >= setting.min && out <= setting.max;
        case Storage::real:
            out = *reinterpret_cast<volatile const float *>(address);
            return std::isfinite(out);
        }
        return false;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool ApplySetting(uintptr_t base, const Setting &setting, float value)
{
    constexpr unsigned char assign[] = {0x48, 0x89, 0x5c, 0x24, 0x20, 0xf3, 0x0f, 0x11};
    constexpr unsigned char save[] = {0x48, 0x81, 0xec, 0x98, 0x00, 0x00, 0x00};
    constexpr unsigned char changed[] = {0x40, 0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b};
    __try
    {
        const auto *descriptor = reinterpret_cast<const uintptr_t *>(base + setting.descriptor);
        const auto commit = *reinterpret_cast<const Commit *>(base + setting.descriptor + 0x24);
        if (descriptor[0] != base + setting.key_rva ||
            descriptor[static_cast<size_t>(setting.type)] != base + setting.storage || commit != setting.commit ||
            memcmp(reinterpret_cast<const void *>(base + kAssignSetting), assign, sizeof(assign)) != 0 ||
            memcmp(reinterpret_cast<const void *>(base + kSaveSetting), save, sizeof(save)) != 0 ||
            memcmp(reinterpret_cast<const void *>(base + kRecordChange), changed, sizeof(changed)) != 0)
            return false;
        reinterpret_cast<void (*)(const void *, float)>(base + kAssignSetting)(descriptor, value);
        reinterpret_cast<void (*)(const void *)>(base + kSaveSetting)(descriptor);
        reinterpret_cast<void (*)(const char *)>(base + kRecordChange)(reinterpret_cast<const char *>(descriptor[0]));
        if (commit == Commit::rebuild)
            InterlockedExchange8(reinterpret_cast<volatile char *>(base + kSettingsChanged), 1);
        else if (commit == Commit::reset_history)
        {
            InterlockedIncrement64(reinterpret_cast<volatile LONG64 *>(base + kModelGeneration));
            InterlockedIncrement64(reinterpret_cast<volatile LONG64 *>(base + kHistoryGeneration));
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static bool ApplyResolution(uintptr_t base, float scale)
{
    constexpr unsigned char prologue[] = {0x40, 0x53, 0x48, 0x83, 0xec, 0x20, 0x8b, 0x05};
    const auto *fn = reinterpret_cast<const void *>(base + kApplyResolution);
    __try
    {
        if (memcmp(fn, prologue, sizeof(prologue)) != 0) return false;
        const ResolutionSettings value { *reinterpret_cast<volatile const uint32_t *>(base + kHookPoint), false, scale };
        reinterpret_cast<void (*)(const ResolutionSettings *)>(base + kApplyResolution)(&value);
        return *reinterpret_cast<volatile const uint8_t *>(base + kFollowInput) == 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static FeedPanelSettingResult ApplyEnabled(uintptr_t base, bool enabled)
{
    constexpr unsigned char prologue[] = {0x48, 0x89, 0x5c, 0x24, 0x10, 0x48, 0x89, 0x6c};
    __try
    {
        if (memcmp(reinterpret_cast<const void *>(base + kApplyEnabled), prologue, sizeof(prologue)) != 0)
            return FeedPanelSettingResult::unverified;
        reinterpret_cast<void (*)(bool)>(base + kApplyEnabled)(enabled);
        // The stock setter can defer the change while its own capture is active.
        return *reinterpret_cast<volatile const uint8_t *>(base + kCaptureActive) != 0
            ? FeedPanelSettingResult::pending : FeedPanelSettingResult::applied;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return FeedPanelSettingResult::unverified; }
}

static bool Get(std::string_view key, float *value)
{
    const auto *setting = FindSetting(key);
    if (setting == nullptr || value == nullptr) return false;
    const VerifiedModule module;
    return module.base() != 0 && ReadValue(module.base(), *setting, *value);
}

static FeedPanelSettingResult Set(std::string_view key, float value)
{
    const auto *setting = FindSetting(key);
    if (setting == nullptr || !std::isfinite(value) || value < setting->min || value > setting->max ||
        (setting->type != Storage::real && std::trunc(value) != value))
        return FeedPanelSettingResult::rejected;
    const VerifiedModule module;
    const auto base = module.base();
    if (base == 0) return FeedPanelSettingResult::unverified;

    if (setting->setter == Setter::enabled)
    {
        const auto result = ApplyEnabled(base, value == 1.0f);
        if (result != FeedPanelSettingResult::applied) return result;
    }
    else if (!(setting->setter == Setter::resolution ? ApplyResolution(base, value) : ApplySetting(base, *setting, value)))
        return FeedPanelSettingResult::unverified;

    float accepted {};
    return ReadValue(base, *setting, accepted) && FeedPanelMatches(accepted, value)
        ? FeedPanelSettingResult::applied : FeedPanelSettingResult::unverified;
}
}
