@echo off
setlocal
set "installer=%~dp0Install-Agent.exe"
if not exist "%installer%" (
    echo BLAD: Brakuje pliku Install-Agent.exe obok tego pliku CMD.
    echo Rozpakuj cala paczke ServerBackuper-win-x64.zip i sprobuj ponownie.
    pause
    exit /b 1
)
start "" /wait "%installer%"
set "result=%errorlevel%"
endlocal & exit /b %result%
