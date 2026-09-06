<#
    Builds the distributable zip for Custom Clip Editor.

    Produces a self-contained win-x64 build, so the machine it runs on does
    not need .NET installed. FFmpeg is deliberately NOT bundled: the app
    launches it as a separate process, and shipping the GPL binaries would
    add redistribution obligations for no real benefit. The app tells the
    user how to install it.
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$csproj  = Join-Path $root 'ClipEditor\ClipEditor.csproj'
$distDir = Join-Path $root 'dist'
$stage   = Join-Path $distDir 'CustomClipEditor'

if (-not $Version) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
               Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { $Version = '1.0.0' }

Write-Host "Building Custom Clip Editor v$Version" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# LibVLC ships every Windows architecture; we only distribute x64.
foreach ($arch in 'win-arm64', 'win-x86') {
    $path = Join-Path $stage "libvlc\$arch"
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

# Debug symbols are not useful to end users.
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

Copy-Item (Join-Path $root 'LICENSE')   $stage -Force
Copy-Item (Join-Path $root 'README.md') $stage -Force

$zip = Join-Path $distDir "CustomClipEditor-v$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$folderMB = [math]::Round((Get-ChildItem $stage -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipMB    = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  folder : $stage  ($folderMB MB)"
Write-Host "  zip    : $zip  ($zipMB MB)"
Write-Host ""
Write-Host "To publish it as a GitHub release, run:" -ForegroundColor Cyan
Write-Host "  gh release create v$Version `"$zip`" --title `"v$Version`" --notes `"...`""
