# Architektura

## Procesy

Server Backuper składa się z dwóch niezależnych procesów użytkownika:

- `ServerBackuper.Agent.exe` — agent odpowiedzialny za harmonogram, backup, restore, kolejkę poleceń i trwały stan;
- `ServerBackuper.App.exe` — panel WPF, który zapisuje ustawienia i przekazuje polecenia agentowi.

Instalator tworzy zadanie Harmonogramu zadań uruchamiające agenta po zalogowaniu. Jest to celowe, ponieważ Docker Desktop również działa w sesji użytkownika. Panel może zostać zamknięty bez zatrzymywania agenta.

## Dane trwałe

Domyślny katalog danych to:

```text
C:\ProgramData\ServerBackuper
```

Najważniejsze pliki:

- `settings.json` — konfiguracja, sekrety zabezpieczone DPAPI;
- `state.json` — heartbeat, historia, bieżący etap i lista kontenerów do odzyskania;
- `commands` — atomowa kolejka poleceń panel–agent;
- `logs` — tekstowe logi miesięczne.

Zapis JSON używa pliku tymczasowego i atomowej podmiany z ponawianiem. Odczyt współdzieli plik tak, aby panel nie blokował zapisu agenta.

## Przebieg backupu

1. Walidacja ustawień, lokalności źródła, 7-Zip, NAS i Docker Engine.
2. Wykrycie kontenerów Compose, bind-mountów i współdzielonych nazwanych wolumenów.
3. Odfiltrowanie wolumenów sieciowych.
4. Włączenie Nextcloud maintenance mode i wykonanie logicznych dumpów rozpoznanych baz.
5. Zapis identyfikatorów działających kontenerów do trwałego stanu.
6. Zatrzymanie wyłącznie kontenerów, które działały przed backupem.
7. Eksport lokalnych nazwanych wolumenów i manifestu migracyjnego.
8. Utworzenie zaszyfrowanego archiwum `.partial.7z` metodą `Copy` bez kompresji.
9. Pełny lokalny test 7-Zip i atomowa zmiana nazwy archiwum.
10. Odzyskanie kontenerów i wyłączenie maintenance mode niezależnym tokenem anulowania.
11. Retencja lokalna.
12. Opcjonalne kopiowanie na NAS z liczeniem SHA-256 źródła.
13. Ponowny odczyt kopii NAS, porównanie SHA-256 i dopiero wtedy końcowa nazwa.
14. Retencja NAS.

## Odzyskiwanie po przerwaniu

Agent zapisuje listę wcześniej działających kontenerów przed ich zatrzymaniem. Anulowanie operacji nie anuluje procedury odzyskania. Po awarii procesu lub systemu agent przy następnym uruchomieniu najpierw próbuje uruchomić zapisane kontenery i wyłączyć Nextcloud maintenance mode.

## Granice modułów

- `BackupEngine` — tworzenie, lokalny test, kopia NAS i retencja;
- `RestoreEngine` — inspekcja, staging, odtworzenie danych i wolumenów;
- `DockerDiscovery` — wykrywanie kontenerów i klasyfikacja wolumenów;
- `JsonFileStore` — bezpieczny współbieżny zapis stanu;
- `SecretProtector` — Windows DPAPI;
- `Worker` — harmonogram, kolejka poleceń i historia;
- `MainWindow` — interfejs, ustawienia i dostępny podgląd operacji.
