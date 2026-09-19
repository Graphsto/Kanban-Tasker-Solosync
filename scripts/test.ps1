$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet restore tests/KanbanTasker.Tests/KanbanTasker.Tests.csproj --locked-mode
    if ($LASTEXITCODE) { throw 'Test restore failed.' }
    dotnet test tests/KanbanTasker.Tests/KanbanTasker.Tests.csproj --no-restore --logger 'trx;LogFileName=core-tests.trx' --results-directory build/test-results
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }
