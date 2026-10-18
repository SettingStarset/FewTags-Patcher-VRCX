[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\Program Files\VRCX',
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = Join-Path $root 'VRCX-source'
$builtHtml = Join-Path $source 'build\html'
$appHtml = Join-Path $InstallPath 'html'

if (-not (Test-Path -LiteralPath (Join-Path $InstallPath 'VRCX.exe'))) {
    throw "VRCX.exe was not found under '$InstallPath'. Pass -InstallPath with the VRCX install directory."
}
if (Get-Process -Name VRCX -ErrorAction SilentlyContinue) {
    throw 'Close VRCX before patching or restoring its files.'
}

if ($Restore) {
    $backup = Get-ChildItem -LiteralPath $InstallPath -Directory -Filter 'FewTagsVRCXScriptBackup-*' |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $backup) { throw 'No FewTags VRCX backup was found in the installation directory.' }
    $savedHtml = Join-Path $backup.FullName 'html'
    if (-not (Test-Path -LiteralPath $savedHtml)) { throw "Backup is incomplete: '$savedHtml' is missing." }
    if (Test-Path -LiteralPath $appHtml) { Remove-Item -LiteralPath $appHtml -Recurse -Force }
    Copy-Item -LiteralPath $savedHtml -Destination $appHtml -Recurse
    Write-Host "Restored VRCX html from $($backup.Name)"
    exit 0
}

if (-not (Test-Path -LiteralPath (Join-Path $source 'package.json'))) {
    throw "The VRCX release source is missing. Clone tag 2026-10-18T13.40-d8619fb into '$source' first."
}

$databaseFiles = @('FewTagsv2.json', 'FewTags.json')
$databases = @{}
foreach ($file in $databaseFiles) {
    $database = Invoke-RestMethod -Uri ('https://raw.githubusercontent.com/Fewdys/FewTags/main/' + $file) -Headers @{ 'User-Agent' = 'FewTags-VRCX-Patcher' }
    if (-not $database.records -or $database.records.Count -lt 1) { throw "The downloaded $file database has no records." }
    if (-not ($database.records | Where-Object { $_.UserID } | Select-Object -First 1)) { throw "The downloaded $file database does not contain user IDs." }
    $databases[$file] = $database
}

Push-Location $source
try {
    if (-not (Test-Path -LiteralPath (Join-Path $source 'node_modules'))) {
        npm install --no-package-lock
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
    }
    npm run prod
    if ($LASTEXITCODE -ne 0) { throw 'VRCX frontend build failed.' }
} finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $builtHtml)) { throw "Build output not found: '$builtHtml'" }
$indexPath = Join-Path $builtHtml 'index.html'
$index = Get-Content -LiteralPath $indexPath -Raw
$marker = '<!-- FEWTAGS_DATA_PLACEHOLDER -->'
if (($index.Split($marker).Count - 1) -ne 1) { throw 'The VRCX frontend is missing its FewTags data marker.' }
$dataJson = @{ v2 = $databases['FewTagsv2.json']; legacy = $databases['FewTags.json'] } | ConvertTo-Json -Depth 100 -Compress
$dataJson = $dataJson.Replace('<', '\u003c').Replace('>', '\u003e').Replace('&', '\u0026')
$injectedScript = '<script>window.__VRCX_FEWTAGS__=' + $dataJson + ';</script>'
Set-Content -LiteralPath $indexPath -Value $index.Replace($marker, $injectedScript) -NoNewline -Encoding utf8

$backupName = 'FewTagsVRCXScriptBackup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$backupPath = Join-Path $InstallPath $backupName
New-Item -ItemType Directory -Path $backupPath | Out-Null
try {
    Copy-Item -LiteralPath $appHtml -Destination (Join-Path $backupPath 'html') -Recurse
    if (Test-Path -LiteralPath $appHtml) { Remove-Item -LiteralPath $appHtml -Recurse -Force }
    Copy-Item -LiteralPath $builtHtml -Destination $appHtml -Recurse
} catch {
    if (Test-Path -LiteralPath (Join-Path $backupPath 'html')) {
        if (Test-Path -LiteralPath $appHtml) { Remove-Item -LiteralPath $appHtml -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $backupPath 'html') -Destination $appHtml -Recurse
    }
    throw
}

Write-Host "FewTags profile chips installed. Original VRCX html is backed up at '$backupPath'."
Write-Host 'To undo: .\Patch-VRCX.ps1 -Restore'
