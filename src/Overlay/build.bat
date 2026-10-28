@echo off
rem Builds dlss5-overlay.exe, the helper behind DLSS 5 Master's overlay, into build\.
rem The SDK files it compiles against are not kept in this repository; fetch-sdk.ps1 downloads them
rem (checked against pinned SHA-256 values) into external\:
rem   external\ngx       NVIDIA NGX headers and nvsdk_ngx_d.lib  (https://github.com/NVIDIA/DLSS)
rem   external\nvof      nvOpticalFlowCommon.h, nvOpticalFlowD3D11.h  (NVIDIA Optical Flow SDK, BSD)
rem   external\nvapi     nvapi.h, NvApiDriverSettings.h and nvapi64.lib  (https://github.com/NVIDIA/nvapi, MIT)
cd /d "%~dp0"
set FETCH=
if not exist external\ngx\nvsdk_ngx_helpers_dlssg_d3d.h set FETCH=1
if not exist external\nvapi\nvapi64.lib set FETCH=1
if defined FETCH powershell -NoProfile -ExecutionPolicy Bypass -File fetch-sdk.ps1 || exit /b 1
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 || exit /b 1
if not exist build mkdir build
cl /nologo /EHsc /O2 /MD /W4 /std:c++20 /permissive- /DUNICODE /D_UNICODE /Iexternal\ngx /Iexternal\nvof /Iexternal\nvapi /Fobuild\ /Fdbuild\ main.cpp ^
   /link /OUT:build\dlss5-overlay.exe /SUBSYSTEM:WINDOWS /ENTRY:wmainCRTStartup external\ngx\lib\nvsdk_ngx_d.lib external\nvapi\nvapi64.lib ^
   d3d12.lib d3d11.lib dxgi.lib dcomp.lib dwmapi.lib d3dcompiler.lib windowsapp.lib user32.lib || exit /b 1
rem The Visual C++ runtime goes beside the exe: players do not need the redistributable installed.
for /d %%V in ("%VCToolsRedistDir%x64\Microsoft.VC*.CRT") do copy /y "%%V\msvcp140.dll" build\ >nul & copy /y "%%V\vcruntime140.dll" build\ >nul & copy /y "%%V\vcruntime140_1.dll" build\ >nul
echo dlss5-overlay built.
