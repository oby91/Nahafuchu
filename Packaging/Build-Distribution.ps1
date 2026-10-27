$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distRoot = Join-Path $projectRoot 'dist'
$portableRoot = Join-Path $distRoot 'portable'
$setupOutput = Join-Path $distRoot 'setup-wizard'
$portableZip = Join-Path $distRoot 'Nahafuchu-Portable-1.0.2.zip'
$setupExe = Join-Path $distRoot 'Nahafuchu-Setup-1.0.2.exe'
$projectFile = Join-Path $projectRoot 'LayoutFix.csproj'
$setupProject = Join-Path $projectRoot 'SetupWizard\NahafuchuSetup.csproj'
$readme = Join-Path $projectRoot 'README.md'
$icon = Join-Path $projectRoot 'Assets\Nahafuchu.ico'

New-Item -ItemType Directory -Path $portableRoot,$setupOutput -Force | Out-Null

& dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true --output $portableRoot
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath $readme -Destination (Join-Path $portableRoot 'README.md') -Force
Copy-Item -LiteralPath $icon -Destination (Join-Path $portableRoot 'Nahafuchu.ico') -Force
Compress-Archive -LiteralPath (Join-Path $portableRoot 'Nahafuchu.exe'),(Join-Path $portableRoot 'README.md'),(Join-Path $portableRoot 'Nahafuchu.ico') `
    -DestinationPath $portableZip -CompressionLevel Optimal -Force

& dotnet publish $setupProject --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true --output $setupOutput
if ($LASTEXITCODE -ne 0) { throw "Setup wizard publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $setupOutput 'Nahafuchu-Setup.exe') -Destination $setupExe -Force
if (-not (Test-Path -LiteralPath $setupExe)) { throw "The setup wizard executable was not created at $setupExe." }

Get-Item -LiteralPath $setupExe,$portableZip | Select-Object Name,Length,FullName

