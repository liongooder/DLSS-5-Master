# Builds DLSS 5 Master in Release and packages dist\DLSS5Master-Setup-<version>.exe with Inno Setup 6.
#   -Stage all        publish + installer (default, local builds)
#   -Stage publish    only the self-contained app in .\publish (CI signs these files next)
#   -Stage installer  only the installer, from whatever is in .\publish
#   -PayloadSource    folder holding the bundled runtime files (NVIDIA runtimes, ReShade add-on build, RenoDX
#                     add-ons, DLSS5-Feeder); every file is checked against installer\payload-manifest.csv
param(
    [ValidateSet('all', 'publish', 'installer')] [string]$Stage = 'all',
    [string]$PayloadSource = (Join-Path $PSScriptRoot 'vendor\payload')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DLSS5Master\DLSS5Master.csproj'
$publish = Join-Path $root 'publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "DLSS 5 Master $version ($Stage)" -ForegroundColor Green

if ($Stage -in 'all', 'publish') {
    # Bundled third-party add-on: MFGAdaUnlock-RenoDx (MIT). Pinned URL + SHA-256, same pin as Core/Components.cs.
    $addons = Join-Path $root 'src\DLSS5Master\addons'
    $mfg = Join-Path $addons 'renodx-mfgunlock.addon64'
    $mfgSha = '080bcca4c5b6cd3531466458559a598996a271cd8592b3289186810f613e7eee'
    if (-not (Test-Path $mfg) -or (Get-FileHash $mfg -Algorithm SHA256).Hash.ToLower() -ne $mfgSha) {
        New-Item -ItemType Directory -Force $addons | Out-Null
        Invoke-WebRequest 'https://github.com/mavismmg/MFGAdaUnlock-RenoDx/releases/download/1.4.1/renodx-mfgunlock.addon64' -OutFile $mfg -UseBasicParsing
        if ((Get-FileHash $mfg -Algorithm SHA256).Hash.ToLower() -ne $mfgSha) { Remove-Item $mfg; throw 'renodx-mfgunlock.addon64 failed its checksum' }
    }

    # Bundled runtime payload: copied file by file and verified against the pinned manifest.
    $payload = Join-Path $root 'src\DLSS5Master\payload'
    foreach ($row in Import-Csv (Join-Path $root 'installer\payload-manifest.csv')) {
        $dest = Join-Path $payload $row.dest
        if ((Test-Path $dest) -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $row.sha256) { continue }
        if ($row.source -like 'https://*' -and (Split-Path $row.source -Leaf) -eq (Split-Path $row.dest -Leaf)) {
            # The release file itself is bundled (e.g. the OptiScaler zip).
            $src = Join-Path ([IO.Path]::GetTempPath()) (Split-Path $row.source -Leaf)
            Invoke-WebRequest $row.source -OutFile $src -UseBasicParsing
        } elseif ($row.source -like 'https://*') {
            # Newer add-on than the payload source has: take the file of the same name out of its release zip.
            $zip = Join-Path ([IO.Path]::GetTempPath()) (Split-Path $row.source -Leaf)
            Invoke-WebRequest $row.source -OutFile $zip -UseBasicParsing
            $src = Join-Path ([IO.Path]::GetTempPath()) ('dlss5master-' + [IO.Path]::GetFileNameWithoutExtension($zip))
            Expand-Archive $zip $src -Force
            $src = Get-ChildItem $src -Recurse -Filter (Split-Path $row.dest -Leaf) | Select-Object -First 1 -ExpandProperty FullName
            if (-not $src) { throw "$(Split-Path $row.dest -Leaf) not found in $($row.source)" }
        } else {
            $src = Join-Path $PayloadSource $row.source
        }
        if (-not (Test-Path $src)) { throw "Payload file missing: $src (set -PayloadSource)" }
        if ((Get-FileHash $src -Algorithm SHA256).Hash.ToLower() -ne $row.sha256) { throw "Payload file does not match its pinned checksum: $src" }
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $src $dest -Force
    }

    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    if (-not (Test-Path (Join-Path $publish 'DLSS5Master.pri'))) { throw 'publish is missing DLSS5Master.pri (the app would crash on start)' }
    if (-not (Test-Path (Join-Path $publish 'addons\renodx-mfgunlock.addon64'))) { throw 'publish is missing the bundled MFG unlock add-on' }
    if (-not (Test-Path (Join-Path $publish 'payload\runtime\nvngx_dlssnr.dll'))) { throw 'publish is missing the bundled runtime payload' }
}

if ($Stage -in 'all', 'installer') {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
    & $iscc "/DAppVersion=$version" (Join-Path $root 'installer\DLSS5Master.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    Get-Item (Join-Path $root "dist\DLSS5Master-Setup-$version.exe") | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}}
}
