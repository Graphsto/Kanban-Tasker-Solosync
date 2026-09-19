param(
    [string]$PackagePath,
    [switch]$CheckOnly,
    [switch]$CertificateOnly,
    [string]$ExpectedThumbprint
)
$ErrorActionPreference = 'Stop'
try {
    if (-not $PackagePath) {
        $packageDirectory = $PSScriptRoot
        if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory 'KanbanTasker.Local.cer'))) {
            $packageDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'build/packages'
        }
        $architecture = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') { 'arm64' } else { 'x64' }
        $package = Get-ChildItem -LiteralPath $packageDirectory -Filter "KanbanTasker-*-$architecture.msix" |
            Sort-Object { [version]($_.BaseName.Split('-')[1]) } -Descending | Select-Object -First 1
        if (-not $package) { throw "No $architecture package found in $packageDirectory. Run scripts/package.ps1 first." }
        $PackagePath = $package.FullName
    }
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
    $certificatePath = Join-Path (Split-Path $PackagePath -Parent) 'KanbanTasker.Local.cer'
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if ($certificate.Subject -ne 'CN=KanbanTasker.Local' -or -not $signature.SignerCertificate -or
        [Convert]::ToBase64String($signature.SignerCertificate.RawData) -ne [Convert]::ToBase64String($certificate.RawData)) {
        throw 'The package signature does not match the supplied KanbanTasker.Local.cer. No certificate was trusted.'
    }
    if ($ExpectedThumbprint -and $certificate.Thumbprint -ne $ExpectedThumbprint) {
        throw 'The signing certificate changed before elevation. No certificate was trusted.'
    }
    if ($signature.Status -in @('HashMismatch', 'NotSigned') -or (Get-Date) -lt $certificate.NotBefore -or (Get-Date) -gt $certificate.NotAfter) {
        throw 'The package signature is damaged, missing, or its certificate is outside its validity period.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry) { throw 'The package has no MSIX manifest.' }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.Package.Identity.Name -ne 'KanbanTasker.Revived' -or $manifest.Package.Identity.Publisher -ne $certificate.Subject) {
            throw 'The package identity or publisher is not the expected Kanban Tasker local edition.'
        }
    } finally { $archive.Dispose() }

    $trustPath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    Write-Host "Package: $PackagePath"
    Write-Host "Publisher: $($certificate.Subject)"
    Write-Host "Certificate thumbprint: $($certificate.Thumbprint)"
    Write-Host "Trusted on this computer: $(Test-Path -LiteralPath $trustPath)"
    if ($CheckOnly) { exit 0 }

    if (-not (Test-Path -LiteralPath $trustPath)) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        } elseif ($CertificateOnly) {
            throw 'Administrator rights are required to trust the local signing certificate.'
        } else {
            Write-Host 'Approve the Windows administrator prompt to trust this matching public certificate in Local Computer / Trusted People.'
            # Only certificate trust is elevated. App installation stays with the original user,
            # including when different administrator credentials are used in the UAC prompt.
            $arguments = '-NoProfile -NonInteractive -File "{0}" -PackagePath "{1}" -CertificateOnly -ExpectedThumbprint "{2}"' -f $PSCommandPath, $PackagePath, $certificate.Thumbprint
            # Keep the host that is already allowed to run this script. Windows
            # PowerShell and PowerShell 7 can have different execution policies.
            $powershell = Join-Path $PSHOME 'pwsh.exe'
            if (-not (Test-Path -LiteralPath $powershell)) { $powershell = Join-Path $PSHOME 'powershell.exe' }
            $process = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trustPath)) {
                throw "Certificate trust was not completed (helper exit code: $($process.ExitCode)). Approve the administrator prompt, or ask your administrator to import the supplied certificate into Local Computer / Trusted People."
            }
        }
    }
    if ($CertificateOnly) { exit 0 }
    Add-AppxPackage -Path $PackagePath -ErrorAction Stop
    Get-AppxPackage -Name 'KanbanTasker.Revived' | Select-Object Name, Version, Status, PackageFamilyName
    Write-Host 'Installation completed. Open Kanban Tasker from the Start menu.'
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
