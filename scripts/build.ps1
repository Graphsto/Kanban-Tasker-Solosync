param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [ValidateSet('x64','arm64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet restore src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj --locked-mode
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj --no-restore -c $Configuration -r "win-$Architecture" -p:Platform=$Architecture
    if ($LASTEXITCODE) { throw 'App build failed.' }
    & "$PSScriptRoot/test.ps1"
} finally { Pop-Location }
