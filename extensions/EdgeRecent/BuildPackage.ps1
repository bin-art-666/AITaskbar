param(
    [string]$Dotnet = 'dotnet',
    [string]$Source,
    [ValidateSet('Edge', 'Chrome')]
    [string]$Browser = 'Edge'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $root '.build'
$staging = Join-Path $output ($Browser.ToLowerInvariant() + '-recent-package-' + [Guid]::NewGuid().ToString('N'))
$name = "AITaskbar-${Browser}Recent"
$package = Join-Path $staging $name
$nativeHost = Join-Path $package 'Host'
New-Item -ItemType Directory -Path $nativeHost -Force | Out-Null

$publish = @('publish', (Join-Path $PSScriptRoot 'NativeHost/NativeHost.csproj'),
    '-c', 'Release', '-o', $nativeHost)
if ($Source) { $publish += @('--source', $Source, '-p:NuGetAudit=false') }
& $Dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }

$content = if ($Browser -eq 'Chrome') { Join-Path $PSScriptRoot '../ChromeRecent' } else { $PSScriptRoot }
foreach ($item in 'Browser', 'Install.cmd', 'Uninstall.cmd', 'README.md') {
    Copy-Item -LiteralPath (Join-Path $content $item) -Destination $package -Recurse
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RegisterHost.ps1') -Destination $package
if ($Browser -eq 'Chrome') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Browser/worker.js') -Destination (Join-Path $package 'Browser')
}
$archive = Join-Path $output "$name-win-x64.zip"
Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
Get-Item -LiteralPath $archive
