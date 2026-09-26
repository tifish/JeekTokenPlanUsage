$ErrorActionPreference = "Stop"
$appName = "JeekTokenPlanUsage"

# The app has already downloaded, extracted, and verified the update package
# into a staging folder (via JeekTools.AutoUpdater) before launching this
# script. All that remains here is the short critical window: wait for the app
# to exit, swap the files, restart.

if ($args.Count -eq 0) {
    Exit 1
}

$stageDir = [IO.Path]::GetFullPath($args[0]).TrimEnd([IO.Path]::DirectorySeparatorChar)
$installDir = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
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
    $updateRoot = Split-Path -Parent $stageDir
    $rootName = Split-Path -Leaf $updateRoot
    if ((Split-Path -Leaf $stageDir) -ne 'package' -or
        $rootName -notin @('Update', "$appName-update") -or
        $installDir.Equals($updateRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $installDir.StartsWith($updateRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $updateRoot.StartsWith($installDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid update package location: $stageDir"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "$appName.exe"))) {
        throw "Staged update package is missing $appName.exe: $stageDir"
    }

    Write-Host "[1/3] Waiting for $appName to exit..."
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Path -and [string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase)) {
            if (-not $_.WaitForExit(60000)) { throw "The installed application did not exit within 60 seconds." }
        }
    }

    Write-Host "[2/3] Installing files..."
    # Mirror the package while keeping user data and the running updater script.
    # Both paths are absolute and their non-overlap was checked above.
    robocopy $stageDir $installDir /MIR /XD Config Logs /XF AutoUpdate.ps1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Update copy failed (robocopy exit code $LASTEXITCODE)." }
    $newScript = Join-Path $stageDir 'AutoUpdate.ps1'
    if (Test-Path -LiteralPath $newScript) {
        Copy-Item -LiteralPath $newScript -Destination (Join-Path $installDir 'AutoUpdate.ps1') -Force
    }

    # The verified root is Update (or the legacy name), never the install directory.
    Remove-Item -LiteralPath $updateRoot -Recurse -Force

    Write-Host "[3/3] Restarting $appName..."
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir -WindowStyle Hidden
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    # Best effort: bring the app back even if the install failed.
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir -WindowStyle Hidden
    }
    Start-Sleep -Seconds 5
    Exit 1
}
