param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$binRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'bin'))
$appPath = Join-Path $binRoot 'JeekTokenPlanUsage.exe'
Set-Location -LiteralPath $repoRoot

Get-Process -Name JeekTokenPlanUsage -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Path -and [string]::Equals($_.Path, $appPath, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }
}

function Remove-GeneratedItem([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($binRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean outside bin: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# A legacy client may still hold this build copy open. Rename before publishing.
$adapterPath = Join-Path $binRoot 'JeekTokenPlanUsageMcp.exe'
if (Test-Path -LiteralPath $adapterPath) {
    Move-Item -LiteralPath $adapterPath -Destination ($adapterPath + '.old.' + [Guid]::NewGuid().ToString('N'))
}
Get-ChildItem -LiteralPath $binRoot -Filter 'JeekTokenPlanUsageMcp.exe.old.*' -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-GeneratedItem $_.FullName } catch { Write-Verbose $_ } }

if ($Configuration -eq 'Release') {
    # Only generated outputs: never delete Config, Logs, scripts or other user data.
    foreach ($name in @('Libs', 'runtimes', 'zh-CN', 'JeekTokenPlanUsage.exe',
        'JeekTokenPlanUsage.dll', 'JeekTokenPlanUsage.pdb', 'JeekTokenPlanUsage.deps.json',
        'JeekTokenPlanUsage.runtimeconfig.json', 'JeekTools.dll', 'JeekTools.pdb')) {
        Remove-GeneratedItem (Join-Path $binRoot $name)
    }
}

dotnet build 'JeekTokenPlanUsage\JeekTokenPlanUsage.csproj' --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish 'Tools\JeekTokenPlanUsageMcp\JeekTokenPlanUsageMcp.csproj' --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Configuration -eq 'Release') {
    Get-ChildItem -LiteralPath $binRoot -Filter '*.pdb' -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\(Config|Logs)\\' } |
        ForEach-Object { Remove-GeneratedItem $_.FullName }
}
if ($Launch) { Start-Process -FilePath $appPath -WorkingDirectory $binRoot }
