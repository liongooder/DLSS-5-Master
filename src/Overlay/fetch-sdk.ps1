# Downloads the SDK files dlss5-overlay compiles against into external\, each pinned to a commit and
# checked against its SHA-256. They are not kept in this repository:
#   NVIDIA NGX (DLSS SDK)            https://github.com/NVIDIA/DLSS                  NVIDIA RTX SDKs licence
#   NVIDIA Optical Flow interface    the official SDK as mirrored at mbucchia/Optical-Flow-SDK   BSD-3-Clause
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'external'
$dlss = 'https://raw.githubusercontent.com/NVIDIA/DLSS/9981c6350dc58c0ce21f024fd65e31e851c31689'
$nvof = 'https://raw.githubusercontent.com/mbucchia/Optical-Flow-SDK/54e68293b4898a530bc07e4d7df71efbc5d30f9b/NvOFInterface'
$files = @(
    @("$dlss/include/nvsdk_ngx.h",               'ngx\nvsdk_ngx.h',               'dc38e7467cf415379c9d12ae1b6e4a494c453ed92720fb53e92aecb523e7b848'),
    @("$dlss/include/nvsdk_ngx_defs.h",          'ngx\nvsdk_ngx_defs.h',          'ea23f33497cd274860d1c25a97644fce807dcb0037c594547203343103fad03e'),
    @("$dlss/include/nvsdk_ngx_params.h",        'ngx\nvsdk_ngx_params.h',        '943bc8cc5cdae03b6303016fbad3183636f2335ae27a2d18776798c3b4efabbc'),
    @("$dlss/include/nvsdk_ngx_helpers.h",       'ngx\nvsdk_ngx_helpers.h',       '5bcbadfe7478b802cf6d3aca4dc5ddd7d0889b99726e69c63f9e9bd555f44471'),
    @("$dlss/include/nvsdk_ngx_helpers_d3d.h",   'ngx\nvsdk_ngx_helpers_d3d.h',   'ec75224f36ed6580baaf250fa83620405ad81a1d98c2c436654a0c9cf6a6b8ba'),
    @("$dlss/include/nvsdk_ngx_helpers_cuda.h",  'ngx\nvsdk_ngx_helpers_cuda.h',  'f2851a76107bdf4fbcb7b261f3c35573c312d8ecb4bc18fb02d9e2cec7705042'),
    @("$dlss/include/nvsdk_ngx_loader.h",        'ngx\nvsdk_ngx_loader.h',        'abdf5f66a692620538357d29fdf78e3e2a677ee8140b7a638962ecb766585e75'),
    @("$dlss/lib/Windows_x86_64/x64/nvsdk_ngx_d.lib", 'ngx\lib\nvsdk_ngx_d.lib', 'bc43df9ec032004fde681d3e3afd01cde239e03a0125b822e88674c84287f99c'),
    @("$nvof/nvOpticalFlowCommon.h",             'nvof\nvOpticalFlowCommon.h',    'cdbdbf0797fc02d2de1051520d4164bb1e36b7722009f46cb16216675319436a'),
    @("$nvof/nvOpticalFlowD3D11.h",              'nvof\nvOpticalFlowD3D11.h',     'c33a4da15d849e26270bd9f3aff9fe06e0b60eedfbf5bc455b4e326dd1796003')
)
foreach ($f in $files) {
    $dest = Join-Path $root $f[1]
    if ((Test-Path $dest) -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $f[2]) { continue }
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Invoke-WebRequest $f[0] -OutFile $dest -UseBasicParsing
    if ((Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -ne $f[2]) { Remove-Item $dest; throw "$($f[1]) failed its checksum" }
}
Write-Host 'SDK files ready.'
