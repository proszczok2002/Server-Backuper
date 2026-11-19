#requires -version 5.1
$ErrorActionPreference = "Stop"

$installer = Join-Path $PSScriptRoot "Install-Agent.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Brakuje Install-Agent.exe obok skryptu. Rozpakuj cala paczke ZIP i sprobuj ponownie."
}

$process = Start-Process -FilePath $installer -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Instalator zglosil blad (kod $($process.ExitCode)). Sprawdz log wskazany w oknie instalatora."
}
