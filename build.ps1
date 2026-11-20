param(
    [string]$DotnetPath,
    [switch]$Offline,
    [string]$WindhawkRoot
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

$nativeDll = Join-Path $workspace 'modules\LumaBar\native\ExplorerTAP.dll'
$expectedHash = 'C48F1A06168ACD7839269DDE0D15CCF5D09C702512B94DD6BA9A05DF1B153DC2'
if ((Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Unexpected ExplorerTAP.dll checksum.'
}

$restore = @()
if ($Offline) { $restore = @('--source', (Join-Path $workspace '.tools\feed'), '-p:NuGetAudit=false') }
Push-Location $workspace
try {
    & $DotnetPath publish AITaskbar.csproj -c Release -p:Platform=x64 -o $output --disable-build-servers @restore
    if ($LASTEXITCODE -ne 0) { throw 'AITaskbar build failed.' }
}
finally { Pop-Location }

foreach ($file in @('AITaskbar.exe', 'resources.pri', 'Assets\AITaskbar-Ring.ico', 'Assets\AIEnter.png', 'Assets\Folder.png', 'native\ExplorerTAP.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
        throw "Publish output is incomplete: $file"
    }
}
if ($WindhawkRoot) {
    & (Join-Path $workspace 'tools\BuildTaskbar.ps1') -RuntimeRoot $WindhawkRoot
}
Write-Output "Built: $program"
