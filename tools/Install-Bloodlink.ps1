param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL'
)

$ErrorActionPreference = 'Stop'

$gamePath = [System.IO.Path]::GetFullPath($GameDirectory)
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginSource = Join-Path $PSScriptRoot '..\bin\Release\netstandard2.1\ULTRAKILL.Bloodlink.dll'
$netSource = Join-Path $PSScriptRoot '..\bin\Release\netstandard2.1\LiteNetLib.dll'
$relayDependencyDirectory = Join-Path $projectRoot 'Dependencies'
$doorstopTemplate = Join-Path $PSScriptRoot 'doorstop_config.ini'
$relayDependencies = @('Unity.Networking.Transport.dll', 'Unity.Services.Authentication.dll', 'Unity.Services.QoS.dll', 'Unity.Services.Relay.dll')
$pluginCode = Get-Content -LiteralPath (Join-Path $projectRoot 'Core\BloodlinkPlugin.cs') -Raw
$protocolCode = Get-Content -LiteralPath (Join-Path $projectRoot 'Networking\NetworkMessage.cs') -Raw
$pluginVersionMatch = [regex]::Match($pluginCode, 'public const string PluginVersion\s*=\s*"([^"]+)"')
$protocolVersionMatch = [regex]::Match($protocolCode, 'public const byte CurrentProtocolVersion\s*=\s*(\d+)')
if (-not $pluginVersionMatch.Success) { throw 'Could not read PluginVersion from Core\BloodlinkPlugin.cs.' }
if (-not $protocolVersionMatch.Success) { throw 'Could not read CurrentProtocolVersion from Networking\NetworkMessage.cs.' }
$pluginVersion = $pluginVersionMatch.Groups[1].Value
$protocolVersion = [byte]$protocolVersionMatch.Groups[1].Value
$pluginDirectory = Join-Path $gamePath 'BepInEx\plugins'
$coreDirectory = Join-Path $gamePath 'BepInEx\core'

if (Get-Process -Name 'ULTRAKILL' -ErrorAction SilentlyContinue) {
    throw 'ULTRAKILL is running. Close the game before installing Bloodlink.'
}
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'ULTRAKILL.exe'))) {
    throw "ULTRAKILL.exe was not found in '$gamePath'."
}
if (-not (Test-Path -LiteralPath (Join-Path $coreDirectory 'BepInEx.dll'))) {
    throw "BepInEx is not installed in '$gamePath'."
}
if (-not (Test-Path -LiteralPath (Join-Path $coreDirectory 'BepInEx.Preloader.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $gamePath 'winhttp.dll'))) {
    throw 'The BepInEx bootstrap is incomplete: winhttp.dll and BepInEx\core\BepInEx.Preloader.dll are required.'
}
foreach ($source in @($pluginSource, $netSource) + @($relayDependencies | ForEach-Object { Join-Path $relayDependencyDirectory $_ })) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Build output not found: '$source'. Build the project first." }
}

New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
$backupRoot = Join-Path $gamePath 'BepInEx\BloodlinkBackups'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDirectory = Join-Path $backupRoot $timestamp
$filesToInstall = @(
    @{ Source = $pluginSource; Destination = (Join-Path $pluginDirectory 'ULTRAKILL.Bloodlink.dll') },
    @{ Source = $netSource; Destination = (Join-Path $pluginDirectory 'LiteNetLib.dll') }
)
foreach ($dependency in $relayDependencies) {
    $filesToInstall += @{ Source = (Join-Path $relayDependencyDirectory $dependency); Destination = (Join-Path $pluginDirectory $dependency) }
}
$backedUp = @()

$doorstopConfig = Join-Path $gamePath 'doorstop_config.ini'
if (-not (Test-Path -LiteralPath $doorstopConfig)) {
    Copy-Item -LiteralPath $doorstopTemplate -Destination $doorstopConfig
    Write-Output 'Restored missing doorstop_config.ini so Unity loads the BepInEx preloader.'
}

foreach ($entry in $filesToInstall) {
    if (Test-Path -LiteralPath $entry.Destination) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        $backupFile = Join-Path $backupDirectory ([System.IO.Path]::GetFileName($entry.Destination))
        Copy-Item -LiteralPath $entry.Destination -Destination $backupFile
        $backedUp += [System.IO.Path]::GetFileName($entry.Destination)
    }
    Copy-Item -LiteralPath $entry.Source -Destination $entry.Destination -Force
}

$bepInExConfig = Join-Path $gamePath 'BepInEx\config\BepInEx.cfg'
if (Test-Path -LiteralPath $bepInExConfig) {
    $configText = [System.IO.File]::ReadAllText($bepInExConfig)
    $updatedConfig = [regex]::Replace($configText, '(?m)^(\s*HideManagerGameObject\s*=\s*)false\s*$', '${1}true')
    if ($updatedConfig -ne $configText) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        Copy-Item -LiteralPath $bepInExConfig -Destination (Join-Path $backupDirectory 'BepInEx.cfg') -Force
        [System.IO.File]::WriteAllText($bepInExConfig, $updatedConfig, (New-Object System.Text.UTF8Encoding($false)))
        $backedUp += 'BepInEx.cfg (HideManagerGameObject=true)'
        Write-Output 'Enabled BepInEx HideManagerGameObject (required by ULTRAKILL scene cleanup).'
    }
}

# Older iterations were sometimes installed into a nested Bloodlink folder.
# BepInEx scans nested plugin folders too, so keep a recoverable backup and
# remove only the two Bloodlink-owned binaries from that known legacy folder.
$legacyDirectory = Join-Path $pluginDirectory 'Bloodlink'
if (Test-Path -LiteralPath $legacyDirectory -PathType Container) {
    foreach ($legacyName in @('ULTRAKILL.Bloodlink.dll', 'LiteNetLib.dll') + $relayDependencies) {
        $legacyFile = Join-Path $legacyDirectory $legacyName
        if (-not (Test-Path -LiteralPath $legacyFile -PathType Leaf)) { continue }
        $legacyBackupDirectory = Join-Path $backupDirectory 'Legacy\Bloodlink'
        New-Item -ItemType Directory -Path $legacyBackupDirectory -Force | Out-Null
        Copy-Item -LiteralPath $legacyFile -Destination (Join-Path $legacyBackupDirectory $legacyName) -Force
        Remove-Item -LiteralPath $legacyFile -Force
        $backedUp += "Legacy/Bloodlink/$legacyName (removed duplicate)"
    }
    if (-not (Get-ChildItem -LiteralPath $legacyDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $legacyDirectory -Force
    }
}

$dependencyHashes = @{}
foreach ($dependency in $relayDependencies) {
    $dependencyHashes[$dependency] = (Get-FileHash -LiteralPath (Join-Path $pluginDirectory $dependency) -Algorithm SHA256).Hash
}

$manifest = [ordered]@{
    PluginVersion = $pluginVersion
    ProtocolVersion = $protocolVersion
    InstalledAt = (Get-Date).ToUniversalTime().ToString('o')
    GameDirectory = $gamePath
    PluginSha256 = (Get-FileHash -LiteralPath (Join-Path $pluginDirectory 'ULTRAKILL.Bloodlink.dll') -Algorithm SHA256).Hash
    LiteNetLibSha256 = (Get-FileHash -LiteralPath (Join-Path $pluginDirectory 'LiteNetLib.dll') -Algorithm SHA256).Hash
    RelayDependencySha256 = $dependencyHashes
    BackupDirectory = if ($backedUp.Count -gt 0) { $backupDirectory } else { $null }
    BackedUpFiles = $backedUp
}
$manifestPath = Join-Path $gamePath 'BepInEx\bloodlink-install.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Output "Bloodlink $pluginVersion (protocol $protocolVersion) installed in '$pluginDirectory'."
Write-Output "Install manifest: '$manifestPath'."
if ($backedUp.Count -gt 0) { Write-Output "Previous files backed up to '$backupDirectory'." }
