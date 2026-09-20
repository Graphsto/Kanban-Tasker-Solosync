param([ValidateSet('x64','arm64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo ("build/showcase/{0}" -f [Guid]::NewGuid().ToString('N'))
Push-Location $repo
try {
    dotnet publish src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj -c Release -r "win-$Architecture" -p:Platform=$Architecture -p:RestoreLockedMode=true -p:KanbanUiSmokeTest=true -o $output
    if ($LASTEXITCODE) { throw 'Showcase build failed.' }
    $executable = Join-Path $output 'KanbanTasker.exe'
    $captureProcess = Start-Process -FilePath $executable -ArgumentList '--startup-showcase' -WindowStyle Hidden -PassThru
    if (-not $captureProcess.WaitForExit(60000)) {
        $current = Get-Process -Id $captureProcess.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $executable) { Stop-Process -Id $current.Id }
        throw "Showcase timed out. Logs: $output"
    }
    $results = Join-Path $output 'smoke-results-showcase'
    $report = Join-Path $results 'result.json'
    if (-not (Test-Path -LiteralPath $report)) { throw "No showcase result. Logs: $output" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success) { throw "Showcase failed: $($result.error)" }
    $names = @('board-dark', 'drag-and-drop', 'task-details', 'calendar', 'board-dark-blue', 'appearance')
    foreach ($name in $names) {
        if (-not (Test-Path -LiteralPath (Join-Path $results "$name.png"))) { throw "Missing screenshot: $name" }
    }
    $destination = Join-Path $repo 'branding/screenshots'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($name in $names) {
        Copy-Item -LiteralPath (Join-Path $results "$name.png") -Destination (Join-Path $destination "$name.png")
    }
    Write-Host "Six screenshots captured from the real app with isolated demo data: $destination"
} finally { Pop-Location }
