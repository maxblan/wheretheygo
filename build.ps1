param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Get-UserDataPath {
    if ($env:CSII_USERDATAPATH -and $env:CSII_USERDATAPATH.Trim()) {
        return $env:CSII_USERDATAPATH
    }

    return Join-Path $env:USERPROFILE "AppData\\LocalLow\\Colossal Order\\Cities Skylines II"
}

$userDataPath = Get-UserDataPath
$modTarget = Join-Path $userDataPath "Mods\\StationSuitabilityOverlay"

Write-Host "Building C# mod (StationSuitabilityOverlay)..." -ForegroundColor Cyan
& dotnet build ".\dotnet\StationSuitabilityOverlay.csproj" -c $Configuration

Write-Host "Deploying C# mod artifacts..." -ForegroundColor Cyan
if (!(Test-Path $modTarget)) {
    New-Item -ItemType Directory -Path $modTarget | Out-Null
}
Copy-Item ".\\dotnet\\bin\\$Configuration\\net48\\StationSuitabilityOverlay.*" $modTarget -Force

Write-Host "Building UI (StationSuitabilityOverlay)..." -ForegroundColor Cyan
Push-Location ".\ui"
if (!(Test-Path "node_modules")) {
    & npm.cmd install
}
if (-not $env:CSII_USERDATAPATH) {
    $env:CSII_USERDATAPATH = $userDataPath
}
& npm.cmd run build
Pop-Location

$uiTarget = $modTarget
if (Test-Path $uiTarget) {
    Copy-Item ".\\ui\\mod.json" $uiTarget -Force
}
