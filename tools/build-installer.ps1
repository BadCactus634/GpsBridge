# Pubblica l'app e crea l'installer: installer\output\GpsBridge-setup.exe
# Uso (dalla radice del repo):  powershell -File tools\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root 'src\GpsBridge.App\GpsBridge.App.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Versione $version"
dotnet publish (Join-Path $root 'src\GpsBridge.App') -c Release -r win-x64 --self-contained -o (Join-Path $root 'installer\publish')
if ($LASTEXITCODE -ne 0) { throw 'publish non riuscito' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 non trovato (winget install JRSoftware.InnoSetup)' }
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\GpsBridge.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC non riuscito' }
Get-FileHash (Join-Path $root 'installer\output\GpsBridge-setup.exe') -Algorithm SHA256 | Format-List Hash, Path
