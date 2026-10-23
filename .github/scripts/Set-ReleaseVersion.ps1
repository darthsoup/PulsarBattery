param(
    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$Tag,
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot '../../PulsarBattery'),
    [string]$OutputPath = $env:GITHUB_OUTPUT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$match = [regex]::Match($Tag, '\Av(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)\z')
if (-not $match.Success) {
    throw 'Release tags must use vMAJOR.MINOR.PATCH with no leading zeros or suffix.'
}

foreach ($name in 'major', 'minor', 'patch') {
    $component = 0
    if (-not [int]::TryParse($match.Groups[$name].Value, [ref]$component) -or $component -gt 65534) {
        throw 'Release version components must be between 0 and 65534.'
    }
}

$version = $Tag.Substring(1)
$windowsVersion = "$version.0"
$appPath = (Resolve-Path -LiteralPath (Join-Path $ProjectDirectory 'app.manifest')).Path
$packagePath = (Resolve-Path -LiteralPath (Join-Path $ProjectDirectory 'Package.appxmanifest')).Path

$app = New-Object System.Xml.XmlDocument
$app.PreserveWhitespace = $true
$app.XmlResolver = $null
$app.Load($appPath)
$appNamespaces = New-Object System.Xml.XmlNamespaceManager($app.NameTable)
$appNamespaces.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$appIdentity = $app.SelectSingleNode('/asm:assembly/asm:assemblyIdentity', $appNamespaces)
if ($null -eq $appIdentity -or -not $appIdentity.HasAttribute('version')) {
    throw 'app.manifest must contain an assemblyIdentity with a version attribute.'
}

$package = New-Object System.Xml.XmlDocument
$package.PreserveWhitespace = $true
$package.XmlResolver = $null
$package.Load($packagePath)
$packageNamespaces = New-Object System.Xml.XmlNamespaceManager($package.NameTable)
$packageNamespaces.AddNamespace('pkg', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$packageIdentity = $package.SelectSingleNode('/pkg:Package/pkg:Identity', $packageNamespaces)
if ($null -eq $packageIdentity -or -not $packageIdentity.HasAttribute('Version')) {
    throw 'Package.appxmanifest must contain an Identity with a Version attribute.'
}

$appIdentity.SetAttribute('version', $windowsVersion)
$packageIdentity.SetAttribute('Version', $windowsVersion)
$app.Save($appPath)
$package.Save($packagePath)

if (-not [string]::IsNullOrEmpty($OutputPath)) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $outputs = "version=$version" + [Environment]::NewLine + "windows_version=$windowsVersion" + [Environment]::NewLine
    [System.IO.File]::AppendAllText($OutputPath, $outputs, $encoding)
}

Write-Host "Release version: $version (Windows: $windowsVersion)"
