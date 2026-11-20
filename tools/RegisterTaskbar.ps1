param([Parameter(Mandatory)][string]$RuntimeRoot, [switch]$Disable)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$RuntimeRoot = (Resolve-Path -LiteralPath $RuntimeRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $RuntimeRoot 'windhawk.exe') -PathType Leaf)) {
    throw 'Specify the root of a portable Windhawk installation.'
}
$modRoot = Join-Path $RuntimeRoot 'AppData\Engine\Mods'
$moduleIds = @('aitaskbar-ai', 'aitaskbar-halo')
if ($Disable) {
    foreach ($id in $moduleIds) {
        $ini = Join-Path $modRoot ($id + '.ini')
        if (Test-Path -LiteralPath $ini -PathType Leaf) {
            $config = [IO.File]::ReadAllText($ini) -replace '(?m)^Disabled=0', 'Disabled=1'
            [IO.File]::WriteAllText($ini, $config, [Text.Encoding]::Unicode)
        }
    }
    return
}

$program = Join-Path $workspace 'dist\AITaskbar.exe'
$buildRoot = Join-Path $workspace '.build\taskbar'
$compilerBin = Join-Path $RuntimeRoot 'Compiler\x86_64-w64-mingw32\bin'
$dependencies = @(
    @{ Source = 'libc++.dll'; Target = 'libc++.whl' },
    @{ Source = 'libunwind.dll'; Target = 'libunwind.whl' },
    @{ Source = 'windhawk-mod-shim.dll'; Target = 'windhawk-mod-shim.dll' }
)
$required = @($program) + @($moduleIds | ForEach-Object { Join-Path $buildRoot ($_.ToString() + '.dll') })
foreach ($file in $required) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Build output is missing: $file" }
}
$libraryRoot = Join-Path $modRoot '64'
foreach ($dependency in $dependencies) {
    $source = Join-Path $compilerBin $dependency.Source
    $target = Join-Path $libraryRoot $dependency.Target
    if (!(Test-Path -LiteralPath $target -PathType Leaf) -and !(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Windhawk runtime dependency is missing: $source"
    }
}

function Get-CommandPrefix([string]$Module, [string]$DataDirectory) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($DataDirectory.ToUpperInvariant())) }
    finally { $sha.Dispose() }
    $hash = [BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 16)
    return "Local\AITaskbar.$Module.$hash"
}

New-Item -ItemType Directory -Path $libraryRoot -Force | Out-Null
foreach ($dependency in $dependencies) {
    $target = Join-Path $libraryRoot $dependency.Target
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path $compilerBin $dependency.Source) -Destination $target
    }
}
$data = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'AITaskbar'
$profileRoot = if (Test-Path -LiteralPath (Join-Path $data 'use-existing-profiles') -PathType Leaf) {
    Split-Path $data -Parent
} else { $data }
$aiData = Join-Path $profileRoot 'AIEnter'
$haloData = Join-Path $profileRoot 'Halo'
$prefix = Get-CommandPrefix 'AIEnter' $aiData
$haloPrefix = Get-CommandPrefix 'Halo' $haloData
$report = Join-Path $workspace '.build\taskbar-reports'
New-Item -ItemType Directory -Path $haloData,$report -Force | Out-Null
$modules = @(
    @{ Id = 'aitaskbar-ai'; Version = '3.0.0'; Settings = @("executable=$program", "commandPrefix=$prefix", "dataDirectory=$aiData") },
    @{ Id = 'aitaskbar-halo'; Version = '0.4.0'; Settings = @("executable=$program", "dataDirectory=$haloData", "appearancePrefix=$haloPrefix", "reportDirectory=$report", 'diagnosticOnly=0') }
)
foreach ($module in $modules) {
    $source = Join-Path $buildRoot ($module.Id + '.dll')
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.Substring(0, 16)
    $library = "$($module.Id)_$hash.dll"
    $target = Join-Path $libraryRoot $library
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) { Copy-Item -LiteralPath $source -Destination $target }
    $config = @(
        '[Mod]', "LibraryFileName=$library", 'Disabled=0', 'LoggingEnabled=1', 'DebugLoggingEnabled=0',
        'Include=explorer.exe', 'Architecture=x86-64', "Version=$($module.Version)",
        "SettingsChangeTime=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())", '[Settings]'
    ) + $module.Settings
    [IO.File]::WriteAllLines((Join-Path $modRoot ($module.Id + '.ini')), $config, [Text.Encoding]::Unicode)
}
Write-Output 'Registered the integrated AITaskbar modules. Start or reload this portable Windhawk instance to apply them.'
