param(
    [string]$DotnetPath,
    [switch]$Offline,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
if (!$DotnetPath) {
    $DotnetPath = Join-Path $workspace '.tools\dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\packages'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$program = Join-Path $output 'AITaskbar.exe'
if (Get-Process AITaskbar -ErrorAction SilentlyContinue | Where-Object Path -eq $program) {
    throw 'Exit AITaskbar before rebuilding this output directory.'
}

$restore = @()
if ($Offline) { $restore = @('--source', (Join-Path $workspace '.tools\feed'), '-p:NuGetAudit=false') }
Push-Location $workspace
try {
    & $DotnetPath publish AITaskbar.csproj -c Release -p:Platform=x64 -o $output --disable-build-servers @restore
    if ($LASTEXITCODE -ne 0) { throw 'AITaskbar build failed.' }

    $companions = Join-Path $output 'BrowserCompanions'
    $hostOutput = Join-Path $companions 'Host'
    & $DotnetPath publish 'extensions/EdgeRecent/NativeHost/NativeHost.csproj' -c Release -o $hostOutput --disable-build-servers @restore
    if ($LASTEXITCODE -ne 0) { throw 'Browser native host build failed.' }

    foreach ($browser in 'Edge', 'Chrome') {
        $extensionSource = Join-Path $workspace "extensions/${browser}Recent/Browser"
        $extensionOutput = Join-Path $companions $browser
        New-Item -ItemType Directory -Path $extensionOutput -Force | Out-Null
        Copy-Item -Path (Join-Path $extensionSource '*') -Destination $extensionOutput
    }
    Copy-Item -LiteralPath 'extensions/EdgeRecent/Browser/worker.js' -Destination (Join-Path $companions 'Chrome')
}
finally { Pop-Location }

foreach ($file in @('AITaskbar.exe', 'resources.pri', 'Assets\AITaskbar-Ring.ico', 'Assets\AIEnter.png', 'Assets\Folder.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
        throw "Publish output is incomplete: $file"
    }
}
$obsoleteLibrary = Join-Path $output 'native\ExplorerTAP.dll'
if (Test-Path -LiteralPath $obsoleteLibrary -PathType Leaf) {
    Remove-Item -LiteralPath $obsoleteLibrary
}
Write-Output "Built: $program"
