# JeekTokenPlanUsage self-updater. Launched by the running app right before it
# exits; waits for the process to release file locks, downloads the new .zip into
# %TEMP%, clears the install directory (preserving user data and the committed
# scripts), expands the archive in place, and restarts the app.
#
# Wipe-and-extract (rather than deleting only *.dll) so stale files under the
# NetBeauty Libs/ folder don't accumulate across updates.

$ErrorActionPreference = "Stop"
$appName = "JeekTokenPlanUsage"
$installDir = $PSScriptRoot

# Wait for the previous instance to fully exit so we can replace files.
Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        Write-Host "Waiting for $appName (pid $($_.Id)) to exit..."
        $_.WaitForExit()
    } catch {}
}

if ($args.Count -eq 0) {
    Write-Host "No download URL provided. Exiting..."
    Start-Sleep -Seconds 3
    Exit 1
}

$downloadUrl = $args[0]
$packPath = Join-Path $env:TEMP "$appName.zip"
$stageDir = Join-Path $env:TEMP "$appName-update"

try {
    Write-Host "Downloading update from $downloadUrl..."
    # WebClient is faster than Invoke-WebRequest for large binary downloads and
    # doesn't burn memory buffering the whole response.
    $client = New-Object System.Net.WebClient
    $client.Headers.Add("User-Agent", "$appName-Updater/1.0")
    $client.DownloadFile($downloadUrl, $packPath)

    if (-not (Test-Path $packPath)) {
        Write-Host "Download did not produce $packPath"
        Start-Sleep -Seconds 5
        Exit 1
    }

    Write-Host "Extracting update package..."
    Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    Expand-Archive -Path $packPath -DestinationPath $stageDir -Force

    $stagedExe = Join-Path $stageDir "$appName.exe"
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        Write-Host "Update package is missing $appName.exe."
        Start-Sleep -Seconds 5
        Exit 1
    }

    # Clear the install dir but preserve portable user data and the committed
    # scripts (the package carries them too, so they are restored regardless).
    $preserveNames = @("Config", "AutoUpdate.ps1", "Setup.cmd", "dotnet-install.ps1")
    Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $preserveNames -inotcontains $_.Name } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force

    Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue
}
catch {
    Write-Host "Update failed: $($_.Exception.Message)"
    Start-Sleep -Seconds 5
}

# Restart the app, forwarding any extra arguments the caller passed through.
$exePath = Join-Path $installDir "$appName.exe"
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
