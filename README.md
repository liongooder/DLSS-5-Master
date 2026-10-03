# DLSS 5 Master

A Windows app that brings DLSS 5 neural rendering setups for your games together in one place:

- **Game library** – finds Steam, Epic, GOG, Ubisoft, Xbox and custom-folder games, with posters, and detects each game's executable, DirectX/Vulkan version, 32/64-bit, and DLSS / FSR / XeSS / Streamline DLLs.
- **ReShade + RenoDX routes** – *Native* (RenoDX DLSS5 add-on), *Multipass* (RenoDX DLSS Tool) and *DLSS5 Feeder* (for games without DLSS).
- **OptiScaler** – DLSS-NR builds or an official OptiScaler folder, a choice of proxy DLL name (`dxgi`, `winmm`, `version`, …), and the in-game menu key.
- **MFG unlock (RTX 40)** – ships [MFGAdaUnlock-RenoDx](https://github.com/mavismmg/MFGAdaUnlock-RenoDx) for DLSS multi frame generation (3x/4x) on GeForce RTX 40 cards, as a tick-box add-on on every ReShade route (Native, Multipass, Feeder).
- **DLL swapping** – swap any upscaler DLL for another version from your DLL library.
- **Exact restore** – every file added or replaced is recorded in `_DLSS5Master_Backup` inside the game folder; *Restore originals* puts the game back byte-for-byte.

> **Anti-cheat warning:** injecting DLLs into online games can get your account banned. DLSS 5 Master asks you to acknowledge this for games that ship anti-cheat.

## Requirements

- Windows 10 1809 or newer, x64
- An NVIDIA RTX GPU for the DLSS routes. The NVIDIA runtimes, ReShade, RenoDX and DLSS5-Feeder files are included in the installer.

## Download

Get the installer from the [Releases](../../releases) page.

## Build from source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# app only
dotnet build src/DLSS5Master -c Release

# self-contained app + installer -> dist/DLSS5Master-Setup-<version>.exe
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

`tests/CoreTests` runs the core unit tests (`dotnet run --project tests/CoreTests`). `tests/CoreProbe` is a read-only console tool that scans your game library and prints what the app would detect, without changing anything.

## Third-party components

The installer bundles, unmodified and checked against the pinned SHA-256 list in `installer/payload-manifest.csv`: the NVIDIA DLSS / Frame Generation / Ray Reconstruction / neural rendering runtimes and Streamline (© NVIDIA, NVIDIA licence), ReShade, the RenoDX add-ons, DLSS5-Feeder, vort_Shaders and MFGAdaUnlock. These binaries are not stored in this repository; `build-installer.ps1` copies them from `-PayloadSource`. OptiScaler builds are downloaded from their release pages when first used.

| Component | Source | Licence |
|---|---|---|
| OptiScaler NR pre-SR multipass 0.8.3 (incl. RTX 40 MFG edition) | [wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass](https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass) | GPL-3.0 |
| OptiScaler DLSS-NR | [Dagherbou/OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR) | GPL-3.0 |
| OptiScaler DLSS-NR pre-SR multipass | [jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass](https://github.com/jlrouzies-fr/OptiScaler-DLSSNR-PreSR-Multipass) | GPL-3.0 |
| DLSS5-Feeder | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) | MIT |
| RenoDX DLSS5 add-on / DLSS Tool | [clshortfuse/renodx](https://github.com/clshortfuse/renodx), builds via [RankFTW/rhi-repo](https://github.com/RankFTW/rhi-repo) | MIT |
| NVIDIA DLSS / Frame Gen / Ray Reconstruction / neural rendering runtimes, Streamline | NVIDIA | NVIDIA licence (included) |
| MFGAdaUnlock-RenoDx (RTX 40 multi frame generation) | [mavismmg/MFGAdaUnlock-RenoDx](https://github.com/mavismmg/MFGAdaUnlock-RenoDx) | MIT |
| vort_Shaders | [vortigern11/vort_Shaders](https://github.com/vortigern11/vort_Shaders) | MIT |
| ReShade | [reshade.me](https://reshade.me) | BSD-3-Clause |

The installer includes the Microsoft .NET runtime and Windows App SDK, redistributed under their licences.

## Code signing policy

See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Privacy

See the privacy section of [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md#privacy).

## Feedback

Report bugs and suggest features in [Issues](../../issues). In the app, **About → Report a problem** opens a new issue with your app version, Windows version and graphics card already filled in.

## Support

DLSS 5 Master is free. If it saved you time, you can support its development:

[![Buy me a coffee](https://img.buymeacoffee.com/button-api/?text=Buy%20me%20a%20coffee&emoji=&slug=Liongooder&button_colour=FFDD00&font_colour=000000&font_family=Cookie&outline_colour=000000&coffee_colour=ffffff)](https://www.buymeacoffee.com/Liongooder)

## Licence

[MIT](LICENSE)
