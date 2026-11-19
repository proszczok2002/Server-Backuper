# Wydawanie wersji

## Przygotowanie

1. Zaktualizuj `Version` w `Directory.Build.props`.
2. Uzupełnij `CHANGELOG.md`.
3. Sprawdź instrukcję, README i informacje o ograniczeniach testów.
4. Upewnij się, że w repozytorium nie ma sekretów, prywatnych ustawień, logów ani paczek binarnych.

## Walidacja lokalna

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

`Publish.ps1` ponownie uruchamia testy, tworzy `ServerBackuper-win-x64.zip`, weryfikuje wymagane wpisy i brak pustych plików, zachowuje jedną poprzednią paczkę lokalnie oraz sprząta artefakty robocze.

Przed publikacją zapisz rozmiar i SHA-256:

```powershell
Get-Item .\ServerBackuper-win-x64.zip | Select-Object Name,Length
Get-FileHash .\ServerBackuper-win-x64.zip -Algorithm SHA256
```

## GitHub

1. Zatwierdź i wypchnij czyste źródła do `main`.
2. Poczekaj na zielony workflow CI.
3. Utwórz tag `vMAJOR.MINOR.PATCH` wskazujący zweryfikowany commit.
4. Uruchom workflow „Release” z numerem tagu albo utwórz wydanie przez GitHub CLI.
5. Dołącz dokładnie `ServerBackuper-win-x64.zip`.
6. Pobierz publiczny asset i porównaj jego rozmiar oraz SHA-256 z lokalnym plikiem.

Nie umieszczaj ZIP-a bezpośrednio w historii Git — binaria należą do GitHub Releases.
