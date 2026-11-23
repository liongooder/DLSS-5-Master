# Run after verifying the installed add-on and host. The install plan supplies
# the trusted original release paths and checksums, with no interactive picks.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InstallPlan
)

$ErrorActionPreference = 'Stop'

function Confirm-OrdinaryFile([string] $Path) {
    if (![IO.File]::Exists($Path)) { throw "File missing: $Path" }
    for ($ancestor = $Path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) {
            throw "A link or junction cannot supply release metadata: $ancestor"
        }
    }
}

function Read-Digest([string] $Path) {
    Confirm-OrdinaryFile $Path
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$planFile = (Resolve-Path -LiteralPath $InstallPlan).Path
Confirm-OrdinaryFile $planFile
if ((Get-Item -LiteralPath $planFile).Length -gt 65536) { throw 'Install plan is too large.' }
$plan = Get-Content -Raw -LiteralPath $planFile | ConvertFrom-Json -AsHashtable -Depth 16
if ($plan.build -isnot [string] -or $plan.game -isnot [string] -or $plan.files -isnot [System.Collections.IDictionary]) {
    throw 'Use the verified NeuralMaster install plan.'
}
$label = $plan.build.Trim()
if ($label.Length -lt 1 -or $label.Length -gt 64 -or $label -match '\p{Cc}') {
    throw 'The install plan has an invalid build label.'
}
$game = [IO.Path]::GetFullPath($plan.game)
$gamePrefix = $game.TrimEnd('\') + '\'
if ($planFile.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase) -or $plan.files.Count -eq 0) {
    throw 'Use a populated release plan saved outside the game.'
}
$digests = @{}
foreach ($entry in $plan.files.GetEnumerator()) {
    $relative = $entry.Key
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?([\\/]|$)|:') {
        throw "Invalid install target: $relative"
    }
    $target = [IO.Path]::GetFullPath($relative, $game)
    if (!$target.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Install target outside the game: $relative"
    }
    $name = [IO.Path]::GetFileName($target)
    if ($name -ieq 'dxgi.dll') { $role = 'HostSha256' }
    elseif ($name -match '^NeuralMaster(?:[-.].*)?\.addon(?:32|64)?$') { $role = 'AddonSha256' }
    else { throw "Unexpected file in the NeuralMaster install plan: $relative" }

    $record = $entry.Value
    if ($record.source -isnot [string] -or ![IO.Path]::IsPathFullyQualified($record.source) -or
        $record.newSHA256 -isnot [string] -or $record.newSHA256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Missing release path or checksum for $relative"
    }
    $original = [IO.Path]::GetFullPath($record.source)
    $digest = $record.newSHA256.ToLowerInvariant()
    if ($original.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Read-Digest $original) -ne $digest -or (Read-Digest $target) -ne $digest) {
        throw "The installed file and original release checksum do not match: $relative"
    }
    if ($digests.ContainsKey($role) -and $digests[$role] -ne $digest) {
        throw "The installed copies have different checksums: $relative"
    }
    $digests[$role] = $digest
}
if ($digests.Count -ne 2) { throw 'The install plan must verify both an add-on and its matching ReShade host.' }

$dataRoot = $env:DLSS5MASTER_DATA
if ([string]::IsNullOrEmpty($dataRoot)) {
    $dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DLSS5Master'
}
$catalogue = Join-Path ([IO.Path]::GetFullPath($dataRoot)) 'neuralmaster-releases'
if ($catalogue.Equals($game.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $catalogue.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a manager-owned release catalogue outside the game.'
}
for ($dir = $catalogue; $dir; $dir = [IO.Path]::GetDirectoryName($dir)) {
    if ((Test-Path -LiteralPath $dir) -and ([IO.File]::GetAttributes($dir) -band [IO.FileAttributes]::ReparsePoint)) {
        throw "A link or junction cannot hold release metadata: $dir"
    }
}
[void][IO.Directory]::CreateDirectory($catalogue)
$destination = Join-Path $catalogue ($digests.AddonSha256 + '.json')
$receipt = [ordered]@{ Name = $label; AddonSha256 = $digests.AddonSha256; HostSha256 = $digests.HostSha256 }

if ([IO.File]::Exists($destination)) {
    Confirm-OrdinaryFile $destination
    if ((Get-Item -LiteralPath $destination).Length -gt 4096) { throw 'The existing release record is too large.' }
    $receiptName = [IO.Path]::GetFileName($destination)
    $onDisk = [IO.Directory]::EnumerateFiles($catalogue, $receiptName) | Select-Object -First 1
    $existing = Get-Content -Raw -LiteralPath $destination | ConvertFrom-Json -AsHashtable -Depth 8
    if ([IO.Path]::GetFileName($onDisk) -cne $receiptName -or
        $existing.Keys -cnotcontains 'Name' -or $existing.Keys -cnotcontains 'AddonSha256' -or
        $existing.Keys -cnotcontains 'HostSha256' -or $existing.AddonSha256 -ne $receipt.AddonSha256 -or
        $existing.HostSha256 -ne $receipt.HostSha256 -or $existing.Name -cne $receipt.Name) {
        throw 'This add-on already has a different release record. The existing record was preserved.'
    }
} else {
    $temporary = Join-Path $catalogue (([Guid]::NewGuid().ToString('N')) + '.stage')
    try {
        [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $destination)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
Write-Output $destination
