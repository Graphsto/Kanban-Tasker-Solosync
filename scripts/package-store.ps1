param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/distribution.ps1"
Push-Location $repo
try {
    $settings = Get-DistributionSettings -Channel Store
    $run = [Guid]::NewGuid().ToString('N')
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "build/store/$($settings.Version)/$run" }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Store output directory must be new; existing release files are never overwritten.' }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
    $makeAppx = Join-Path $sdk 'makeappx.exe'; $makePri = Join-Path $sdk 'makepri.exe'
    if (-not (Test-Path -LiteralPath $makeAppx)) { throw 'Windows SDK 10.0.26100 packaging tools are required.' }
    $bundleInput = Join-Path $repo "build/store-staging/$run/packages"
    New-Item -ItemType Directory -Path $bundleInput -Force | Out-Null
    foreach ($arch in @('x64','arm64')) {
        $stage = Join-Path $repo "build/store-staging/$run/$arch"
        dotnet publish src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj -c Release -r "win-$arch" --self-contained true `
            -p:Platform=$arch -p:KanbanChannel=Store -p:RestoreLockedMode=true -p:KanbanMsixBuild=true -o $stage
        if ($LASTEXITCODE) { throw "Store publish failed for $arch." }
        Assert-PackageResources -Stage $stage -Settings $settings -MakePri $makePri -LogDirectory $OutputDirectory -Architecture $arch
        Write-PackageManifest -Settings $settings -Architecture $arch -Destination (Join-Path $stage 'AppxManifest.xml')
        Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $repo 'PRIVACY.md') -Destination $stage
        & "$PSScriptRoot/export-notices.ps1" -Channel Store -OutputDirectory (Join-Path $stage 'Notices') -PayloadDirectory $stage
        $packageName = "KanbanTasker-Store-$($settings.KanbanPackageVersion)-$arch.msix"
        & $makeAppx pack /d $stage /p (Join-Path $bundleInput $packageName) /o *> (Join-Path $OutputDirectory "pack-$arch.log")
        if ($LASTEXITCODE) { throw "Store packaging failed for $arch." }
        Copy-Item -LiteralPath (Join-Path $stage 'Notices') -Destination (Join-Path $OutputDirectory "Notices-$arch") -Recurse
    }
    $bundleName = "KanbanTasker-Store-$($settings.KanbanPackageVersion).msixbundle"
    & $makeAppx bundle /d $bundleInput /p (Join-Path $OutputDirectory $bundleName) /bv $settings.KanbanPackageVersion /o *> (Join-Path $OutputDirectory 'bundle.log')
    if ($LASTEXITCODE) { throw 'Store bundle creation failed.' }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE) { throw 'Cannot determine source commit.' }
    $dirty = [bool](git status --porcelain --untracked-files=normal)
    $report = [ordered]@{
        schemaVersion=1; channel='Store'; productVersion=$settings.Version; storePackageVersion=$settings.KanbanPackageVersion
        commit=$commit; dirty=$dirty; sdk=$settings.NETCoreSdkVersion; identity=$settings.KanbanIdentity; publisher=$settings.KanbanPublisher
        packageFamilyName=$settings.KanbanPackageFamilyName; storeId=$settings.KanbanStoreId; architectures=@('x64','arm64')
        bundle=$bundleName; sha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $bundleName) -Algorithm SHA256).Hash
        signing='Unsigned submission bundle; Microsoft signs after certification'
        acceptance='Package checks only; see STORE.md for installed/WACK/device acceptance'
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-report.json') -Encoding utf8
    "$($report.sha256.ToLowerInvariant())  $bundleName" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
    & "$PSScriptRoot/verify-store-package.ps1" -Directory $OutputDirectory
    Write-Host "Store submission bundle and evidence: $OutputDirectory"
} finally { Pop-Location }
