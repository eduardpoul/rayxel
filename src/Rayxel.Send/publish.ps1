# Builds the sender and its window as one folder, to copy to the machine that sends:
#
#   powershell -File src/Rayxel.Send/publish.ps1                  -> publish/sender-win-x64
#   powershell -File src/Rayxel.Send/publish.ps1 -Target linux-x64 -> publish/sender-linux-x64
#
# The folder holds rayxel-send (the sender, no window), rayxel-send-ui (its window, which
# starts the sender from beside itself when it is not running) and PyroWave's library. On a
# Steam Deck use the Decky plugin instead (src/rayxel-decky/build.sh): Game Mode shows no
# desktop windows.
#
# Windows: both self-contained single files, no .NET needed on the machine. PyroWave's DLL
# comes from src/pyrowave-windows/out when it has been built there.
# Linux: the sender is NativeAOT, built in a Linux container (src/Rayxel.Send/Dockerfile),
# the window self-contained; both need Docker and nothing else here.

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
        --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
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

dotnet publish (Join-Path $src 'Rayxel.Send.App\Rayxel.Send.App.csproj') -c Release -r $Target `
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "rayxel-send-ui did not build" }

# Debug symbols are embedded or not wanted in a folder meant for copying.
Get-ChildItem $out -Filter *.pdb | Remove-Item
Write-Host "sender: $out"
Get-ChildItem $out | Format-Table Name, Length -AutoSize
