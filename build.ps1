param(
    [ValidateSet('Debug', 'Release', 'Prod', 'All')]
    [string[]]$Target = @('All'),

    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64', 'All')]
    [string[]]$Runtime = @('win-x64'),

    [string]$VersionOverride,

    [int]$BuildNumber,

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot 'src\SqlManager\SqlManager.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$buildNumberPath = Join-Path $repoRoot 'build-number.txt'
$versionPrefix = '1.0'
$explicitVersionProvided = $PSBoundParameters.ContainsKey('VersionOverride')
$explicitBuildNumberProvided = $PSBoundParameters.ContainsKey('BuildNumber')

if ($explicitVersionProvided -and $explicitBuildNumberProvided) {
    throw 'Specify either VersionOverride or BuildNumber, not both.'
}

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

if ($Target -contains 'All') {
    $Target = @('Debug', 'Release', 'Prod')
}

$Target = $Target | Select-Object -Unique

if ($Runtime -contains 'All') {
    $Runtime = @(
        'win-x64',
        'win-arm64',
        'osx-x64',
        'osx-arm64',
        'linux-x64',
        'linux-arm64',
        'linux-musl-x64',
        'linux-musl-arm64'
    )
}

$Runtime = $Runtime | Select-Object -Unique

function Get-NextBuildNumber {
    $currentBuildNumber = 0

    if (Test-Path $buildNumberPath) {
        $rawValue = (Get-Content -Path $buildNumberPath -Raw).Trim()
        if (-not [int]::TryParse($rawValue, [ref]$currentBuildNumber)) {
            throw "Invalid build number in ${buildNumberPath}: '$rawValue'"
        }
    }

    $nextBuildNumber = $currentBuildNumber + 1
    Set-Content -Path $buildNumberPath -Value $nextBuildNumber -NoNewline
    return $nextBuildNumber
}

function Resolve-BuildNumber {
    if ($explicitBuildNumberProvided) {
        return $BuildNumber
    }

    return Get-NextBuildNumber
}

function Resolve-Version {
    if ($explicitVersionProvided) {
        return $VersionOverride
    }

    return "$versionPrefix.$script:BuildNumber"
}

function Get-NormalizedVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionText
    )

    try {
        return [version]$VersionText
    }
    catch {
        throw "Invalid version '$VersionText'. Use a numeric version like 1.0.0 or 1.0.0.1."
    }
}

function Get-FileVersionString {
    param(
        [Parameter(Mandatory = $true)]
        [version]$VersionObject
    )

    $build = if ($VersionObject.Build -ge 0) { $VersionObject.Build } else { 0 }
    $revision = if ($VersionObject.Revision -ge 0) { $VersionObject.Revision } else { 0 }

    return "$($VersionObject.Major).$($VersionObject.Minor).$build.$revision"
}

if (-not $explicitVersionProvided) {
    $script:BuildNumber = Resolve-BuildNumber
}

$script:Version = Resolve-Version
$script:NormalizedVersion = Get-NormalizedVersion -VersionText $script:Version
$script:FileVersion = Get-FileVersionString -VersionObject $script:NormalizedVersion

if (-not $explicitVersionProvided) {
    Write-Host "Build number: $script:BuildNumber" -ForegroundColor Green
}
Write-Host "Version: $script:Version" -ForegroundColor Green

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $versionArguments = @(
        "/p:Version=$script:Version",
        "/p:FileVersion=$script:FileVersion",
        "/p:InformationalVersion=$script:Version"
    )

    if (-not $explicitVersionProvided) {
        $versionArguments += "/p:BuildNumber=$script:BuildNumber"
    }

    Write-Host "> dotnet $($Arguments -join ' ') $($versionArguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments @versionArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

function Get-OutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return Join-Path $artifactsRoot $Name.ToLowerInvariant()
}

function Build-Debug {
    $outputPath = Get-OutputPath 'debug'
    Invoke-DotNet -Arguments @(
        'build',
        $projectPath,
        '--configuration', 'Debug',
        '--output', $outputPath
    )
}

function Build-Release {
    $outputPath = Get-OutputPath 'release'
    Invoke-DotNet -Arguments @(
        'build',
        $projectPath,
        '--configuration', 'Release',
        '--output', $outputPath
    )
}

function Build-Prod {
    $prodRoot = Get-OutputPath 'prod'

    foreach ($currentRuntime in $Runtime) {
        $outputPath = Join-Path $prodRoot $currentRuntime
        Invoke-DotNet -Arguments @(
            'publish',
            $projectPath,
            '--configuration', 'Release',
            '--runtime', $currentRuntime,
            '--self-contained', 'true',
            '--output', $outputPath
        )
    }
}

if ($Clean -and (Test-Path $artifactsRoot)) {
    Write-Host "Removing existing artifacts: $artifactsRoot" -ForegroundColor Yellow
    Remove-Item -Path $artifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

foreach ($currentTarget in $Target) {
    switch ($currentTarget) {
        'Debug' {
            Write-Host 'Building Debug output...' -ForegroundColor Green
            Build-Debug
        }
        'Release' {
            Write-Host 'Building Release output...' -ForegroundColor Green
            Build-Release
        }
        'Prod' {
            Write-Host "Publishing production output for runtimes: $($Runtime -join ', ')..." -ForegroundColor Green
            Build-Prod
        }
    }
}

Write-Host ''
Write-Host 'Build outputs:' -ForegroundColor Green
foreach ($currentTarget in $Target) {
    if ($currentTarget -eq 'Prod') {
        foreach ($currentRuntime in $Runtime) {
            Write-Host "- $(Join-Path (Join-Path $artifactsRoot 'prod') $currentRuntime)"
        }
        continue
    }

    $folderName = $currentTarget.ToLowerInvariant()
    Write-Host "- $(Join-Path $artifactsRoot $folderName)"
}
Write-Host "Version: $script:Version"