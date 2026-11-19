#requires -version 5.1
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet.exe" }
$release = Join-Path $root "release\ServerBackuper"
$agentOut = Join-Path $release "Agent"
$appOut = Join-Path $release "App"
$cleanup = Join-Path $PSScriptRoot "Cleanup-Project.ps1"
$zipPath = Join-Path $root ("ServerBackuper-" + $Runtime + ".zip")
$stagedZip = Join-Path $root ("ServerBackuper-" + $Runtime + ".nowy.zip")
$previousDirectory = Join-Path $root "niepotrzebne\poprzednia-wersja"
$previousZip = Join-Path $previousDirectory ("ServerBackuper-" + $Runtime + ".zip")
$previousInfo = Join-Path $previousDirectory ("ServerBackuper-" + $Runtime + ".txt")
$published = $false

try {
    & $dotnet run --project (Join-Path $root "ServerBackuper.Tests\ServerBackuper.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Testy przed publikacją nie przeszły poprawnie." }

    & $dotnet publish (Join-Path $root "ServerBackuper.Service\ServerBackuper.Service.csproj") `
        -c Release -r $Runtime --self-contained true -o $agentOut `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Publikowanie agenta nie powiodło się." }

    & $dotnet publish (Join-Path $root "ServerBackuper.App\ServerBackuper.App.csproj") `
        -c Release -r $Runtime --self-contained true -o $appOut `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Publikowanie panelu nie powiodło się." }

    & $dotnet publish (Join-Path $root "ServerBackuper.Uninstaller\ServerBackuper.Uninstaller.csproj") `
        -c Release -r $Runtime --self-contained true -o $release `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Publikowanie deinstalatora nie powiodło się." }

    & $dotnet publish (Join-Path $root "ServerBackuper.Installer\ServerBackuper.Installer.csproj") `
        -c Release -r $Runtime --self-contained true -o $release `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Publikowanie instalatora nie powiodlo sie." }

    Copy-Item -LiteralPath (Join-Path $root "scripts\Install-Agent.ps1") -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $root "INSTRUKCJA.md") -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $root "scripts\Install-Agent.cmd") -Destination $release -Force
    foreach ($document in @("README.md", "LICENSE", "CHANGELOG.md", "SECURITY.md", "THIRD_PARTY_NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $release -Force
    }
    $legacyUninstaller = Join-Path $release "Uninstall-Agent.ps1"
    if (Test-Path -LiteralPath $legacyUninstaller) { Remove-Item -LiteralPath $legacyUninstaller -Force }

    if (Test-Path -LiteralPath $stagedZip) { Remove-Item -LiteralPath $stagedZip -Force }
    Compress-Archive -Path (Join-Path $release "*") -DestinationPath $stagedZip -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $requiredEntries = @(
        "Agent/ServerBackuper.Agent.exe",
        "Agent/appsettings.json",
        "App/ServerBackuper.App.exe",
        "Install-Agent.exe",
        "Uninstall-ServerBackuper.exe",
        "Install-Agent.cmd",
        "Install-Agent.ps1",
        "INSTRUKCJA.md",
        "README.md",
        "LICENSE",
        "CHANGELOG.md",
        "SECURITY.md",
        "THIRD_PARTY_NOTICES.md"
    )
    $archive = [IO.Compression.ZipFile]::OpenRead($stagedZip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in $requiredEntries) {
            if ($entries -notcontains $required) { throw "Nowa paczka nie zawiera wymaganego pliku: $required" }
        }
        $empty = @($archive.Entries | Where-Object { $_.Length -eq 0 -and -not $_.FullName.EndsWith('/') })
        if ($empty.Count -gt 0) { throw "Nowa paczka zawiera pusty plik: $($empty[0].FullName)" }
    }
    finally { $archive.Dispose() }

    New-Item -ItemType Directory -Path $previousDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $zipPath) {
        $old = Get-Item -LiteralPath $zipPath
        $oldName = $old.Name
        $oldLength = $old.Length
        $oldHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if (Test-Path -LiteralPath $previousZip) { Remove-Item -LiteralPath $previousZip -Force }
        [IO.File]::Replace($stagedZip, $zipPath, $previousZip, $true)
        @(
            "Poprzednia paczka zachowana automatycznie przed publikacją: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
            "Nazwa: $oldName",
            "Rozmiar: $oldLength bajtów",
            "SHA-256: $oldHash"
        ) | Set-Content -LiteralPath $previousInfo -Encoding UTF8
    }
    else {
        Move-Item -LiteralPath $stagedZip -Destination $zipPath
    }
    $published = $true
}
finally {
    & $cleanup -Quiet
}

if ($published) {
    $newZip = Get-Item -LiteralPath $zipPath
    $newHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Host "Gotowy instalator ZIP: $zipPath"
    Write-Host "Rozmiar: $($newZip.Length) bajtów"
    Write-Host "SHA-256: $newHash"
    Write-Host "Poprzednia wersja: $previousZip"
    Write-Host "Odtwarzalne bin/obj/release zostały automatycznie posprzątane."
}
