param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building C# mod (StationSuitabilityOverlay)..." -ForegroundColor Cyan
& dotnet build ".\dotnet\StationSuitabilityOverlay.csproj" -c $Configuration

# The build deploys automatically to %CSII_USERDATAPATH%\Mods\StationSuitabilityOverlay
# via the modding toolchain's Mod.targets.
