# Builds artifacts\JarvoScreenRecorder-Setup-<version>.exe
#   - publishes the app self-contained (the target PC needs no .NET installed)
#   - bundles ffmpeg.exe (from $env:SCREENRECORDER_FFMPEG, or the one on PATH)
#   - compiles installer\JarvoScreenRecorder.iss with Inno Setup 6
# Run:  powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\publish'
$project = Join-Path $root 'src\ScreenRecorder.App\ScreenRecorder.App.csproj'

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'No <Version> in ScreenRecorder.App.csproj' }
Write-Host "Version $version"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Build into artifacts\build so a copy of the app that is open (from bin\Release) doesn't block the build.
$buildOut = Join-Path $root 'artifacts\build\'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish "-p:OutDir=$buildOut" -p:SatelliteResourceLanguages=en -p:DebugType=None -p:DebugSymbols=false -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$ffmpeg = $env:SCREENRECORDER_FFMPEG
if (-not $ffmpeg) { $ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source }
Copy-Item $ffmpeg (Join-Path $publish 'ffmpeg.exe')
Copy-Item (Join-Path $root 'installer\THIRD-PARTY-NOTICES.txt') $publish
Write-Host "Bundled FFmpeg from $ffmpeg"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $root 'installer\JarvoScreenRecorder.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $root "artifacts\JarvoScreenRecorder-Setup-$version.exe"
Write-Host ("Built {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
