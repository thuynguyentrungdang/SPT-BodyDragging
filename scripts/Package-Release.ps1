[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

# Packages the built plugin + Fika bridge into dist\BodyDragging-<version>.zip (SPT-root layout).
# Build first: dotnet build SPT-BodyDragging.slnx -c Release -p:SptRoot=<SPT> -p:SkipDeploy=true -p:SkipPackage=true
$repo = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $repo 'SPT-BodyDragging\SPT-BodyDragging.csproj'
$version = @(([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> in $csproj" }

$main = Join-Path $repo "SPT-BodyDragging\bin\$Configuration\netstandard2.1\BodyDragging.dll"
$bridge = Join-Path $repo "BodyDragFika\bin\$Configuration\netstandard2.1\BodyDragFika.dll"
foreach ($file in $main, $bridge) { if (-not (Test-Path $file)) { throw "Missing $file - build first." } }

$dist = Join-Path $repo 'dist'
$name = "BodyDragging-$version"
$root = Join-Path $dist $name
$plugin = Join-Path $root 'BepInEx\plugins\kobethuy-BodyDragging'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item $main, $bridge, (Join-Path $repo 'LICENSE'), (Join-Path $repo 'NOTICE') $plugin

$zip = Join-Path $dist "$name.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Zip written by hand: Windows PowerShell's Compress-Archive stores backslash paths that some extractors mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $root -Recurse -File) {
        $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Write-Output "version=$version"
Write-Output "zip=$zip"
