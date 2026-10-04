# Builds the release files into .\publish:
#   GlassDock.exe       standalone app (self-contained, single file)
#   GlassDockSetup.exe  installer that carries GlassDock.exe inside it
# The version comes from <Version> in GlassDock.csproj.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { "$env:ProgramFiles\dotnet\dotnet.exe" }
$version = ([xml](Get-Content GlassDock.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $PSScriptRoot 'publish'

Write-Host "Building GlassDock $version"
& $dotnet publish GlassDock.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $out
if ($LASTEXITCODE) { throw "GlassDock publish failed" }

& $dotnet build Installer\GlassDockSetup.csproj -c Release "-p:Version=$version" "-p:PayloadPath=$out\GlassDock.exe" -o "$out\setup"
if ($LASTEXITCODE) { throw "Installer build failed" }
Copy-Item "$out\setup\GlassDockSetup.exe" "$out\GlassDockSetup.exe" -Force
Remove-Item "$out\setup" -Recurse -Force

Get-ChildItem $out\*.exe | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
