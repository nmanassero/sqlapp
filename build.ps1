# Genera la aplicación publicada y el instalador en .\dist
# Requisitos: .NET 8 SDK  y  Inno Setup 6 (https://jrsoftware.org/isinfo.php)
param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$publish = Join-Path $PSScriptRoot "publish\win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish src\SqlTableSync.App\SqlTableSync.App.csproj -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "Falló dotnet publish" }

New-Item -ItemType Directory -Force -Path dist | Out-Null
Compress-Archive -Path "$publish\*" -DestinationPath "dist\SqlTableSync-$Version-portable.zip" -Force

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" installer\SqlTableSync.iss
    if ($LASTEXITCODE -ne 0) { throw "Falló Inno Setup" }
    Write-Host "Instalador: dist\SqlTableSync-Setup-$Version.exe"
} else {
    Write-Warning "No se encontró Inno Setup: sólo se generó la versión portable (dist\SqlTableSync-$Version-portable.zip)."
}
