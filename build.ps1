$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GeorgeValdez.Edge510Installer\GeorgeValdez.Edge510Installer.csproj'
$output = Join-Path $PSScriptRoot 'release'
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish finalizó con el código $LASTEXITCODE" }
$exe = Join-Path $output 'Edge510-OPNsense-Installer.exe'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value "$hash  Edge510-OPNsense-Installer.exe" -Encoding ascii
Write-Host "Ejecutable creado: $exe"
