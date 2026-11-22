param(
    [Parameter(Mandatory)]
    [string]$HostExecutable,
    [ValidateSet('Edge', 'Chrome')]
    [string]$Browser = 'Edge',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $HostExecutable).Path
$hostName = "com.aitaskbar.$($Browser.ToLowerInvariant())_recent"
$manifest = Join-Path (Split-Path $exe) "$hostName.json"
$browserKey = if ($Browser -eq 'Chrome') { 'Google\Chrome' } else { 'Microsoft\Edge' }
$origin = if ($Browser -eq 'Chrome') { 'ejgcgkmboopnpecjhmppoehaacinokeb' } else { 'ejfpgkpgggjobadkfedhkoimfedhggan' }
$registryPath = "Software\$browserKey\NativeMessagingHosts\$hostName"
$registry = [Microsoft.Win32.Registry]::CurrentUser
$existing = $registry.OpenSubKey($registryPath)
if ($existing) {
    try {
        if ($existing.GetValue('') -ne $manifest) {
            throw 'The companion host name is already registered to a different installation.'
        }
    }
    finally { $existing.Dispose() }
}

if ($Remove) {
    if (!$existing) { return }
    $registry.DeleteSubKey($registryPath)
    # The browser can keep an existing connection after the registry entry is removed.
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        Stop-Process -ErrorAction Stop
    return
}

$data = @{
    name = $hostName
    description = "AITaskbar $Browser recent sessions"
    path = $exe
    type = 'stdio'
    allowed_origins = @("chrome-extension://$origin/")
}
[IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $data))
$entry = $registry.CreateSubKey($registryPath)
try { $entry.SetValue('', $manifest) }
finally { $entry.Dispose() }
