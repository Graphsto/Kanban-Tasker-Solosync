param([ValidateSet('x64','arm64')][string]$Architecture = 'x64', [ValidateSet('Local','Store')][string]$Channel = 'Local')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ("build/ui-tests/{0}" -f [Guid]::NewGuid().ToString('N'))
Push-Location $repo
try {
    dotnet publish src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj -c Release -r "win-$Architecture" -p:Platform=$Architecture -p:KanbanChannel=$Channel -p:RestoreLockedMode=true -p:KanbanUiSmokeTest=true -o $output
    if ($LASTEXITCODE) { throw 'Desktop test build failed.' }
    $executable = Join-Path $output 'KanbanTasker.exe'
    foreach ($scenario in @('workspace','first-run','missing','invalid')) {
        $testProcess = Start-Process -FilePath $executable -ArgumentList "--startup-$scenario" -WindowStyle Hidden -PassThru
        if (-not $testProcess.WaitForExit(60000)) {
            # Stop only the test process started above, after confirming its exact executable.
            $current = Get-Process -Id $testProcess.Id -ErrorAction SilentlyContinue
            if ($current -and $current.Path -eq $executable) { Stop-Process -Id $current.Id }
            throw "Desktop test timed out. Logs: $output"
        }
        $resultsDirectory = if ($scenario -eq 'workspace') { 'smoke-results' } else { "smoke-results-$scenario" }
        $report = Join-Path $output "$resultsDirectory/result.json"
        if (-not (Test-Path -LiteralPath $report)) { throw "Desktop test did not produce a result. Logs: $output" }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (-not $result.success) { throw "Desktop test failed: $($result.error)" }
        Write-Host "$($result.checks.Count) desktop assertions passed. Rendered PNGs and report: $report"
    }
} finally { Pop-Location }
