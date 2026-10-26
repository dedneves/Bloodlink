param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Tests\Bloodlink.ProtocolChecks.csproj'
& dotnet run --project $project -c Release -- --sync-only
if ($LASTEXITCODE -ne 0) { throw "Synchronization regression checks failed with exit code $LASTEXITCODE." }
