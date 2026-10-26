param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL',
    [string]$BepInExDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($BepInExDirectory)) { $BepInExDirectory = Join-Path $GameDirectory 'BepInEx\core' }
$managed = Join-Path $GameDirectory 'ULTRAKILL_Data\Managed'
foreach ($file in @((Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $BepInExDirectory 'BepInEx.dll'), (Join-Path $BepInExDirectory '0Harmony.dll'))) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Required local reference is missing: $file" }
}
& dotnet build (Join-Path $projectRoot 'ULTRAKILL.Bloodlink.csproj') --configuration Release "-p:GameManagedDir=$managed" "-p:BepInExDir=$BepInExDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Bloodlink compilation failed.' }
$output = Join-Path $projectRoot 'bin\Release\netstandard2.1'
$release = Join-Path $projectRoot 'release\BepInEx\plugins'
New-Item -ItemType Directory -Path $release -Force | Out-Null
foreach ($file in @('ULTRAKILL.Bloodlink.dll','LiteNetLib.dll')) { Copy-Item -LiteralPath (Join-Path $output $file) -Destination (Join-Path $release $file) -Force }
foreach ($file in @('Unity.Networking.Transport.dll','Unity.Services.Authentication.dll','Unity.Services.QoS.dll','Unity.Services.Relay.dll')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('Dependencies\' + $file)) -Destination (Join-Path $release $file) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'doorstop_config.ini') -Destination (Join-Path $projectRoot 'release\doorstop_config.ini') -Force
Write-Output "Build ready: $release"