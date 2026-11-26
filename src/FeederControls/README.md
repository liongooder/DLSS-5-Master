# Feeder Controls

This ReShade add-on supplies the End panel used with the bundled original Feeder 2.0.0. It controls RenoDX Generic in the current game session. When neural rendering is off, it pauses ReShade effects if only the Feeder techniques are active and restores their previous enabled state when neural rendering resumes.

Build from an x64 Visual Studio Native Tools Command Prompt with the Windows SDK. Pass the absolute include directories for ReShade API 20, Dear ImGui 1.92.5, and the locally supplied shared panel stylesheet and widgets header, `neuralmaster_panel_ui.hpp`.

```bat
build.bat "C:\sdk\reshade\include" "C:\sdk\imgui" "C:\local\panel-ui"
```

The release installer uses the companion hash pinned in `installer/payload-manifest.csv`. The source includes the Feeder MIT notice. The ReShade and Dear ImGui notices are in `installer/licenses`.
