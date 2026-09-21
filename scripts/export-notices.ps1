param([string]$OutputDirectory, [ValidateSet('Local','Store')][string]$Channel = 'Local', [string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdkLicense = Join-Path $repo 'packaging/licenses/Windows-SDK-license.rtf'
if ((Get-FileHash -LiteralPath $sdkLicense -Algorithm SHA256).Hash -ne 'DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62') {
    throw 'The preserved Windows SDK license differs from the documented upstream file.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'build/legal' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packages = @{}
$packageDirectories = @{}
$projects = if ($Channel -eq 'Store') { @('KanbanTasker.Desktop.Store') } else { @('KanbanTasker.Desktop','KanbanTasker.Setup') }
foreach ($project in $projects) {
    $assets = Get-Content -LiteralPath (Join-Path $repo "build/obj/$project/project.assets.json") -Raw | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name)
    $candidates = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object {
        [pscustomobject]@{ Id = $_.Name.Split('/')[0]; Version = $_.Name.Split('/')[1]; Path = $_.Value.path }
    })
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($download in $framework.downloadDependencies) {
            $version = $download.version.Trim('[',']').Split(',')[0].Trim()
            $candidates += [pscustomobject]@{ Id = $download.name; Version = $version; Path = "$($download.name.ToLowerInvariant())/$version" }
        }
    }
    foreach ($candidate in $candidates) {
        $key = "$($candidate.Id)/$($candidate.Version)"
        if ($packages.ContainsKey($key)) { continue }
        $directory = $folders | ForEach-Object { Join-Path $_ $candidate.Path } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $directory) { throw "Restore the solution first. Missing package: $key" }
        $spec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' | Select-Object -First 1
        [xml]$xml = Get-Content -LiteralPath $spec.FullName -Raw
        $license = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
        $licenseUrl = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="licenseUrl"]')
        $copyright = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="copyright"]')
        $relative = "$($candidate.Id)-$($candidate.Version)"
        $packageDirectories[$key] = $directory
        $target = Join-Path $OutputDirectory $relative
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -LiteralPath $spec.FullName -Destination $target
        $notices = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '(?i)license|notice' })
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
        $noticePaths = @($notices | ForEach-Object { "$relative/$($_.Name)" })
        if ($candidate.Id -in @('Microsoft.Windows.SDK.NET.Ref','Microsoft.Windows.SDK.BuildTools')) {
            Copy-Item -LiteralPath $sdkLicense -Destination $target
            $noticePaths += "$relative/Windows-SDK-license.rtf"
        }
        $packages[$key] = [ordered]@{
            name = $candidate.Id; version = $candidate.Version
            licenseType = $license.type; license = $license.InnerText; licenseUrl = $licenseUrl.InnerText
            copyright = $copyright.InnerText
            noticeFiles = $noticePaths
            nugetSpecification = "$relative/$($spec.Name)"
            scope = 'Restored build input; includes SDK, reference, host and runtime packages, not proof that every component ships.'
        }
    }
}
$inventory = @($packages.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Value })
$inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dependencies.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
if ($PayloadDirectory) {
    # Match the actual shipped PE files by content, rather than assuming every
    # restored package ships. Apphost is generated from the .NET host template.
    $byName = @{}
    foreach ($key in $packageDirectories.Keys) {
        foreach ($file in Get-ChildItem -LiteralPath $packageDirectories[$key] -Recurse -File | Where-Object Extension -in @('.dll','.exe')) {
            if (-not $byName.ContainsKey($file.Name)) { $byName[$file.Name] = [Collections.Generic.List[object]]::new() }
            $byName[$file.Name].Add([pscustomobject]@{ Package=$key; File=$file })
        }
    }
    $hashCache = @{}; $binaryInventory = @()
    foreach ($file in Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File | Where-Object Extension -in @('.dll','.exe')) {
        $relativePath = [IO.Path]::GetRelativePath($PayloadDirectory, $file.FullName).Replace('\','/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $matches = @()
        foreach ($candidate in $byName[$file.Name]) {
            if ($candidate.File.Length -ne $file.Length) { continue }
            if (-not $hashCache.ContainsKey($candidate.File.FullName)) { $hashCache[$candidate.File.FullName] = (Get-FileHash -LiteralPath $candidate.File.FullName -Algorithm SHA256).Hash }
            if ($hashCache[$candidate.File.FullName] -eq $hash) { $matches += $candidate.Package }
        }
        $kind = 'NuGet binary (SHA-256 match)'
        if ($file.Name -in @('KanbanTasker.dll','KanbanTasker.Core.dll')) { $kind = 'Project source, MIT'; $matches = @('KanbanTasker') }
        elseif ($file.Name -eq 'KanbanTasker.exe') { $kind = 'Project apphost generated by the pinned .NET SDK, MIT'; $matches = @('KanbanTasker','.NET apphost') }
        elseif ($matches.Count -eq 0) { throw "Unattributed shipped binary: $relativePath. Review its source and license before packaging." }
        foreach ($package in $matches) {
            if ($packages.ContainsKey($package) -and $packages[$package].noticeFiles.Count -eq 0) { throw "No license text for shipped package $package." }
        }
        $binaryInventory += [ordered]@{ path=$relativePath; sha256=$hash; source=$kind; packages=@($matches | Sort-Object -Unique) }
    }
    $binaryInventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'shipped-binaries.json') -Encoding utf8
}
Write-Host "$($inventory.Count) restored package records and supplied notices: $OutputDirectory"
