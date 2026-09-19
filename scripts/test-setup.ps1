$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ("build/setup-ui-tests/{0}" -f [Guid]::NewGuid().ToString('N'))
Push-Location $repo
try {
    dotnet build src/KanbanTasker.Setup/KanbanTasker.Setup.csproj -c Release -r win-x64 -p:Platform=x64 -p:RestoreLockedMode=true -p:KanbanSetupSmokeTest=true -o $output
    if ($LASTEXITCODE) { throw 'Installer test build failed.' }
    $executable = Join-Path $output 'KanbanTasker.Setup.exe'
    $testProcess = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(30000)) {
        $current = Get-Process -Id $testProcess.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $executable) { Stop-Process -Id $current.Id }
        throw "Installer test timed out: $output"
    }
    $report = Join-Path $output 'result.json'
    if (-not (Test-Path -LiteralPath $report)) { throw "Installer test did not report: $output" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success) { throw "Installer test failed: $($result.error)" }
    Write-Host "$($result.checks.Count) installer UI assertions passed: $report"
} finally { Pop-Location }
