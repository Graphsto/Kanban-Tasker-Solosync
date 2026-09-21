param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = [IO.Path]::GetFullPath($Directory)
$report = Get-Content -LiteralPath (Join-Path $source 'build-report.json') -Raw | ConvertFrom-Json
$fixture = Join-Path $repo ("build/store-validation/{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$bundlePath = Join-Path $fixture $report.bundle
function Save-Report {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'build-report.json') -Encoding utf8
}
function Expect-Rejection([string]$Name, [string]$Message) {
    $failure = $null
    try { & "$PSScriptRoot/verify-store-package.ps1" -Directory $fixture | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notlike "*$Message*") { throw "$Name was not rejected as expected: $failure" }
    if (Test-Path -LiteralPath (Join-Path $fixture 'verification.json')) { throw 'Rejected input produced successful verification evidence.' }
    Write-Host "Rejected $Name"
}
Save-Report
Expect-Rejection 'missing bundle' 'Missing Store bundle'
Copy-Item -LiteralPath (Join-Path $source $report.bundle) -Destination $bundlePath
$report.sha256 = '0' * 64; Save-Report
Expect-Rejection 'wrong checksum' 'checksum mismatch'
$report.sha256 = (Get-FileHash -LiteralPath $bundlePath).Hash
$productVersion = $report.productVersion
$report.productVersion = '1.0.0.0'; Save-Report
Expect-Rejection 'wrong product version' 'does not match'
$report.productVersion = $productVersion
# Remove a real package, then update the outer checksum. This ensures validation
# inspects the bundle instead of trusting the report/hash alone.
$zip = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Update)
try { ($zip.Entries | Where-Object FullName -Like '*-arm64.msix' | Select-Object -First 1).Delete() }
finally { $zip.Dispose() }
$report.sha256 = (Get-FileHash -LiteralPath $bundlePath).Hash; Save-Report
Expect-Rejection 'incomplete architecture payload' 'Missing or oversized package entry'
Write-Host "Four Store artifact rejection cases passed. Fixtures: $fixture"
