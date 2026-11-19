# Server Backuper

[![CI](https://github.com/proszczok2002/Server-Backuper/actions/workflows/ci.yml/badge.svg)](https://github.com/proszczok2002/Server-Backuper/actions/workflows/ci.yml)
[![Najnowsze wydanie](https://img.shields.io/github/v/release/proszczok2002/Server-Backuper?label=wydanie)](https://github.com/proszczok2002/Server-Backuper/releases/latest)
[![Licencja MIT](https://img.shields.io/badge/licencja-MIT-blue.svg)](LICENSE)

Server Backuper to otwartoźródłowa aplikacja dla Windows, która wykonuje szyfrowane kopie lokalnych środowisk Docker. Tworzy sprawdzone archiwum lokalne, opcjonalnie kopiuje je na NAS i porównuje sumy SHA-256 obu plików.

Projekt składa się z działającego w tle agenta oraz dostępnego panelu WPF. Panel może pracować w zasobniku systemowym, a harmonogram wykonuje kopie bez konieczności pozostawiania otwartego okna.

## Najważniejsze możliwości

- backup lokalnego katalogu serwera oraz należących do niego projektów Docker Compose;
- szyfrowane archiwum 7z z ukrytymi nazwami plików (`7zAES`);
- tryb bez kompresji dla wysokiej przepustowości dużych zbiorów;
- pełny test integralności lokalnego archiwum przed uznaniem backupu za poprawny;
- opcjonalna kopia NAS przez ścieżkę UNC;
- porównanie SHA-256 archiwum lokalnego i kopii NAS;
- niezależna retencja lokalna oraz NAS;
- bezpieczne zatrzymywanie i ponowne uruchamianie wcześniej działających kontenerów;
- maintenance mode dla Nextcloud oraz logiczne dumpy MariaDB, MySQL i PostgreSQL;
- eksport lokalnych nazwanych wolumenów Docker z zachowaniem linuksowych praw dostępu;
- pomijanie wolumenów sieciowych CIFS, SMB, NFS, SSHFS, Rclone i WebDAV;
- sprawdzanie i przywracanie archiwów z poziomu panelu;
- opcjonalne powiadomienia Telegram;
- ikona zasobnika i interfejs oparty na standardowych kontrolkach WPF.

## Wymagania

- Windows 10 lub Windows 11 x64;
- Docker Desktop działający na tym samym koncie Windows;
- 7-Zip z dostępnym `7z.exe`;
- lokalny katalog źródłowy — źródło UNC i mapowane dyski sieciowe są celowo blokowane;
- opcjonalnie dostęp do NAS przez ścieżkę UNC.

## Instalacja

1. Pobierz [najnowszy `ServerBackuper-win-x64.zip`](https://github.com/proszczok2002/Server-Backuper/releases/latest/download/ServerBackuper-win-x64.zip).
2. Rozpakuj cały ZIP do zwykłego katalogu.
3. Uruchom `Install-Agent.exe` i potwierdź UAC.
4. Otwórz panel ze skrótu „Server Backuper”.
5. Wskaż lokalne źródło, katalog kopii lokalnych, `7z.exe`, hasło archiwum oraz opcjonalny NAS.
6. Zapisz ustawienia i wykonaj pierwszy ręczny backup kontrolny.

Program jest publikowany jako samodzielny pakiet `win-x64`, więc użytkownik nie musi instalować .NET. Pliki wykonywalne nie są obecnie podpisane certyfikatem Authenticode; Windows SmartScreen może wyświetlić ostrzeżenie.

Szczegółowa instrukcja użytkownika znajduje się w [INSTRUKCJA.md](INSTRUKCJA.md).

## Bezpieczeństwo danych

- hasło archiwum i token Telegram są chronione przez Windows DPAPI;
- sekrety nie są zapisywane w logach;
- archiwum lokalne przechodzi pełny test 7-Zip;
- podczas kopiowania liczony jest SHA-256 źródła, następnie program odczytuje kopię NAS i porównuje obie sumy;
- kopia NAS otrzymuje końcową nazwę dopiero po zgodnej sumie;
- retencja usuwa wyłącznie archiwa pasujące do nazwy tworzonej przez program;
- deinstalator nie usuwa właściwych backupów lokalnych ani kopii NAS.

Więcej informacji: [SECURITY.md](SECURITY.md) oraz [docs/BACKUP-FORMAT.md](docs/BACKUP-FORMAT.md).

## Budowanie ze źródeł

W repozytorium nie ma lokalnego SDK `.tools` ani gotowych plików binarnych. Zainstaluj .NET SDK wskazany w `global.json`, a następnie uruchom:

```powershell
dotnet restore .\ServerBackuper.slnx
dotnet build .\ServerBackuper.slnx -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

`Publish.ps1` uruchamia testy, buduje samodzielne pliki `win-x64`, sprawdza zawartość ZIP-a i usuwa odtwarzalne `bin`, `obj` oraz `release`.

## Struktura projektu

- `ServerBackuper.App` — panel WPF i zasobnik systemowy;
- `ServerBackuper.Service` — agent, harmonogram i obsługa poleceń;
- `ServerBackuper.Core` — backup, restore, Docker, retencja, sekrety i logowanie;
- `ServerBackuper.Installer` — instalator wymagający uprawnień administratora;
- `ServerBackuper.Uninstaller` — bezpieczny deinstalator;
- `ServerBackuper.Tests` — testy regresji uruchamiane jako aplikacja konsolowa;
- `docs` — architektura, format backupu i proces wydawania.

## Stan testów i ograniczenia

Wydanie 1.0.0 przechodzi 17 testów regresji, kompilację bez ostrzeżeń oraz test uruchomienia opublikowanego panelu na izolowanych danych. Nie wykonano jeszcze opublikowaną wersją pełnego produkcyjnego backupu około 70 GB ani ręcznego przejścia całego interfejsu aktualnym NVDA. Zgłoszenia z takich testów są mile widziane.

## Współpraca

Przed zmianą przeczytaj [CONTRIBUTING.md](CONTRIBUTING.md). Błędy i propozycje można zgłaszać przez zakładkę Issues. Problemów bezpieczeństwa nie publikuj jako zwykłego zgłoszenia — użyj instrukcji w [SECURITY.md](SECURITY.md).

## Licencja

Kod źródłowy jest dostępny na licencji [MIT](LICENSE). Zewnętrzne narzędzia i biblioteki zachowują własne licencje; szczegóły zawiera [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
