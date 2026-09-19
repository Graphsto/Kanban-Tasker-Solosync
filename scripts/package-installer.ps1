param(
    [ValidateSet('x64','arm64','all')][string]$Architecture = 'all',
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or ($Version.Split('.') | Where-Object { [long]$_ -gt 65535 })) { throw 'Version must have four numeric components between 0 and 65535.' }
$output = Join-Path $repo 'build/packages'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL.txt') -Destination (Join-Path $output 'INSTALL.txt') -Force
$signTool = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64/signtool.exe'
$certificatePath = Join-Path $output 'KanbanTasker.Local.cer'
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$signer = Get-Item -LiteralPath "Cert:/CurrentUser/My/$($certificate.Thumbprint)"
if (-not $signer.HasPrivateKey) { throw 'The package signing key is needed to sign the installer.' }
$architectures = if ($Architecture -eq 'all') { @('x64','arm64') } else { @($Architecture) }
Push-Location $repo
try {
    foreach ($arch in $architectures) {
        $package = Join-Path $output "KanbanTasker-$Version-$arch.msix"
        $signature = Get-AuthenticodeSignature -LiteralPath $package
        if (-not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $signature.Status -in @('HashMismatch', 'NotSigned', 'NotSupported', 'Incompatible')) {
            throw "The $arch package does not have the expected valid signature. Build it with scripts/package.ps1 first."
        }
        $stage = Join-Path $repo ("build/setup-staging/{0}/{1}" -f $arch, [Guid]::NewGuid().ToString('N'))
        $payload = Join-Path $stage 'payload'
        $publish = Join-Path $stage 'publish'
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
        Copy-Item -LiteralPath $package -Destination (Join-Path $payload 'package.msix')
        Copy-Item -LiteralPath $certificatePath -Destination (Join-Path $payload 'certificate.cer')
        @{
            Version = $Version; Architecture = $arch
            PackageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
            CertificateSha256 = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash
            CertificateThumbprint = $certificate.Thumbprint
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'payload.json') -Encoding utf8
        dotnet publish src/KanbanTasker.Setup/KanbanTasker.Setup.csproj -c Release -r "win-$arch" --self-contained true `
            -p:Platform=$arch -p:RestoreLockedMode=true -p:SetupPayloadDirectory=$payload -p:Version=$Version -o $publish
        if ($LASTEXITCODE) { throw "Installer publish failed for $arch." }
        $installer = Join-Path $output "KanbanTasker-Setup-$Version-$arch.exe"
        $stagedInstaller = Join-Path $publish 'KanbanTasker.Setup.exe'
        # Sign before exposing the EXE in the output folder, where Explorer may be reading its icon.
        & $signTool sign /s My /sha1 $certificate.Thumbprint /fd SHA256 $stagedInstaller
        if ($LASTEXITCODE) { throw "Installer signing failed for $arch." }
        Copy-Item -LiteralPath $stagedInstaller -Destination $installer -Force
        Get-FileHash -LiteralPath $installer -Algorithm SHA256
    }
} finally { Pop-Location; $certificate.Dispose() }
