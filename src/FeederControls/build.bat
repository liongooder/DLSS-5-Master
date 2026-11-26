@echo off
setlocal
cd /d "%~dp0"
if "%~3"=="" (
    echo Usage: build.bat "ReShade include directory" "ImGui include directory" "shared UI header directory"
    exit /b 1
)
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x64" (
    echo Run this script from an x64 Visual Studio Native Tools Command Prompt.
    exit /b 1
)
if not exist bin mkdir bin
cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /I"%~f1" /I"%~f2" /I"%~f3" /Fobin\ /Fdbin\ feeder-detroit-panel.cpp ^
    /link /OUT:bin\dlss5-feed-controls.addon64 /IMPLIB:bin\dlss5-feed-controls.lib bcrypt.lib user32.lib gdi32.lib
exit /b %errorlevel%
