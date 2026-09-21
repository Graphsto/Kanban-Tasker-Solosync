# Shared by local and Store packaging. Query MSBuild so the app and scripts use
# exactly the same evaluated profile, including version overrides.
function Get-DistributionSettings {
    param([ValidateSet('Local','Store')][string]$Channel = 'Local', [string]$Version)
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $arguments = @('msbuild', (Join-Path $repoRoot 'src/KanbanTasker.Desktop/KanbanTasker.Desktop.csproj'), '-nologo',
        "-p:KanbanChannel=$Channel", '-getProperty:Version,StorePackageVersion,KanbanChannel,KanbanIdentity,KanbanPublisher,KanbanPublisherDisplayName,KanbanDisplayName,KanbanPackageVersion,KanbanPackageFamilyName,KanbanStoreId,KanbanPrivacyUrl,ProjectAssetsFile,NETCoreSdkVersion')
    if ($Version) { $arguments += "-p:Version=$Version" }
    $json = & dotnet @arguments
    if ($LASTEXITCODE) { throw 'Cannot evaluate distribution settings.' }
    $settings = ($json -join "`n" | ConvertFrom-Json).Properties
    foreach ($value in @($settings.Version, $settings.KanbanPackageVersion)) {
        if ($value -notmatch '^\d+\.\d+\.\d+\.\d+$' -or ($value.Split('.') | Where-Object { [long]$_ -gt 65535 })) {
            throw 'Versions must have four components between 0 and 65535.'
        }
    }
    if ($Channel -eq 'Store' -and ([version]$settings.KanbanPackageVersion).Revision -ne 0) { throw 'The fourth Store package version component must be zero.' }
    if ($Channel -eq 'Store' -and ([version]$settings.KanbanPackageVersion).Major -eq 0) { throw 'Store package major version must be nonzero.' }
    return $settings
}

function Write-PackageManifest {
    param($Settings, [string]$Architecture, [string]$Destination)
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Package.appxmanifest.template') -Raw
    $values = @{
        '__ARCH__'=$Architecture; '__VERSION__'=$Settings.KanbanPackageVersion; '__IDENTITY__'=$Settings.KanbanIdentity
        '__PUBLISHER__'=$Settings.KanbanPublisher; '__DISPLAY_NAME__'=$Settings.KanbanDisplayName
        '__PUBLISHER_DISPLAY_NAME__'=$Settings.KanbanPublisherDisplayName
    }
    foreach ($key in $values.Keys) { $manifest = $manifest.Replace($key, [Security.SecurityElement]::Escape($values[$key])) }
    Set-Content -LiteralPath $Destination -Value $manifest -Encoding utf8
}

function Assert-PackageResources {
    param([string]$Stage, $Settings, [string]$MakePri, [string]$LogDirectory, [string]$Architecture)
    $pri = Join-Path $Stage 'resources.pri'
    if (-not (Test-Path -LiteralPath $pri)) { throw 'The package has no XAML resource index.' }
    $dump = Join-Path $LogDirectory "resources-$Architecture.xml"
    & $MakePri dump /if $pri /of $dump /o *> (Join-Path $LogDirectory "resources-$Architecture.log")
    if ($LASTEXITCODE) { throw 'Resource index validation failed.' }
    [xml]$resourceIndex = Get-Content -LiteralPath $dump -Raw
    $resources = $resourceIndex.PriInfo.ResourceMap | Where-Object name -eq $Settings.KanbanIdentity
    foreach ($name in @('App.xbf','MainWindow.xbf','TaskEditorView.xbf')) {
        if (-not $resources -or -not $resources.SelectSingleNode("ResourceMapSubtree[@name='Files']/NamedResource[@name='$name']/Candidate[@type='EmbeddedData']")) {
            throw "Missing $name under the expected resource identity $($Settings.KanbanIdentity)."
        }
    }
}
