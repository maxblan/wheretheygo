param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "Building C# mod (WhereTheyGo)..." -ForegroundColor Cyan
& dotnet build ".\dotnet\WhereTheyGo.csproj" -c $Configuration

# The build deploys automatically to %CSII_USERDATAPATH%\Mods\WhereTheyGo
# via the modding toolchain's Mod.targets.
