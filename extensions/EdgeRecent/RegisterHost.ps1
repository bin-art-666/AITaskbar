param(
    [Parameter(Mandatory)]
    [string]$HostExecutable,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $HostExecutable).Path
$hostName = 'com.aitaskbar.edge_recent'
$manifest = Join-Path (Split-Path $exe) "$hostName.json"
$registryPath = "Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
$registry = [Microsoft.Win32.Registry]::CurrentUser
$existing = $registry.OpenSubKey($registryPath)
if ($existing) {
    try {
        if ($existing.GetValue('') -ne $manifest) {
            throw 'The Edge companion host name is already registered to a different installation.'
        }
    }
    finally { $existing.Dispose() }
}

if ($Remove) {
    if (!$existing) { return }
    $registry.DeleteSubKey($registryPath)
    # Edge can keep an existing Native Messaging connection after the registry entry is removed.
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        Stop-Process -ErrorAction Stop
    return
}

$data = @{
    name = $hostName
    description = 'AITaskbar Edge recent sessions'
    path = $exe
    type = 'stdio'
    allowed_origins = @('chrome-extension://ejfpgkpgggjobadkfedhkoimfedhggan/')
}
[IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $data))
$entry = $registry.CreateSubKey($registryPath)
try { $entry.SetValue('', $manifest) }
finally { $entry.Dispose() }
