param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building C# mod (TransitArchitect)..." -ForegroundColor Cyan
& dotnet build ".\dotnet\TransitArchitect.csproj" -c $Configuration

# The build deploys automatically to %CSII_USERDATAPATH%\Mods\TransitArchitect
# via the modding toolchain's Mod.targets.
