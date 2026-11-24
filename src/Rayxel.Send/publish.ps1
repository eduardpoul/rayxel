# Builds the sender alone, rayxel-send, as one file to copy to the machine that sends:
#
#   powershell -File src/Rayxel.Send/publish.ps1                  -> publish/sender-win-x64
#   powershell -File src/Rayxel.Send/publish.ps1 -Target linux-x64 -> publish/sender-linux-x64
#
# It has no window. On Windows its user interface is Rayxel's Stream page, and the Windows
# build of Rayxel (src/Rayxel/publish.ps1) already carries rayxel-send.exe beside Rayxel.exe -
# this is for a machine that only sends. On a Steam Deck use the Decky plugin
# (src/rayxel-decky/build.sh), which packs the Linux build.
#
# Windows: a self-contained, compressed single file, no .NET needed on the machine. PyroWave's
# DLL comes from src/pyrowave-windows/out when it has been built there.
# Linux: NativeAOT, built in a Linux container (src/Rayxel.Send/Dockerfile); needs Docker and
# nothing else here.

param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Target = 'win-x64'
)
$ErrorActionPreference = 'Stop'

$src = Split-Path $PSScriptRoot -Parent
$root = Split-Path $src -Parent
$out = Join-Path $root "publish\sender-$Target"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

if ($Target -eq 'win-x64') {
    dotnet publish (Join-Path $src 'Rayxel.Send\Rayxel.Send.csproj') -c Release -f net10.0-windows -r win-x64 `
        --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "rayxel-send did not build" }
}
else {
    Push-Location $src
    try {
        docker build -f Rayxel.Send/Dockerfile --output "type=local,dest=$out" .
        if ($LASTEXITCODE -ne 0) { throw "rayxel-send did not build" }
    }
    finally { Pop-Location }
}

# Debug symbols are embedded or not wanted in a folder meant for copying.
Get-ChildItem $out -Filter *.pdb | Remove-Item
Write-Host "sender: $out"
Get-ChildItem $out | Format-Table Name, Length -AutoSize
