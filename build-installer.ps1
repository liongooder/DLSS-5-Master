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

# DLSS5 NR (version 11 and later) is a paid add-on that players bring themselves (your-addons\dlss5-nr). None of
# its files may ever be packaged, whatever the payload manifest says: the csproj ships all of payload\ and addons\, and the
# installer all of publish\. Names are matched loosely (its release zip is "DLSS5 NR Add-on v<version>.zip"), zips are
# looked into, and payload\ may hold only what the manifest pins, so a renamed copy cannot slip in either.
$paidAddon = '(?i)dlss5(?:[\s_-]|%20)*nr|nr-host'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-NoPaidAddon([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    $items = @(Get-ChildItem -LiteralPath $dir -Recurse -Force)
    $found = @($items | Where-Object { $_.Name -match $paidAddon } | ForEach-Object { $_.FullName })
    if ($found.Count -eq 0) {
        foreach ($zip in @($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -eq '.zip' })) {
            try { $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName) }
            catch { throw "$($zip.FullName) cannot be opened as a zip, so it cannot be checked for DLSS5 NR: $($_.Exception.Message)" }
            try { $found += @($archive.Entries | Where-Object { $_.FullName -match $paidAddon } | ForEach-Object { "$($zip.FullName) > $($_.FullName)" }) }
            finally { $archive.Dispose() }
        }
    }
    if ($found.Count -gt 0) {
        throw "DLSS5 NR must never be packaged. Remove: $(($found | Select-Object -First 5) -join ', ')"
    }
}

# payload\ is gitignored, so a stray file there would ship unseen: only what installer\payload-manifest.csv pins may be in
# it (plus $also), the retired NeuralMaster folder aside (the csproj leaves it out).
function Assert-OnlyPinned([string]$dir, [string[]]$also) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    $pinned = @{}
    foreach ($row in Import-Csv (Join-Path $root 'installer\payload-manifest.csv')) { $pinned[$row.dest.ToLowerInvariant()] = $true }
    foreach ($file in $also) { $pinned[$file.ToLowerInvariant()] = $true }
    $base = (Resolve-Path -LiteralPath $dir).ProviderPath.TrimEnd('\') + '\'
    $extra = @(Get-ChildItem -LiteralPath $dir -Recurse -Force -File | ForEach-Object { $_.FullName.Substring($base.Length) } |
        Where-Object { -not $pinned.ContainsKey($_.ToLowerInvariant()) -and $_ -notlike 'neuralmaster\*' })
    if ($extra.Count -gt 0) {
        throw "$dir holds files that installer\payload-manifest.csv does not pin, and they would be packaged: $(($extra | Select-Object -First 5) -join ', ')"
    }
}

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "DLSS 5 Master $version ($Stage)" -ForegroundColor Green

if ($Stage -in 'all', 'publish') {
    # Bundled third-party add-on: MFGAdaUnlock-RenoDx (MIT). Pinned URL + SHA-256, same pin as Core/Components.cs.
    $addons = Join-Path $root 'src\DLSS5Master\addons'
    $mfg = Join-Path $addons 'renodx-mfgunlock.addon64'
    $mfgSha = 'f7cc86a84c05436409a1b7a59499ffb9d8d5cec51ed6fe0e8bb4e55885e62ebf'
    if (-not (Test-Path $mfg) -or (Get-FileHash $mfg -Algorithm SHA256).Hash.ToLower() -ne $mfgSha) {
        New-Item -ItemType Directory -Force $addons | Out-Null
        Invoke-WebRequest 'https://github.com/mavismmg/MFGAdaUnlock-RenoDx/releases/download/1.12.0/renodx-mfgunlock.addon64' -OutFile $mfg -UseBasicParsing
        if ((Get-FileHash $mfg -Algorithm SHA256).Hash.ToLower() -ne $mfgSha) { Remove-Item $mfg; throw 'renodx-mfgunlock.addon64 failed its checksum' }
    }

    # The overlay helper (C++, src\Overlay): built here and staged with its Visual C++ runtime into the app's overlay\ folder.
    # Its exit code decides, not stray stderr text (Windows PowerShell turns any into an error under 'Stop').
    $ErrorActionPreference = 'Continue'
    & cmd /c "`"$(Join-Path $root 'src\Overlay\build.bat')`" 2>&1"
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw 'dlss5-overlay build failed' }
    $overlay = Join-Path $root 'src\DLSS5Master\overlay'
    New-Item -ItemType Directory -Force $overlay | Out-Null
    foreach ($f in 'dlss5-overlay.exe', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
        Copy-Item (Join-Path $root "src\Overlay\build\$f") $overlay -Force
    }

    # Bundled runtime payload: copied file by file and verified against the pinned manifest.
    $payload = Join-Path $root 'src\DLSS5Master\payload'
    foreach ($row in Import-Csv (Join-Path $root 'installer\payload-manifest.csv')) {
        if ($row.dest -like 'neuralmaster\*') { throw 'The retired NeuralMaster finished-picture payload must not be packaged.' }
        if ($row.dest -match $paidAddon -or $row.source -match $paidAddon) { throw "DLSS5 NR must never be packaged, but payload-manifest.csv lists $($row.dest)." }
        $dest = Join-Path $payload $row.dest
        if ((Test-Path $dest) -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $row.sha256) { continue }
        if ($row.source -like 'https://*' -and $row.dest -like '*.zip') {
            # The release file itself is bundled (e.g. the OptiScaler zip), possibly under another name.
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
    Assert-OnlyPinned $payload @()
    Assert-NoPaidAddon $payload
    Assert-NoPaidAddon $addons

    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    if (-not (Test-Path (Join-Path $publish 'DLSS5Master.pri'))) { throw 'publish is missing DLSS5Master.pri (the app would crash on start)' }
    if (-not (Test-Path (Join-Path $publish 'addons\renodx-mfgunlock.addon64'))) { throw 'publish is missing the bundled MFG unlock add-on' }
    if (-not (Test-Path (Join-Path $publish 'overlay\dlss5-overlay.exe'))) { throw 'publish is missing the overlay helper' }
    if (-not (Test-Path (Join-Path $publish 'payload\runtime\nvngx_dlssnr.dll'))) { throw 'publish is missing the bundled runtime payload' }
    Assert-OnlyPinned (Join-Path $publish 'payload') @('feeder\licenses\THIRD-PARTY-SOURCES.txt')
    Assert-NoPaidAddon $publish
}

if ($Stage -in 'all', 'installer') {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
    Assert-OnlyPinned (Join-Path $publish 'payload') @('feeder\licenses\THIRD-PARTY-SOURCES.txt')
    Assert-NoPaidAddon $publish
    & $iscc "/DAppVersion=$version" (Join-Path $root 'installer\DLSS5Master.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    Get-Item (Join-Path $root "dist\DLSS5Master-Setup-$version.exe") | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}}
}
