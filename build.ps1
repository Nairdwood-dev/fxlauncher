$ErrorActionPreference = 'Stop'

$launcherRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $launcherRoot 'Nairdwood.Launcher.csproj'
$publishPath = Join-Path $launcherRoot 'publish\win-x64'
$cliHome = Join-Path $launcherRoot '.dotnet-cli'

$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Nairdwood Launcher publish failed with exit code $LASTEXITCODE."
}

Write-Host "Nairdwood Launcher published to: $publishPath" -ForegroundColor Green
