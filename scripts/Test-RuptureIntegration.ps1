[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SptRoot, [Parameter(Mandatory=$true)][string]$RuptureDll,
      [string]$PhysXBackendDll, [string]$NativeBridge)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
& dotnet build (Join-Path $repo 'tests/BodyDragging.Tests.csproj') -c Release "-p:SptRoot=$SptRoot" "-p:RuptureDll=$RuptureDll" -p:SkipDeploy=true -p:SkipPackage=true --disable-build-servers -m:1 -v minimal
if ($LASTEXITCODE) { throw 'BodyDragging integration build failed.' }
$nativeArgs = @()
if ($PhysXBackendDll -or $NativeBridge) {
    if (!$PhysXBackendDll -or !$NativeBridge) { throw 'Specify both PhysXBackendDll and NativeBridge for the isolated physics fixture.' }
    $nativeArgs = @($PhysXBackendDll, $NativeBridge)
}
& dotnet (Join-Path $repo 'tests/bin/Release/net8.0/BodyDragging.Tests.dll') @nativeArgs
if ($LASTEXITCODE) { throw 'BodyDragging integration regressions failed.' }
Write-Output 'PASS: managed integration regressions. Live EFT/Fika acceptance remains separate.'
