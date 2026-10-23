Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$helper = Join-Path $PSScriptRoot 'Set-ReleaseVersion.ps1'
$sourceProject = Join-Path $PSScriptRoot '../../PulsarBattery'
$temporaryProject = Join-Path ([System.IO.Path]::GetTempPath()) ('PulsarBattery-release-version-' + [guid]::NewGuid().ToString('N'))
$outputPath = Join-Path $temporaryProject 'github-output.txt'
$appPath = Join-Path $temporaryProject 'app.manifest'
$packagePath = Join-Path $temporaryProject 'Package.appxmanifest'
$script:passed = 0

function Reset-Fixture {
    foreach ($name in 'app.manifest', 'Package.appxmanifest') {
        Copy-Item -LiteralPath (Join-Path $sourceProject $name) -Destination (Join-Path $temporaryProject $name) -Force
    }
    [System.IO.File]::WriteAllText($outputPath, "existing=preserved`n")
}

function Assert-Equal($Expected, $Actual, [string]$Description) {
    if ($Expected -cne $Actual) {
        throw "$Description`: expected '$Expected', got '$Actual'."
    }
}

function Assert-RejectedWithoutChanges([string]$Tag) {
    $before = @{}
    foreach ($path in $appPath, $packagePath, $outputPath) {
        $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $rejected = $false
    try {
        & $helper -Tag $Tag -ProjectDirectory $temporaryProject -OutputPath $outputPath
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Expected rejection for tag '$Tag'."
    }
    foreach ($path in $appPath, $packagePath, $outputPath) {
        Assert-Equal $before[$path] (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash "Rejected input changed $path"
    }
    $script:passed++
}

try {
    New-Item -ItemType Directory -Path $temporaryProject | Out-Null

    foreach ($tag in 'v0.4.1', 'v0.0.0', 'v1.2.3', 'v65534.65534.65534') {
        Reset-Fixture
        & $helper -Tag $tag -ProjectDirectory $temporaryProject -OutputPath $outputPath
        $version = $tag.Substring(1)
        $app = [xml](Get-Content -LiteralPath $appPath -Raw)
        $package = [xml](Get-Content -LiteralPath $packagePath -Raw)
        Assert-Equal "$version.0" $app.assembly.assemblyIdentity.version 'Assembly manifest version'
        Assert-Equal "$version.0" $package.Package.Identity.Version 'Package manifest version'
        $expectedOutput = "existing=preserved`n" + "version=$version" + [Environment]::NewLine + "windows_version=$version.0" + [Environment]::NewLine
        Assert-Equal $expectedOutput ([System.IO.File]::ReadAllText($outputPath)) 'Workflow outputs'
        $script:passed++
    }

    foreach ($tag in @(
        '', 'v', '1.2.3', 'V1.2.3', 'v1.2', 'v1.2.3.4', 'v1.2.3.4.5',
        'v01.2.3', 'v1.02.3', 'v1.2.03', 'v1.2.3-beta.1', 'v1.2.3+build',
        'v65535.0.0', 'v0.65535.0', 'v0.0.65535', 'v999999999999999999999.2.3',
        'v-1.2.3', 'v1.-2.3', 'v1.2.-3', 'v1.2.3 ', ' v1.2.3', "v1.2.3`n",
        "v1.2.3'", 'v1.2.3;throw', 'v1.2.3$(throw)', ('v' + [char]0x0661 + '.2.3')
    )) {
        Reset-Fixture
        Assert-RejectedWithoutChanges $tag
    }

    Reset-Fixture
    $package = [xml](Get-Content -LiteralPath $packagePath -Raw)
    $package.Package.RemoveChild($package.Package.Identity) | Out-Null
    $package.Save($packagePath)
    Assert-RejectedWithoutChanges 'v1.2.3'

    Reset-Fixture
    $app = [xml](Get-Content -LiteralPath $appPath -Raw)
    $app.assembly.assemblyIdentity.RemoveAttribute('version')
    $app.Save($appPath)
    Assert-RejectedWithoutChanges 'v1.2.3'

    Reset-Fixture
    [System.IO.File]::WriteAllText($packagePath, '<Package>')
    Assert-RejectedWithoutChanges 'v1.2.3'

    Reset-Fixture
    $previousOutput = $env:GITHUB_OUTPUT
    try {
        $env:GITHUB_OUTPUT = $outputPath
        & $helper -Tag 'v0.4.1' -ProjectDirectory $temporaryProject
        $expectedOutput = "existing=preserved`n" + 'version=0.4.1' + [Environment]::NewLine + 'windows_version=0.4.1.0' + [Environment]::NewLine
        Assert-Equal $expectedOutput ([System.IO.File]::ReadAllText($outputPath)) 'Default GITHUB_OUTPUT path'
        $script:passed++
    }
    finally {
        $env:GITHUB_OUTPUT = $previousOutput
    }

    Write-Host "PASS: $script:passed release-version cases."
}
finally {
    if (Test-Path -LiteralPath $temporaryProject) {
        Remove-Item -LiteralPath $temporaryProject -Recurse -Force
    }
}
