param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [int]$Tail = 160,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $GameDirectory 'BepInEx\bloodlink-install.json'
$logPath = Join-Path $GameDirectory 'BepInEx\LogOutput.log'
$dllPath = Join-Path $GameDirectory 'BepInEx\plugins\ULTRAKILL.Bloodlink.dll'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('BLOODLINK SYNCHRONIZATION DIAGNOSTICS')
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $lines.Add("Installed version: $($manifest.PluginVersion), protocol: $($manifest.ProtocolVersion)")
    if (Test-Path -LiteralPath $dllPath) {
        $actualHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
        $lines.Add("Installed DLL matches manifest: $($actualHash -eq $manifest.PluginSha256)")
    }
}
$instances = @(Get-Process -Name ULTRAKILL -ErrorAction SilentlyContinue)
$lines.Add("Running game instances: $($instances.Count)")
if (Test-Path -LiteralPath $logPath) {
    $lines.Add('Recent synchronization events and errors:')
    foreach ($line in Get-Content -LiteralPath $logPath -Tail ([Math]::Max(200, $Tail * 6))) {
        if ($line -match 'enemy sync:|world:|items:|outgoingHits|resynchronization|transition|Error|Exception|Warning|Bloodlink.*loaded') {
            $lines.Add($line)
        }
    }
}
$report = $lines | Select-Object -Last ([Math]::Max(20, $Tail))
$report
if ($OutputPath) { $report | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
