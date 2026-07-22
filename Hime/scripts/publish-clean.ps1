[CmdletBinding()]
param(
    [switch]$IncludeLocalConfig
)

$ErrorActionPreference = "Stop"

$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory ".."))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $workspaceDirectory "publish"))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $publishRoot "Hime"))
$projectFile = Join-Path $projectDirectory "Hime.csproj"

$publishPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputDirectory.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a path outside the publish root: $outputDirectory"
}

if (Test-Path -LiteralPath $outputDirectory) {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Write-Host "Publishing the clean Hime package..."
$publishArguments = @(
    "publish",
    $projectFile,
    "-c", "Release",
    "--nologo",
    "-p:PublishProfile=CleanFolder",
    "--output", $outputDirectory
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Static-link import libraries are SDK artifacts and are not needed at runtime.
Get-ChildItem -LiteralPath $outputDirectory -Filter "*.lib" -File |
    Remove-Item -Force

$localConfig = Join-Path $projectDirectory "appsettings.Local.json"
if ($IncludeLocalConfig -and (Test-Path -LiteralPath $localConfig)) {
    Copy-Item -LiteralPath $localConfig -Destination $outputDirectory -Force
    Write-Warning "appsettings.Local.json was included. Do not share this directory because it contains local secrets."
}

$publishedFiles = Get-ChildItem -LiteralPath $outputDirectory -Recurse -File
$fileCount = $publishedFiles.Count
$totalBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
$totalMegabytes = [Math]::Round($totalBytes / 1MB, 1)

Write-Host ""
Write-Host "Publish directory: $outputDirectory"
Write-Host "Files: $fileCount"
Write-Host "Total size: $totalMegabytes MB"
Write-Host "Executable: $(Join-Path $outputDirectory 'Hime.exe')"
