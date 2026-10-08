[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,
      [Parameter(Mandatory=$true)][string]$RuptureRoot)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$rupture = [IO.Path]::GetFullPath($RuptureRoot)
$bodyBase = '7b5dc200d7c87d9f8630a39cfe3ecb95cde965f6'
$ruptureBase = '6bdb68dc7f30eadf6e7b5f74919c9bd6b66a3332'
$bodyHead = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot identify BodyDragging commit.' }
if ((& git -C $rupture rev-parse HEAD).Trim() -cne $ruptureBase) { throw 'Rupture source patch requires its audited baseline HEAD.' }
if ((& git -C $repo status --porcelain).Count) { throw 'Commit the BodyDragging changes before exporting format-patch.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stage = Join-Path $outputRoot ('bodydrag-source-patches-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'BodyDragging'),(Join-Path $stage 'Rupture'),(Join-Path $stage 'docs') -Force | Out-Null
& git -C $repo format-patch --binary --full-index --no-signature --output-directory (Join-Path $stage 'BodyDragging') "$bodyBase..$bodyHead"
if ($LASTEXITCODE) { throw 'BodyDragging format-patch failed.' }
$oldIndex = [Environment]::GetEnvironmentVariable('GIT_INDEX_FILE','Process')
$temporaryIndex = Join-Path $stage 'rupture-temporary.index'
try {
    $env:GIT_INDEX_FILE = $temporaryIndex
    & git -C $rupture read-tree $ruptureBase
    if ($LASTEXITCODE) { throw 'Cannot initialize temporary Rupture index.' }
    & git -C $rupture add --all
    if ($LASTEXITCODE) { throw 'Cannot snapshot Rupture working tree.' }
    & git -C $rupture diff --cached --binary --full-index "--output=$(Join-Path $stage 'Rupture/Rupture-ABI-V1.patch')" $ruptureBase
    if ($LASTEXITCODE) { throw 'Rupture patch export failed.' }
} finally {
    [Environment]::SetEnvironmentVariable('GIT_INDEX_FILE',$oldIndex,'Process')
    if (Test-Path -LiteralPath $temporaryIndex) { Remove-Item -LiteralPath $temporaryIndex }
}
Copy-Item -LiteralPath (Join-Path $repo 'docs/Rupture-Integration.md') -Destination (Join-Path $stage 'docs')
foreach ($name in @('BodyDragging-Rupture-ABI-Proposal.md','BodyDragging-Integration-Handoff.md')) {
    Copy-Item -LiteralPath (Join-Path $rupture "docs/$name") -Destination (Join-Path $stage 'docs')
}
@"
Source patches only: no deployment DLLs or release ZIPs are inside this archive.

BodyDragging: git am BodyDragging/0001-*.patch on base $bodyBase.
BodyDragging local branch: codex/rupture-drag-abi; commit $bodyHead.
Rupture: git apply Rupture/Rupture-ABI-V1.patch on base $ruptureBase.
The Rupture patch contains the complete ABI V1 work through version 1.1.6.
For in-game testing, use the separately supplied canonical Rupture 1.1.6 ZIP.

See docs/Rupture-Integration.md for build commands, executable checks and the
live solo/graphical-host/headless/client/observer acceptance matrix.
Live EFT/Fika behavior has not been tested by this patch export.
"@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding utf8
$manifest = Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
}
$manifest | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
$archive = Join-Path $outputRoot ("BodyDragging-Rupture-ABI-git-patches-$($bodyHead.Substring(0,12)).zip")
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal -Force
$resolvedStage = [IO.Path]::GetFullPath($stage)
if (!$resolvedStage.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedStage) -notlike 'bodydrag-source-patches-*') { throw 'Unsafe source staging cleanup target.' }
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Write-Output $archive
