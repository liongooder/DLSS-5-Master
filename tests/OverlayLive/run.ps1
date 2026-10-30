# Runs the overlay helper on a test window that sits beyond the right edge of the rightmost screen (one pixel column on
# screen, so Windows keeps drawing it), and records the helper's dedicated graphics memory once a second. Nothing takes
# the focus and nothing shows but that pixel column. Needs an NVIDIA RTX card, DLSS 5 Master's overlay set up once
# (its files in %LOCALAPPDATA%\DLSS5Master\overlay) and the test build of the helper: src\Overlay\build.bat tests
#   -Resizes  window size changes, every 500 ms from 14 s (each rebuilds DLSS for the new size)
#   -Extra    more helper arguments, e.g. "--fg 1" or "--test-remove-device 10"
#   -StopAt   seconds after the start to ask the helper to stop (0: it ends by itself)
#   -Repeat   run that many sessions and count the ones the helper survived
param([string]$Helper = (Join-Path $PSScriptRoot '..\..\src\Overlay\build-tests\dlss5-overlay.exe'),
      [int]$Resizes = 30, [string]$Extra = '', [int]$StopAt = 36, [int]$Life = 60, [int]$Repeat = 1)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP 'dlss5-overlay-live'
$installed = Join-Path $env:LOCALAPPDATA 'DLSS5Master\overlay'
New-Item -ItemType Directory -Force $work | Out-Null

# The test window, built once with the .NET Framework compiler every Windows has.
$target = Join-Path $work 'target.exe'
if (-not (Test-Path $target)) {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe "/out:$target" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'target.cs')
    if ($LASTEXITCODE -ne 0) { throw 'target.cs did not build' }
}
# The overlay's folder as the app prepares it: the helper, its runtime, ReShade, the add-on, the NVIDIA runtimes.
foreach ($f in 'dxgi.dll', 'renodx-dlss5.addon64', 'nvngx_dlss.dll', 'nvngx_dlssnr.dll', 'nvngx_dlssg.dll') {
    if (-not (Test-Path (Join-Path $installed $f))) { throw "$f is missing from $installed; start the overlay from the app once first" }
    Copy-Item (Join-Path $installed $f) $work -Force
}
foreach ($f in 'dlss5-overlay.exe', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') { Copy-Item (Join-Path (Split-Path $Helper) $f) $work -Force }
$ini = [IO.File]::ReadAllText((Join-Path $installed 'ReShade.ini')) -replace '(?m)^NRPasses=\d', 'NRPasses=1' -replace '(?ms)^\[ADDON\].*?(?=^\[|\z)', ''
[IO.File]::WriteAllText((Join-Path $work 'ReShade.ini'), $ini.TrimEnd() + "`r`n`r`n[ADDON]`r`nAddonPath=$work`r`n")

$survived = 0
for ($r = 1; $r -le $Repeat; $r++) {
    foreach ($f in 'dlss5-overlay.log', 'ReShade.log', 'stop', 'hwnd.txt', 'RenoDX-DLSS5-crash.log') { Remove-Item (Join-Path $work $f) -ErrorAction SilentlyContinue }
    $hwndFile = Join-Path $work 'hwnd.txt'
    $window = Start-Process $target -ArgumentList "`"$hwndFile`" 1280 720 14 $Resizes 500 $Life" -PassThru
    for ($i = 0; $i -lt 50 -and -not (Test-Path $hwndFile); $i++) { Start-Sleep -Milliseconds 100 }
    $stop = Join-Path $work 'stop'
    $p = Start-Process (Join-Path $work 'dlss5-overlay.exe') -ArgumentList "--hwnd $((Get-Content $hwndFile).Trim()) --stop-file `"$stop`" --scale 1 --test-any-focus $Extra" -WorkingDirectory $work -PassThru
    $t0 = Get-Date
    $memory = @()
    while (-not $p.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt [Math]::Max($StopAt, $Life) + 20) {
        $s = ((Get-Date) - $t0).TotalSeconds
        if ($StopAt -gt 0 -and $s -ge $StopAt -and -not (Test-Path $stop)) { New-Item $stop -ItemType File | Out-Null }
        try {
            $c = Get-Counter "\GPU Process Memory(pid_$($p.Id)_*)\Dedicated Usage" -ErrorAction Stop
            $memory += '{0:0}s {1:0} MB' -f $s, (($c.CounterSamples | Measure-Object CookedValue -Sum).Sum / 1MB)
        } catch { }
        Start-Sleep -Milliseconds 700
    }
    if (-not $p.WaitForExit(15000)) { $p.Kill(); 'the helper did not end: killed' }
    if (-not $window.HasExited) { $window.Kill() }
    $log = Get-Content (Join-Path $work 'dlss5-overlay.log')
    $ok = [bool]($log | Select-String '^\S+\s+closed$')
    if ($ok) { $survived++ }
    "=== session $r of $Repeat`: exit code $($p.ExitCode), $(($log | Select-String 'DLSS feature').Count) DLSS builds, $(if ($ok) { 'closed normally' } else { 'DID NOT CLOSE NORMALLY' })"
    if ($Repeat -eq 1) { 'graphics memory: ' + ($memory -join ', '); $log }
}
if ($Repeat -gt 1) { "$survived of $Repeat sessions closed normally" }
