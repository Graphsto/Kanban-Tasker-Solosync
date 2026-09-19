param([string]$Version)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
$output = Join-Path $repo 'build/packages'
$qa = Join-Path $repo 'build/qa'
New-Item -ItemType Directory -Path $qa -Force | Out-Null
$checks = @()
foreach ($arch in @('x64','arm64')) {
    foreach ($kind in @('msix','exe')) {
        $name = if ($kind -eq 'msix') { "KanbanTasker-$Version-$arch.msix" } else { "KanbanTasker-Setup-$Version-$arch.exe" }
        $path = Join-Path $output $name
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid') { throw "Invalid signature: $name" }
        $record = [ordered]@{ File=$name; Signature=$signature.Status.ToString(); Version=$Version; Architecture=$arch; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        if ($kind -eq 'msix') {
            $zip = [IO.Compression.ZipFile]::OpenRead($path)
            try {
                function Read-Entry([string]$entryName) {
                    $entry = $zip.GetEntry($entryName)
                    if (-not $entry) { throw "Missing $entryName in $name" }
                    $reader = [IO.StreamReader]::new($entry.Open())
                    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
                }
                [xml]$manifest = Read-Entry 'AppxManifest.xml'
                if ($manifest.Package.Identity.Version -ne $Version -or $manifest.Package.Identity.ProcessorArchitecture -ne $arch) { throw "Wrong package metadata: $name" }
                $languages = @($manifest.Package.Resources.Resource | ForEach-Object { $_.Language })
                if (($languages | Sort-Object) -join ',' -ne 'de-DE,en-US,es-ES,fr-FR,it-IT') { throw "Missing languages: $name" }
                $assemblyText = Read-Entry 'KanbanTasker.dll'
                foreach ($code in @('en','de','es','fr','it')) {
                    if (-not $assemblyText.Contains("KanbanTasker.Localization.$code.json")) { throw "Missing catalog $code" }
                }
                if ($assemblyText.Contains('SmokeProfile') -or $assemblyText.Contains('RunDesktopSmokeTestsAsync')) { throw "Test harness in $name" }
                if (-not (Read-Entry 'LICENSE.txt').Contains('Copyright (c) 2019 hjohnson12')) { throw 'Missing original license notice.' }
                $inventory = Read-Entry 'Notices/dependencies.json' | ConvertFrom-Json
                if ($inventory.Count -lt 1) { throw 'Missing dependency inventory.' }
                $record.NoticeRecords = $inventory.Count
                $record.Languages = $languages
            } finally { $zip.Dispose() }
        } else {
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne $Version) { throw "Wrong setup version: $name" }
            $reader = [IO.StreamReader]::new($path, [Text.Encoding]::Latin1)
            try {
                $characters = [char[]]::new(65536); $tail = ''
                while (($count = $reader.ReadBlock($characters, 0, $characters.Length)) -gt 0) {
                    $chunk = $tail + [string]::new($characters, 0, $count)
                    if ($chunk.Contains('RunSmokeTestsAsync')) { throw "Test harness in $name" }
                    $tail = $chunk.Substring([Math]::Max(0, $chunk.Length - 64))
                }
            } finally { $reader.Dispose() }
        }
        $record.TestHarnessExcluded = $true
        $checks += [pscustomobject]$record
    }
}
$checks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $qa "package-verification-$Version.json") -Encoding utf8
$checks | Format-Table File,Signature,Version,NoticeRecords
$installer = Join-Path $output "KanbanTasker-Setup-$Version-x64.exe"
$log = Join-Path $qa "payload-$Version.log"
$process = Start-Process -FilePath $installer -ArgumentList '--verify-only' -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $qa "payload-$Version-error.log")
if (-not $process.WaitForExit(60000)) { throw 'Payload verification did not finish within 60 seconds.' }
if ($process.ExitCode -ne 0) { throw "Payload verification failed: $($process.ExitCode)" }
Get-Content -LiteralPath $log
