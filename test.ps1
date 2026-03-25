param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot 'sql_manager.sln'

if (-not (Test-Path $solutionPath)) {
	throw "Solution file not found: $solutionPath"
}

function Invoke-DotNet {
	param(
		[Parameter(Mandatory = $true)]
		[string[]]$Arguments
	)

	Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
	& dotnet @Arguments
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet command failed with exit code $LASTEXITCODE"
	}
}

function Get-TestProjects {
	$projectFiles = Get-ChildItem -Path $repoRoot -Filter '*.csproj' -Recurse -File |
		Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

	foreach ($projectFile in $projectFiles) {
		$projectContents = Get-Content -Path $projectFile.FullName -Raw

		if (
			$projectFile.BaseName -match '(?:\.|-)Tests?$' -or
			$projectContents -match '<IsTestProject>\s*true\s*</IsTestProject>' -or
			$projectContents -match 'Microsoft\.NET\.Test\.Sdk'
		) {
			$projectFile
		}
	}
}

Invoke-DotNet -Arguments @(
	'restore',
	$solutionPath
)

$testProjects = @(Get-TestProjects | Sort-Object -Property FullName -Unique)

if ($testProjects.Count -eq 0) {
	Write-Host 'No test projects found. Running solution build validation instead.' -ForegroundColor Yellow
	Invoke-DotNet -Arguments @(
		'build',
		$solutionPath,
		'--configuration', $Configuration,
		'--no-restore'
	)
	return
}

foreach ($testProject in $testProjects) {
	Write-Host "Running tests for $($testProject.FullName)..." -ForegroundColor Green
	Invoke-DotNet -Arguments @(
		'test',
		$testProject.FullName,
		'--configuration', $Configuration,
		'--no-restore'
	)
}
