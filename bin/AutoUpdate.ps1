$ErrorActionPreference = "Stop"
$appName = "JeekTokenPlanUsage"

# The app has already downloaded, extracted, and verified the update package
# into a staging folder (via JeekTools.AutoUpdater) before launching this
# script. All that remains here is the short critical window: wait for the app
# to exit, swap the files, restart.

if ($args.Count -eq 0) {
    Exit 1
}

$stageDir = $args[0]
$installDir = $PSScriptRoot
$exePath = Join-Path $installDir "$appName.exe"

$Host.UI.RawUI.WindowTitle = "$appName Updater"

Write-Host "================================================================"
Write-Host " $appName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "$appName.exe"))) {
        throw "Staged update package is missing $appName.exe: $stageDir"
    }

    Write-Host "[1/3] Waiting for $appName to exit..."
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.WaitForExit()
        } catch {}
    }

    Write-Host "[2/3] Installing files..."
    # Preserve portable user data and the updater itself. Wipe-and-copy (rather
    # than copying over) so stale files under the NetBeauty Libs/ folder don't
    # accumulate across updates. The package carries the committed scripts, so
    # they are restored regardless.
    $preserveNames = @("Config", "AutoUpdate.ps1")
    Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $preserveNames -inotcontains $_.Name } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force

    # Remove the staging folder the app created (...\JeekTokenPlanUsage-update\package).
    $stageRoot = Split-Path -Parent $stageDir
    if ((Split-Path -Leaf $stageRoot) -eq "$appName-update") {
        Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue
    } else {
        Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    }

    Write-Host "[3/3] Restarting $appName..."
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    # Best effort: bring the app back even if the install failed.
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath
    }
    Start-Sleep -Seconds 5
    Exit 1
}
