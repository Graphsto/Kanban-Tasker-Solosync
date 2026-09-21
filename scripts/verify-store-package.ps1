param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/distribution.ps1"
$settings = Get-DistributionSettings -Channel Store
if (-not ('PackageAssemblyInspection' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'PackageAssemblyInspection.cs') }
function Read-ZipBytes($Archive, [string]$Name, [long]$Limit = 16777216) {
    $entry = $Archive.GetEntry($Name)
    if (-not $entry -or $entry.Length -gt $Limit) { throw "Missing or oversized package entry: $Name" }
    $inputStream = $entry.Open(); $memory = [IO.MemoryStream]::new()
    try { $inputStream.CopyTo($memory); return ,$memory.ToArray() } finally { $inputStream.Dispose(); $memory.Dispose() }
}
function Read-ZipText($Archive, [string]$Name) { [Text.Encoding]::UTF8.GetString((Read-ZipBytes $Archive $Name)) }
$report = Get-Content -LiteralPath (Join-Path $Directory 'build-report.json') -Raw | ConvertFrom-Json
if ($report.schemaVersion -ne 1 -or $report.channel -ne 'Store' -or $report.productVersion -ne $settings.Version -or
    $report.storePackageVersion -ne $settings.KanbanPackageVersion -or $report.identity -ne $settings.KanbanIdentity -or
    $report.publisher -ne $settings.KanbanPublisher -or $report.packageFamilyName -ne $settings.KanbanPackageFamilyName -or
    $report.sdk -ne $settings.NETCoreSdkVersion -or $report.commit -notmatch '^[a-f0-9]{40}$') { throw 'Build report does not match the Store profile.' }
$expectedName = "KanbanTasker-Store-$($settings.KanbanPackageVersion).msixbundle"
if ($report.bundle -ne $expectedName) { throw 'Unexpected bundle filename.' }
$path = Join-Path $Directory $expectedName
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing Store bundle.' }
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $report.sha256) { throw 'Bundle checksum mismatch.' }
$checks = @()
$bundle = [IO.Compression.ZipFile]::OpenRead($path)
try {
    if ($bundle.GetEntry('AppxSignature.p7x')) { throw 'The submission bundle must not carry the local test signature.' }
    [xml]$manifest = Read-ZipText $bundle 'AppxMetadata/AppxBundleManifest.xml'
    if ($manifest.Bundle.Identity.Name -ne $settings.KanbanIdentity -or $manifest.Bundle.Identity.Publisher -ne $settings.KanbanPublisher -or
        $manifest.Bundle.Identity.Version -ne $settings.KanbanPackageVersion) { throw 'Wrong bundle identity or version.' }
    $applications = @($manifest.Bundle.Packages.Package | Where-Object Type -eq 'application')
    if ($applications.Count -ne 2 -or (@($applications.Architecture | Sort-Object) -join ',') -ne 'arm64,x64') { throw 'The bundle must contain exactly x64 and ARM64 applications.' }
    foreach ($application in $applications) {
        $bytes = Read-ZipBytes $bundle $application.FileName 536870912
        $memory = [IO.MemoryStream]::new($bytes, $false)
        $package = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
        try {
            [xml]$app = Read-ZipText $package 'AppxManifest.xml'
            if ($app.Package.Identity.Name -ne $settings.KanbanIdentity -or $app.Package.Identity.Publisher -ne $settings.KanbanPublisher -or
                $app.Package.Identity.Version -ne $settings.KanbanPackageVersion -or $app.Package.Identity.ProcessorArchitecture -ne $application.Architecture -or
                $app.Package.Properties.DisplayName -ne $settings.KanbanDisplayName -or $app.Package.Properties.PublisherDisplayName -ne $settings.KanbanPublisherDisplayName) { throw 'Wrong app manifest metadata.' }
            if (($app.Package.Resources.Resource.Language | Sort-Object) -join ',' -ne 'de-DE,en-US,es-ES,fr-FR,it-IT') { throw 'Missing app languages.' }
            if ($package.GetEntry('AppxSignature.p7x') -or @($package.Entries | Where-Object FullName -match '\.(cer|pfx|p12|key)$').Count) { throw 'A signing certificate or signature leaked into the Store app.' }
            $metadata = [PackageAssemblyInspection]::Read((Read-ZipBytes $package 'KanbanTasker.dll'))
            if ($metadata.Version -ne $settings.Version -or $metadata.DistributionChannel -ne 'Store' -or
                $metadata.PackageIdentity -ne $settings.KanbanIdentity -or $metadata.PackagePublisher -ne $settings.KanbanPublisher -or
                $metadata.StoreId -ne $settings.KanbanStoreId -or $metadata.ProfileDirectory -eq 'KanbanTasker.Revived' -or
                $metadata.ContainsKey('Resource:KanbanTasker.PublisherCertificate')) { throw 'Wrong application distribution metadata or embedded local certificate.' }
            foreach ($language in @('en','de','es','fr','it')) {
                if (-not $metadata.ContainsKey("Resource:KanbanTasker.Localization.$language.json")) { throw "Missing embedded $language catalog." }
            }
            $runtime = Read-ZipText $package 'KanbanTasker.runtimeconfig.json' | ConvertFrom-Json
            if ($runtime.runtimeOptions.includedFrameworks.Count -lt 1 -or
                @($runtime.runtimeOptions.includedFrameworks | Where-Object version -ne '10.0.12').Count) { throw 'The package does not contain the approved .NET 10.0.12 runtime.' }
            if (-not (Read-ZipText $package 'LICENSE.txt').Contains('Copyright (c) 2019 hjohnson12') -or
                -not (Read-ZipText $package 'PRIVACY.md').Contains('Recovery')) { throw 'Missing license or privacy notice.' }
            if (-not $package.GetEntry('resources.pri')) { throw 'Missing XAML resource index.' }
            $inventory = @(Read-ZipText $package 'Notices/shipped-binaries.json' | ConvertFrom-Json)
            $binaries = @($package.Entries | Where-Object FullName -match '\.(exe|dll)$')
            if ($inventory.Count -ne $binaries.Count) { throw 'Incomplete shipped binary inventory.' }
            foreach ($binary in $binaries) {
                $record = @($inventory | Where-Object path -eq $binary.FullName)
                if ($record.Count -ne 1 -or $record[0].packages.Count -eq 0) { throw "Missing attribution for $($binary.FullName)." }
                $inputStream = $binary.Open()
                try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)) } finally { $inputStream.Dispose() }
                if ($hash -ne $record[0].sha256) { throw "Inventory checksum mismatch: $($binary.FullName)" }
            }
            $checks += [ordered]@{ architecture=$application.Architecture; productVersion=$metadata.Version; packageVersion=$settings.KanbanPackageVersion; binaries=$binaries.Count; success=$true }
        } finally { $package.Dispose(); $memory.Dispose() }
    }
} finally { $bundle.Dispose() }
$checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'verification.json') -Encoding utf8
$checks | Format-Table
