param(
    [string]$DotnetPath,
    [switch]$Offline
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
$output = Join-Path $workspace 'dist'
$program = Join-Path $output 'AITaskbar.exe'
if (Get-Process AITaskbar -ErrorAction SilentlyContinue | Where-Object Path -eq $program) {
    throw 'Exit AITaskbar before rebuilding dist.'
}

$restore = @()
if ($Offline) { $restore = @('--source', (Join-Path $workspace '.tools\feed'), '-p:NuGetAudit=false') }
Push-Location $workspace
try {
    & $DotnetPath publish AITaskbar.csproj -c Release -p:Platform=x64 -o $output --disable-build-servers @restore
    if ($LASTEXITCODE -ne 0) { throw 'AITaskbar build failed.' }
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
