param([Parameter(Mandatory)][string]$RuntimeRoot)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$RuntimeRoot = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$compilerRoot = Join-Path $RuntimeRoot 'Compiler'
$compiler = Join-Path $compilerRoot 'bin\clang++.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Windhawk compiler not found: $compiler" }
$engine = Get-ChildItem -LiteralPath (Join-Path $RuntimeRoot 'Engine') -Recurse -File -Filter windhawk.lib |
    Where-Object { $_.Directory.Name -eq '64' } | Select-Object -First 1 -ExpandProperty FullName
if (!$engine) { throw "Windhawk x64 engine library not found: $RuntimeRoot" }
$output = Join-Path $workspace '.build\taskbar'
New-Item -ItemType Directory -Path $output -Force | Out-Null

$modules = @(
    @{ Id = 'aitaskbar-ai'; Version = '3.0.0'; Source = 'modules\AIEnter\tools\taskbar-ai\aienter-taskbar-ai.wh.cpp' },
    @{ Id = 'aitaskbar-halo'; Version = '0.4.0'; Source = 'modules\Halo\tools\taskbar\halo-taskbar.wh.cpp' }
)
foreach ($module in $modules) {
    $source = Join-Path $workspace $module.Source
    $dll = Join-Path $output ($module.Id + '.dll')
    $response = Join-Path $output ($module.Id + '.rsp')
    $arguments = @(
        '-std=c++23', '-O2', '-shared', '-DUNICODE', '-D_UNICODE', '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00',
        '-DWH_MOD', ('-DWH_MOD_ID=L\"' + $module.Id + '\"'), ('-DWH_MOD_VERSION=L\"' + $module.Version + '\"'),
        '-include', 'windhawk_api.h', '-target', 'x86_64-w64-mingw32', '-Wl,--export-all-symbols',
        ('"' + $engine.Replace('\','/') + '"'), ('"' + $source.Replace('\','/') + '"'),
        '-o', ('"' + $dll.Replace('\','/') + '"'), '-lole32', '-loleaut32', '-lruntimeobject',
        '-lshell32', '-lshcore', '-luuid', '-ladvapi32'
    )
    [IO.File]::WriteAllLines($response, $arguments, [Text.UTF8Encoding]::new($false))
    Push-Location $compilerRoot
    try {
        & $compiler "@$response"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $($module.Id)" }
    }
    finally { Pop-Location }
    Write-Output "Built: $dll"
}
