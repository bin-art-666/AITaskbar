param(
    [string]$Dotnet = 'dotnet',
    [string]$Source
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $root '.build'
$staging = Join-Path $output ('edge-recent-package-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'AITaskbar-EdgeRecent'
$nativeHost = Join-Path $package 'Host'
New-Item -ItemType Directory -Path $nativeHost -Force | Out-Null

$publish = @('publish', (Join-Path $PSScriptRoot 'NativeHost/NativeHost.csproj'),
    '-c', 'Release', '-o', $nativeHost)
if ($Source) { $publish += @('--source', $Source, '-p:NuGetAudit=false') }
& $Dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }

foreach ($item in 'Browser', 'Install.cmd', 'Uninstall.cmd', 'RegisterHost.ps1', 'README.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $item) -Destination $package -Recurse
}
$archive = Join-Path $output 'AITaskbar-EdgeRecent-win-x64.zip'
Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
Get-Item -LiteralPath $archive
