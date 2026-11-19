#requires -version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet.exe" }
try {
    & $dotnet run --project (Join-Path $root "ServerBackuper.Tests\ServerBackuper.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Testy nie przeszły poprawnie." }
}
finally {
    & (Join-Path $PSScriptRoot "Cleanup-Project.ps1") -Quiet
}
