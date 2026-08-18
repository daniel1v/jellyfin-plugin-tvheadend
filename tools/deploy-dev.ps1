<#
.SYNOPSIS
    Publishes the plugin into the local Jellyfin 12 test instance and restarts it.

.DESCRIPTION
    The deployment step of commit -> push -> deploy. It stops the instance, copies the plugin's
    own assemblies over the installed ones, rewrites meta.json and starts the instance again,
    then confirms through the API that the plugin loaded.

    Two things this exists to get right, both of which have broken the instance before:

    - The backup goes outside data/plugins. Jellyfin scans that directory recursively, finds a
      second copy of TVHeadEnd.dll and refuses to load either ("Assembly with same name is
      already loaded"), which disables the plugin entirely.
    - meta.json is written without a byte order mark. Set-Content -Encoding utf8 writes one in
      Windows PowerShell 5.1 and Jellyfin's JSON reader rejects the file.

    Only the plugin's own assemblies are copied. A publish also emits the Jellyfin framework
    assemblies it compiled against; those belong to the server and must not be shadowed.

.PARAMETER InstanceRoot
    The Jellyfin 12 test instance directory.

.PARAMETER SkipStart
    Leave the instance stopped after deploying.
#>
[CmdletBinding()]
param(
    [string] $InstanceRoot = 'C:\dev\jellyfin-12-test',
    [switch] $SkipStart
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$pluginDirectory = Join-Path $InstanceRoot 'data\plugins\TVHeadEnd'
$serverDll = [System.IO.Path]::GetFullPath((Join-Path $InstanceRoot 'system\jellyfin\jellyfin.dll'))
$dotnetPath = [System.IO.Path]::GetFullPath((Get-Command dotnet).Source)

# The plugin's own output. Everything else a publish produces is Jellyfin's.
$assemblies = @(
    'TVHeadEnd.dll',
    'TVHeadEnd.deps.json',
    'TVHeadEnd.pdb',
    'TVHeadEnd.xml',
    'Tvheadend.Htsp.dll',
    'Tvheadend.Htsp.pdb',
    'Tvheadend.Htsp.xml'
)

if (-not (Test-Path $pluginDirectory)) {
    throw "No TVHeadend plugin directory at '$pluginDirectory'. Is this the right instance?"
}

function Stop-Instance {
    $listening = Get-NetTCPConnection -LocalPort 8097 -State Listen -ErrorAction SilentlyContinue
    if (-not $listening) {
        Write-Host 'The test instance is not running.'
        return
    }

    $processId = $listening[0].OwningProcess
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $process) { return }

    # The instance runs as "dotnet jellyfin.dll", so it has no distinguishing process name.
    # Identify it the way Stop-JellyfinTest.ps1 does before stopping anything.
    $info = Get-CimInstance Win32_Process -Filter "ProcessId = $processId"
    $isConfiguredServer = $process.Path.Equals($dotnetPath, [System.StringComparison]::OrdinalIgnoreCase) `
        -and $null -ne $info.CommandLine `
        -and $info.CommandLine.IndexOf($serverDll, [System.StringComparison]::OrdinalIgnoreCase) -ge 0

    if (-not $isConfiguredServer) {
        throw "Process $processId listens on 8097 but is not the configured Jellyfin 12 test server."
    }

    Stop-Process -Id $processId
    $process.WaitForExit(20000) | Out-Null
    if (-not $process.HasExited) {
        throw "The test instance ($processId) did not stop within 20 seconds."
    }

    Write-Host "Stopped the test instance (process $processId)."
}

Write-Host 'Publishing...'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("tvh-deploy-" + [System.Guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $repo 'TVHeadEnd\TVHeadEnd.csproj') -c Release -o $staging --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The build failed; nothing was deployed.' }

foreach ($assembly in $assemblies) {
    if (-not (Test-Path (Join-Path $staging $assembly))) {
        throw "The publish did not produce '$assembly'."
    }
}

$version = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $staging 'TVHeadEnd.dll')).Version.ToString()
$branch = (& git -C $repo rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git -C $repo rev-parse --short HEAD).Trim()

Stop-Instance

# Outside data/plugins on purpose; see the note above.
$backup = Join-Path $InstanceRoot "data\plugin-backups\TVHeadEnd-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($assembly in $assemblies + @('meta.json')) {
    $existing = Join-Path $pluginDirectory $assembly
    if (Test-Path $existing) { Copy-Item $existing $backup -Force }
}

foreach ($assembly in $assemblies) {
    Copy-Item (Join-Path $staging $assembly) $pluginDirectory -Force
}

Remove-Item $staging -Recurse -Force

$meta = [ordered]@{
    category    = 'LiveTV'
    changelog   = "Local development build from $branch ($commit)."
    description = 'Provides live TV using TVHeadend as the source.'
    guid        = '3fd018e5-5e78-4e58-b280-a0c068febee0'
    name        = 'TVHeadend'
    overview    = 'Manage TVHeadend from Jellyfin'
    owner       = 'jellyfin'
    targetAbi   = '12.0.0.0'
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    version     = $version
    status      = 'Active'
    autoUpdate  = $false
    imagePath   = (Join-Path $pluginDirectory 'jellyfin-plugin-tvheadend.png')
    assemblies  = @()
}

# No byte order mark; see the note above.
[System.IO.File]::WriteAllText(
    (Join-Path $pluginDirectory 'meta.json'),
    ($meta | ConvertTo-Json -Depth 10),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Deployed $version from $branch ($commit)."

if ($SkipStart) {
    Write-Host 'Left the instance stopped.'
    return
}

Start-Process powershell.exe -WorkingDirectory $InstanceRoot -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $InstanceRoot 'Start-JellyfinTest.ps1'))

# Confirmed through the API rather than the log: the file sink lags behind by minutes, so an
# empty log says nothing about whether the plugin loaded.
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    try {
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:8097/System/Info/Public' -TimeoutSec 5
        break
    } catch {
        continue
    }
}

$auth = 'MediaBrowser Client="deploy", Device="cli", DeviceId="deploy-dev", Version="1.0"'
$session = Invoke-RestMethod -Uri 'http://127.0.0.1:8097/Users/AuthenticateByName' -Method Post `
    -Headers @{ Authorization = $auth } -ContentType 'application/json' `
    -Body (@{ Username = 'devadmin'; Pw = 'devadmin' } | ConvertTo-Json) -TimeoutSec 30

$plugins = Invoke-RestMethod -Uri 'http://127.0.0.1:8097/Plugins' `
    -Headers @{ Authorization = "$auth, Token=`"$($session.AccessToken)`"" } -TimeoutSec 30

$plugin = $plugins | Where-Object { $_.Id -eq '3fd018e55e784e58b280a0c068febee0' }
if (-not $plugin) { throw 'The server started but does not list the TVHeadend plugin.' }
if ($plugin.Status -ne 'Active') { throw "The TVHeadend plugin is '$($plugin.Status)', not Active." }

Write-Host "TVHeadend $($plugin.Version) is Active on http://localhost:8097."
