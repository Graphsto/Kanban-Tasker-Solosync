param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'build/legal' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packages = @{}
foreach ($project in @('KanbanTasker.Desktop','KanbanTasker.Setup')) {
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
        $target = Join-Path $OutputDirectory $relative
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -LiteralPath $spec.FullName -Destination $target
        $notices = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '(?i)license|notice' })
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
        $packages[$key] = [ordered]@{
            name = $candidate.Id; version = $candidate.Version
            licenseType = $license.type; license = $license.InnerText; licenseUrl = $licenseUrl.InnerText
            copyright = $copyright.InnerText
            noticeFiles = @($notices | ForEach-Object { "$relative/$($_.Name)" })
            nugetSpecification = "$relative/$($spec.Name)"
            scope = 'Restored build input; includes SDK, reference, host and runtime packages, not proof that every component ships.'
        }
    }
}
$inventory = @($packages.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Value })
$inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dependencies.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
Write-Host "$($inventory.Count) restored package records and supplied notices: $OutputDirectory"
