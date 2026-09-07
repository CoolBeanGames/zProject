[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$SourceRoot = "",
    [string]$PackageRoot = "",
    [string]$BundleName = ""
)

$ErrorActionPreference = "Stop"

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
} else {
    [IO.Path]::GetFullPath($SourceRoot)
}
$buildsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "builds"))
$packageRoot = if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    [IO.Path]::GetFullPath((Join-Path $buildsRoot "package"))
} else {
    [IO.Path]::GetFullPath($PackageRoot)
}
$bundleName = if ([string]::IsNullOrWhiteSpace($BundleName)) { "zProject-$Runtime" } else { $BundleName }
$bundleDirectory = [IO.Path]::GetFullPath((Join-Path $packageRoot $bundleName))
$archivePath = [IO.Path]::GetFullPath((Join-Path $packageRoot "$bundleName.zip"))
$allowedPrefix = $packageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

foreach ($target in @($bundleDirectory, $archivePath)) {
    if (-not $target.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a packaging path outside the requested package directory: $target"
    }
}

if (Test-Path -LiteralPath $bundleDirectory) {
    Remove-Item -LiteralPath $bundleDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $bundleDirectory -Force | Out-Null

$publishProperties = @(
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true",
    "--output", $bundleDirectory,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

$projects = @(
    "src/PromptQueue.App/PromptQueue.App.csproj",
    "src/PromptQueue.Operator/PromptQueue.Operator.csproj",
    "src/PromptQueue.Server/PromptQueue.Server.csproj"
)

foreach ($project in $projects) {
    & dotnet publish (Join-Path $repositoryRoot $project) @publishProperties
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project with exit code $LASTEXITCODE"
    }
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../release/README.txt") -Destination $bundleDirectory

$requiredFiles = @(
    "zProject.exe",
    "zProject_operator.exe",
    "zProject_server.exe",
    "ui/main.html",
    "zProject_webView.html",
    "zui/css/zui.css",
    "README.txt"
)

foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $bundleDirectory $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Packaged output is incomplete; missing $relativePath"
    }
}

Compress-Archive -LiteralPath $bundleDirectory -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Runnable bundle: $bundleDirectory"
Write-Host "GitHub asset:   $archivePath"
