# Builds Rayxel for Windows as one folder to copy anywhere:
#
#   powershell -File src/Rayxel/publish.ps1   -> publish/rayxel-win-x64
#
# Rayxel.exe - watching, and on its Stream page this computer as a sender - and beside it
# rayxel-send.exe, the program that does the sending, which the Stream page starts and which
# goes on with Rayxel closed. NVIDIA's DLSS networks (nvngx_dlss.dll, nvngx_dlssg.dll) sit
# beside them, where NGX looks. Both executables are self-contained single files: nothing
# needs to be installed on the machine.

$ErrorActionPreference = 'Stop'

$src = Split-Path $PSScriptRoot -Parent
$root = Split-Path $src -Parent
$out = Join-Path $root 'publish\rayxel-win-x64'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $src 'Rayxel\Rayxel.csproj') -c Release -r win-x64 --self-contained -o $out
if ($LASTEXITCODE -ne 0) { throw 'Rayxel did not build' }

dotnet publish (Join-Path $src 'Rayxel.Send\Rayxel.Send.csproj') -c Release -f net10.0-windows -r win-x64 `
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'rayxel-send did not build' }

# Debug symbols are embedded or not wanted in a folder meant for copying.
Get-ChildItem $out -Filter *.pdb | Remove-Item
Write-Host "rayxel: $out"
Get-ChildItem $out | Format-Table Name, Length -AutoSize
