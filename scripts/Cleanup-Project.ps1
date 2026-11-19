#requires -version 5.1
param([switch]$Quiet)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$protectedRoots = @(
    [IO.Path]::GetFullPath((Join-Path $root ".tools")).TrimEnd('\'),
    [IO.Path]::GetFullPath((Join-Path $root "niepotrzebne")).TrimEnd('\')
)

function Test-IsProtected([string]$Path) {
    foreach ($protected in $protectedRoots) {
        if ($Path.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

$removed = 0
for ($pass = 0; $pass -lt 3; $pass++) {
    $targets = [Collections.Generic.List[string]]::new()
    $release = [IO.Path]::GetFullPath((Join-Path $root "release")).TrimEnd('\')
    if (Test-Path -LiteralPath $release) { [void]$targets.Add($release) }

    Get-ChildItem -LiteralPath $root -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("bin", "obj", ".vs", "TestResults", "artifacts") } |
        ForEach-Object {
            $candidate = [IO.Path]::GetFullPath($_.FullName).TrimEnd('\')
            if (-not (Test-IsProtected $candidate)) { [void]$targets.Add($candidate) }
        }

    if ($targets.Count -eq 0) { break }
    foreach ($target in ($targets | Sort-Object Length -Descending -Unique)) {
        if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Odmowa usuniecia katalogu spoza projektu: $target"
        }
        if (Test-IsProtected $target) {
            throw "Odmowa usuniecia chronionego katalogu: $target"
        }
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
            $removed++
            if (-not $Quiet) { Write-Host "Usunieto odtwarzalny katalog: $target" }
        }
    }
}

Get-ChildItem -LiteralPath $root -File -Filter "ServerBackuper-*.nowy.zip" -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removed++
        if (-not $Quiet) { Write-Host "Usunieto niedokonczona paczke: $($_.FullName)" }
    }

if (-not $Quiet) { Write-Host "Porzadki zakonczone. Usuniete elementy odtwarzalne: $removed." }
