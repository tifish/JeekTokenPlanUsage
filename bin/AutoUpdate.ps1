# JeekTokenPlanUsage self-updater. Launched by the running app right before
# it exits; waits for the process to release file locks, downloads the new
# .zip into %TEMP%, sweeps the stale binaries out of the install directory,
# expands the archive in place, and restarts the app.
#
# Version identity is the total git commit count, stamped into AssemblyVersion
# at CI build time. This script therefore does not need to touch file
# timestamps — extraction order and zip DOS-time quirks no longer affect
# the next update check.

$ErrorActionPreference = "Stop"
$appName = "JeekTokenPlanUsage"

# Wait for the previous instance to fully exit so we can replace files.
$processes = Get-Process -Name $appName -ErrorAction SilentlyContinue
foreach ($p in $processes) {
    try {
        Write-Host "Waiting for $appName (pid $($p.Id)) to exit..."
        $p.WaitForExit()
    } catch {}
}

if ($args.Count -eq 0) {
    Write-Host "No download URL provided. Exiting..."
    Start-Sleep -Seconds 3
    Exit 1
}

$downloadUrl = $args[0]
$packPath = Join-Path $env:TEMP "$appName.zip"

try {
    Write-Host "Downloading update from $downloadUrl..."
    # WebClient is faster than Invoke-WebRequest for large binary downloads and
    # doesn't burn memory buffering the whole response.
    $client = New-Object System.Net.WebClient
    $client.Headers.Add("User-Agent", "JeekTokenPlanUsage-Updater/1.0")
    $client.DownloadFile($downloadUrl, $packPath)

    if (-not (Test-Path $packPath)) {
        Write-Host "Download did not produce $packPath"
        Start-Sleep -Seconds 5
        Exit 1
    }

    # Sweep old binaries; user data lives under %APPDATA%\JeekTokenPlanUsage,
    # so nothing here is destructive to settings or logs.
    Get-ChildItem -Path $PSScriptRoot -Filter "*.dll"               -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $PSScriptRoot -Filter "*.pdb"               -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $PSScriptRoot -Filter "*.deps.json"         -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $PSScriptRoot -Filter "*.runtimeconfig.json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Localization satellite assemblies live in culture-named subfolders;
    # delete the folders so removed cultures don't accumulate.
    foreach ($dir in Get-ChildItem -Path $PSScriptRoot -Directory -ErrorAction SilentlyContinue) {
        if ($dir.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$') {
            Remove-Item -Recurse -Force -Path $dir.FullName -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Expanding archive into $PSScriptRoot..."
    Expand-Archive -Path $packPath -DestinationPath $PSScriptRoot -Force

    Remove-Item -Force -Path $packPath -ErrorAction SilentlyContinue
}
catch {
    Write-Host "Update failed: $($_.Exception.Message)"
    Start-Sleep -Seconds 5
}

# Restart the app, forwarding any extra arguments the caller passed through.
$exePath = Join-Path $PSScriptRoot "$appName.exe"
if (Test-Path $exePath) {
    Write-Host "Starting $appName..."
    if ($args.Count -gt 1) {
        Start-Process -FilePath $exePath -ArgumentList $args[1..$args.Length]
    } else {
        Start-Process -FilePath $exePath
    }
} else {
    Write-Host "Cannot find $exePath after update; aborting restart."
    Start-Sleep -Seconds 5
}
