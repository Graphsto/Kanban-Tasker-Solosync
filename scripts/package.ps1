param(
    [ValidateSet('x64','arm64','all')][string]$Architecture = 'all',
    [string]$Version,
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/distribution.ps1"
$settings = Get-DistributionSettings -Channel Local -Version $Version
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
$makeAppx = Join-Path $sdk 'makeappx.exe'
$makePri = Join-Path $sdk 'makepri.exe'
$signTool = Join-Path $sdk 'signtool.exe'
if (-not (Test-Path -LiteralPath $makeAppx)) { throw 'Install Windows SDK 10.0.26100 with packaging tools.' }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or ($Version.Split('.') | Where-Object { [long]$_ -gt 65535 })) { throw 'Version must have four numeric components between 0 and 65535.' }
Push-Location $repo
try {
    $output = Join-Path $repo 'build/packages'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # The notice export includes Setup dependencies before the installer is published.
    # Restore them explicitly so packaging also works without a previous Setup build.
    dotnet restore src/KanbanTasker.Setup/KanbanTasker.Setup.csproj --locked-mode
    if ($LASTEXITCODE) { throw 'Installer dependency restore failed.' }
    if ($CertificateThumbprint) {
        $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
    } else {
        $certificate = Get-ChildItem Cert:/CurrentUser/My | Where-Object {
            $_.Subject -eq $settings.KanbanPublisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30)
        } | Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $certificate) {
            $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $settings.KanbanPublisher `
                -CertStoreLocation Cert:/CurrentUser/My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
                -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(3)
        }
    }
    if ($certificate.Subject -ne $settings.KanbanPublisher) { throw 'Signing certificate subject must match the local publisher.' }
    $certificatePath = Join-Path $output 'KanbanTasker.Local.cer'
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
    $architectures = if ($Architecture -eq 'all') { @('x64','arm64') } else { @($Architecture) }
    foreach ($arch in $architectures) {
        # A fresh staging directory prevents stale binaries from a previous SDK entering the package.
        $stage = Join-Path $repo ("build/staging/{0}/{1}" -f $arch, [Guid]::NewGuid().ToString('N'))
        dotnet publish src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj -c Release -r "win-$arch" --self-contained true `
            -p:Platform=$arch -p:RestoreLockedMode=true -p:KanbanMsixBuild=true -p:Version=$Version "-p:KanbanSigningCertificatePath=$certificatePath" -o $stage
        if ($LASTEXITCODE) { throw "Publish failed for $arch." }
        Assert-PackageResources -Stage $stage -Settings $settings -MakePri $makePri -LogDirectory $output -Architecture $arch
        Write-PackageManifest -Settings $settings -Architecture $arch -Destination (Join-Path $stage 'AppxManifest.xml')
        Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
        & "$PSScriptRoot/export-notices.ps1" -OutputDirectory (Join-Path $stage 'Notices')
        $package = Join-Path $output "KanbanTasker-$Version-$arch.msix"
        $packLog = Join-Path $output "pack-$arch.log"
        & $makeAppx pack /d $stage /p $package /o *> $packLog
        if ($LASTEXITCODE) { throw "MSIX validation/packing failed for $arch." }
        & $signTool sign /s My /sha1 $certificate.Thumbprint /fd SHA256 $package
        if ($LASTEXITCODE) { throw "Signing failed for $arch." }
        Get-FileHash -LiteralPath $package -Algorithm SHA256
        & "$PSScriptRoot/package-installer.ps1" -Architecture $arch -Version $Version
    }
    Write-Host "Packages: $output"
    Write-Host 'The public certificate is included. Private key remains in your Windows certificate store.'
    Write-Host 'Certificate trust and package installation are not performed by this script.'
} finally { Pop-Location }
