# Historia zmian

Wszystkie istotne zmiany projektu są opisywane w tym pliku. Projekt używa wersjonowania semantycznego.

## [1.0.0] — 2026-11-19

Pierwsze publiczne wydanie kodu źródłowego.

### Dodano

- agent backupu uruchamiany automatycznie w sesji użytkownika;
- dostępny panel WPF działający także w zasobniku;
- harmonogram, historia, postęp operacji i bezpieczne anulowanie;
- szyfrowane archiwa 7z bez kompresji, lokalny test integralności i SHA-256 NAS;
- backup lokalnych bind-mountów, nazwanych wolumenów i logicznych dumpów baz;
- obsługę Nextcloud maintenance mode;
- opcjonalny NAS oraz niezależną retencję lokalną i NAS;
- sprawdzanie oraz przywracanie archiwów;
- powiadomienia Telegram chronione przez DPAPI;
- instalator i deinstalator z ochroną aktywnej operacji;
- automatyczne testy, publikację i sprzątanie artefaktów roboczych.

[1.0.0]: https://github.com/proszczok2002/Server-Backuper/releases/tag/v1.0.0
