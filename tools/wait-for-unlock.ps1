# Waits until Windows has released every file in a folder, then exits 0.
#
# Deploying is a build step, and MSBuild starts it by removing the Mods folder. Windows
# refuses that while the game still holds a handle on the DLLs it loaded, and it holds
# them for a moment after the process is gone. The old answer was to sleep 25 seconds and
# hope; this asks instead, four times a second, and comes back the moment it is true.
#
# The probe is an exclusive open (FileShare.None): it succeeds only when nothing else has
# the file open. It has to run as a WINDOWS process, because WSL's DrvFs ignores Windows share
# locks, so the same test from bash reports every file as free.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$TimeoutSeconds = 120
)

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($true) {
    $blocked = $null
    foreach ($file in Get-ChildItem -Path $Path -File -Recurse -ErrorAction SilentlyContinue) {
        try {
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Write', 'None')
            $stream.Close()
        }
        catch {
            $blocked = $file.Name
            break
        }
    }

    if (-not $blocked) {
        exit 0
    }

    if ((Get-Date) -ge $deadline) {
        Write-Output "still locked after $TimeoutSeconds s: $blocked"
        exit 1
    }

    Start-Sleep -Milliseconds 250
}
