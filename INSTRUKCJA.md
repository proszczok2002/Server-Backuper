# Server Backuper — instrukcja

## Co to jest

Server Backuper składa się z niewidocznego agenta działającego w tle oraz dostępnego panelu WPF. Agent uruchamia się automatycznie po zalogowaniu do Windows, ponieważ Docker Desktop również działa w sesji użytkownika. Panel ma ikonę w zasobniku systemowym. Zamknięcie albo zminimalizowanie okna ukrywa je w zasobniku i nie zatrzymuje agenta ani harmonogramu.

Instalator konfiguruje zadanie agenta bez limitu czasu działania i z automatycznym restartem po awarii. Dzięki temu agent może pozostawać uruchomiony pomiędzy kopiami wykonywanymi rzadziej niż co 72 godziny.

Domyślne ustawienia:

- backup co 48 godzin;
- pierwsze wykonanie o 05:00 w dacie pokazanej w panelu;
- 2 najnowsze poprawne archiwa w `C:\Backupy`;
- 3 najnowsze poprawne archiwa na NAS;
- pakowanie 7-Zip bez kompresji, ograniczone głównie szybkością dysku;
- obowiązkowe porównanie SHA-256 archiwum lokalnego i kopii NAS;
- niepełne pliki `.partial` usuwane po 3 dniach.

Wszystkie te wartości można zmienić w panelu. Kopiowanie na NAS można całkowicie wyłączyć. Wtedy harmonogram tworzy, sprawdza i zachowuje wyłącznie backup lokalny, a program nie łączy się z NAS-em.

## Wymagania

1. Windows 10 lub Windows 11 x64.
2. Działający Docker Desktop ustawiony do automatycznego uruchamiania po logowaniu.
3. 7-Zip. Jeśli nie jest zainstalowany w standardowym katalogu, wskaż `7z.exe` w panelu.
4. Dostęp do udziału NAS przez ścieżkę UNC — tylko jeśli kopiowanie na NAS jest włączone.
5. Konto Windows używane do instalacji musi mieć dostęp do Dockera i katalogu źródłowego, a przy włączonej kopii NAS także do udziału NAS.

## Instalacja gotowego pakietu

1. Rozpakuj najnowszy `ServerBackuper-win-x64.zip` do zwykłego katalogu.
2. Uruchom dwukrotnie plik `Install-Agent.exe`. Alternatywnie uruchom `Install-Agent.cmd` z wiersza polecenia CMD.
3. Potwierdź uprawnienia administratora.
4. Instalator skopiuje program do `C:\Program Files\ServerBackuper`, utworzy wewnętrzne zadanie agenta, uruchomi je i otworzy panel.
5. Na pulpicie pojawi się skrót „Server Backuper”.

Pakiet jest samodzielny (`self-contained`) i zawiera środowisko .NET wymagane przez aplikację. Instalator pokazuje komunikat sukcesu dopiero po zweryfikowaniu sum plików, obecności zadania autostartu, działającego procesu agenta, dwóch kolejnych heartbeatów oraz uruchomienia panelu. Jeśli którykolwiek etap się nie powiedzie, instalacja kończy się błędem zamiast komunikatem sukcesu.

Trwały log instalatora znajduje się w `C:\ProgramData\ServerBackuper\logs\installer.log`. Awaria agenta występująca przed uruchomieniem heartbeat jest zapisywana w `C:\ProgramData\ServerBackuper\logs\agent-startup.log`.

Instalator nie uruchamia backupu. Najpierw trzeba zapisać i przetestować konfigurację.

## Wpisanie ścieżki NAS

NAS jest opcjonalny. W sekcji „Ustawienia” zaznacz „Kopiuj backup także na NAS”, jeśli chcesz mieć drugą kopię sieciową. Po odznaczeniu tej opcji pola NAS są nieaktywne i zarówno backup ręczny, jak i harmonogram działają tylko lokalnie.

W sekcji „Ustawienia” w polu „Ścieżka NAS UNC” wpisz pełną ścieżkę udziału, na przykład:

```text
\\NAS\Backupy
```

albo:

```text
\\192.168.1.50\Backupy
```

Nie wpisuj `E:\Backupy`. Litery mapowanych dysków mogą być niewidoczne dla procesu działającego w tle.

Po wpisaniu ścieżki wybierz „Zapisz ustawienia”, a następnie „Testuj NAS”. Panel bezpośrednio utworzy mały plik testowy, odczyta go i usunie. Dostępny pasek pokazuje trwanie testu, a na końcu pojawia się okno z wynikiem.

Jeżeli NAS wymaga hasła, zaloguj się do udziału w Windows na tym samym koncie i zaznacz zapamiętanie poświadczeń. Agent działa jako zalogowany użytkownik i korzysta z jego dostępu sieciowego.

Program najpierw sprawdza integralność lokalnego archiwum przez 7-Zip, a dopiero później kopiuje je na NAS. Suma SHA-256 pliku lokalnego jest liczona już podczas kopiowania, bez osobnego dodatkowego odczytu. Po zakończeniu zapisu program ponownie odczytuje kopię z NAS, liczy jej SHA-256 i porównuje obie sumy. Dopiero zgodna kopia otrzymuje końcową nazwę.

## Pierwsza konfiguracja

1. Otwórz kartę „Ustawienia”.
2. Wskaż lokalny katalog główny całego serwera, na przykład `D:\Serwer`. Źródło UNC i mapowany dysk sieciowy, taki jak `K:` wskazujący na `\\serwer\udział`, są blokowane przed zatrzymaniem kontenerów.
3. Sprawdź katalog lokalny, domyślnie `C:\Backupy`.
4. Zdecyduj, czy zaznaczyć „Kopiuj backup także na NAS”. Jeśli tak, wpisz ścieżkę UNC do NAS.
5. Wskaż `7z.exe`, jeżeli program nie znajduje się w standardowym katalogu.
6. Wpisz nowe hasło archiwum, minimum 12 znaków. Nie używaj hasła zapisanego w starym ZIP-ie.
7. Pozostaw odstęp 48 godzin albo wpisz własny.
8. Wpisz datę pierwszego wykonania w formacie `RRRR-MM-DD`.
9. Wpisz godzinę `05:00` albo inną godzinę.
10. Ustaw liczbę kopii lokalnych, a przy włączonym NAS także liczbę kopii na NAS.
11. Wybierz „Zapisz ustawienia”.
12. Przy włączonym NAS wybierz „Testuj NAS” i poczekaj na wynik.

Harmonogram jest zakotwiczony w dacie pierwszego wykonania. Przykładowo pierwsze wykonanie 2026-08-22 o 05:00 oznacza następne 2026-08-24 o 05:00, potem 2026-08-26 o 05:00 itd. Jeżeli komputer ominie termin, agent wykona zaległy backup po uruchomieniu.

## Telegram

1. Utwórz bota w oficjalnym bocie `@BotFather` i skopiuj token.
2. Wyślij dowolną wiadomość do nowego bota.
3. Ustal Chat ID konta lub grupy.
4. Otwórz kartę „Telegram”.
5. Wpisz token oraz Chat ID.
6. Zaznacz „Włącz powiadomienia Telegram”.
7. Wybierz typy powiadomień: rozpoczęcie, sukces i błąd.
8. Wybierz „Zapisz Telegram”.
9. Wybierz „Wyślij wiadomość testową”.

Token jest szyfrowany przez Windows DPAPI. Nie jest zapisywany w ustawieniach jako jawny tekst i nie jest zapisywany w dzienniku.

## Ręczne wykonanie

1. Otwórz kartę „Stan”.
2. Wybierz „Uruchom backup teraz”.
3. Potwierdź ostrzeżenie o krótkim zatrzymaniu kontenerów.
4. Etap, procent i komunikaty dopisują się automatycznie co sekundę w polu „Przebieg bieżącej operacji”. Pole można czytać i kopiować w NVDA bez używania „Odśwież stan”.
5. NVDA będzie ogłaszać najważniejsze zmiany etapu.
6. Panel można zamknąć albo zminimalizować — okno ukryje się w zasobniku, a agent kontynuuje pracę.

Dwukrotne kliknięcie ikony Server Backuper w zasobniku ponownie otwiera panel. Menu ikony zawiera polecenia „Otwórz panel” i „Zakończ panel”. Polecenie „Zakończ panel” zamyka tylko interfejs; osobny agent backupu nadal działa.

„Anuluj operację” przerywa backup albo restore, ale nie przerywa procedury bezpieczeństwa. Agent osobnym, nieanulowanym tokenem ponawia uruchomienie wcześniej działających kontenerów, czeka na Nextclouda i ponawia `occ maintenance:mode --off`. Stan odzyskiwania jest kasowany dopiero po powodzeniu.

## Bezpieczny przebieg backupu

1. Agent sprawdza źródło, Docker i 7-Zip przed zatrzymaniem czegokolwiek. NAS sprawdza tylko wtedy, gdy kopiowanie na NAS jest włączone.
2. Odrzuca źródło UNC lub mapowany dysk sieciowy, zanim zmieni stan jakiegokolwiek kontenera.
3. Wykrywa kontenery należące do projektów Compose pod wybranym lokalnym katalogiem oraz kontenery używające jego bind-mountów i współdzielonych wolumenów.
4. Dla Nextcloud włącza maintenance mode. Dla MariaDB, MySQL i PostgreSQL tworzy strumieniowy, logiczny dump SQL.
5. Zapamiętuje działające kontenery i zatrzymuje je przez `docker stop`, bez wyłączania WSL oraz bez `docker compose down`.
6. Sprawdza `docker volume inspect`. Wolumeny CIFS, SMB, NFS, SSHFS, Rclone i WebDAV oraz wolumeny, których lokalności nie da się potwierdzić, są pomijane.
7. Eksportuje wyłącznie lokalne nazwane Docker volumes przez `tar`, zachowując linuksowe UID, GID i standardowe prawa dostępu.
8. Wykrywa liczbę procesorów logicznych i pamięć dostępną dla procesu, zapisuje wynik w logu, a następnie pakuje lokalny katalog źródłowy, dumpy baz, eksporty lokalnych wolumenów, listę projektów Compose i manifest migracyjny. Używa trybu bez kompresji `7-Zip -mx=0 -m0=Copy`, więc wydajność zależy głównie od szybkości odczytu i zapisu dysku, a nie od procesora. Dowiązania i junctiony są zapisywane jako łącza, bez przechodzenia do ich zawartości.
   Chronione katalogi Windows `$RECYCLE.BIN`, `System Volume Information` oraz techniczny katalog roboczy są pomijane.
9. Archiwum powstaje jako plik `.partial.7z`.
10. 7-Zip wykonuje test integralności i dopiero wtedy archiwum otrzymuje końcową nazwę.
11. Agent uruchamia dokładnie te kontenery, które działały wcześniej, i po uruchomieniu ponawia wyłączenie maintenance mode Nextcloud.
12. Po poprawnym utworzeniu archiwum od razu wykonuje retencję lokalną. Dzięki temu jej działanie nie zależy od dostępności NAS.
13. Jeśli NAS jest włączony, kopiuje sprawdzone lokalnie archiwum jako `.partial.7z`. Kopiowanie nie wymusza synchronicznego zapisu każdego pojedynczego bloku, a SHA-256 źródła jest liczony w tym samym przebiegu.
14. Ponownie odczytuje kopię z NAS i porównuje SHA-256. Dopiero po zgodnym wyniku nadaje końcową nazwę i wykonuje retencję. Jeśli NAS jest wyłączony, wszystkie etapy sieciowe są pomijane.

Podczas kopiowania panel pokazuje ilość przesłanych danych, średnią prędkość w MB/s i szacowany pozostały czas. Log zapisuje wykrytą specyfikację oraz osobne czasy pakowania, testowania lokalnego archiwum, kopiowania na NAS i weryfikacji SHA-256 NAS. Dzięki temu kolejny problem wydajności można przypisać do konkretnego etapu.

Jeżeli NAS zawiedzie, poprawny nowy backup lokalny pozostaje zapisany, a lokalna retencja nadal obowiązuje. Poprzednie poprawne kopie NAS nie są wtedy usuwane.

## Dlaczego miejsce na Synology może nadal być zajęte

Ustawienie „Liczba kopii na NAS” steruje tylko widocznymi archiwami `Kopia serwera *.7z` w skonfigurowanym katalogu. Program usuwa starsze archiwa zwykłą operacją usunięcia pliku i zapisuje w logu liczbę oraz pełne ścieżki usuniętych plików. Nie zarządza mechanizmami odzyskiwania danych Synology.

Jeżeli po opróżnieniu kosza miejsce nadal nie wraca, sprawdź w DSM:

1. Kosz udziału `homes`, używając opcji opróżnienia koszy wszystkich użytkowników „home”, a nie tylko kosza widocznego w File Station.
2. `Snapshot Replication` — migawki nadal wskazują bloki usuniętych dużych archiwów. Dla udziału wybierz `Snapshot > Calculate Size`.
3. `Synology Drive Admin Console` — historia wersji oraz osobny kosz Drive mogą przechowywać usunięte wersje plików.
4. `Storage Manager > Storage > Global Settings > Space Reclamation Schedule` na woluminie Btrfs. Zwolnienie bloków może nastąpić dopiero podczas zadania w tle.
5. Najnowszy log `C:\ProgramData\ServerBackuper\logs\server-backuper-YYYY-MM.log`. Wpis `Retencja NAS` pokazuje, ile archiwów program naprawdę usunął; ostrzeżenie retencji oznacza, że usunięcie nie powiodło się.

## Nextcloud i bazy danych

Nie wyłączaj całego WSL poleceniem `wsl --shutdown`. Docker Desktop korzysta z dystrybucji `docker-desktop`; jej zatrzymanie uniemożliwi eksport i odtworzenie wolumenów.

Dla projektu Nextcloud znajdującego się pod wybranym katalogiem źródłowym agent:

1. wykrywa cały projekt Compose, nie tylko kontener aplikacji;
2. włącza maintenance mode przez `php occ`;
3. tworzy logiczny dump MariaDB, MySQL albo PostgreSQL, jeśli rozpozna taki obraz;
4. zatrzymuje aplikację, bazę, tunel, Redis i inne kontenery należące do tego projektu;
5. kopiuje lokalny katalog Nextcloud i fizyczny katalog bazy oraz eksportuje wyłącznie lokalne nazwane wolumeny;
6. po utworzeniu archiwum ponownie uruchamia wcześniejsze kontenery i wyłącza maintenance mode.

Jeżeli katalog serwera jest dostępny jako mapowany dysk, na przykład `K:` wskazujący na `\\NAS\Kontenery`, nie może być źródłem backupu. Po przeniesieniu kontenerów na lokalny dysk wskaż ich nowy lokalny katalog główny. Lokalne bind-mounty Nextclouda, na przykład `nextcloud_data` i `db_data`, zostaną wtedy objęte archiwum.

W Audiobookshelf wolumen taki jak `nas_audiobooks`, którego opcje wskazują na CIFS/NFS/SMB, jest pomijany. Kopiowane są lokalne pliki Compose, konfiguracja i metadane znajdujące się w lokalnym źródle, ale nie są ponownie kopiowane audiobooki przechowywane na NAS-ie. Nazwa i powód pominięcia trafiają do zaszyfrowanego manifestu.

Logicznym dumpem SQL można posłużyć się ręcznie w nietypowej sytuacji, gdy fizycznego katalogu bazy nie da się uruchomić na innej wersji silnika. Automatyczne odtworzenie standardowej migracji korzysta z całego, zatrzymanego katalogu bazy.

## Przywracanie i migracja

1. Zainstaluj i uruchom agenta na komputerze docelowym.
2. Przygotuj pusty katalog docelowy, na przykład `L:\Serwer`. Nie wybieraj samego `L:\`.
3. Otwórz kartę „Przywracanie”.
4. Wybierz archiwum lokalne albo archiwum z NAS.
5. Wpisz hasło użyte podczas tworzenia tego archiwum.
6. Wybierz „Sprawdź archiwum”. Program testuje wszystkie dane, hasło, manifest i bezpieczne ścieżki.
7. Sprawdź odczytaną datę, pierwotną lokalizację, liczbę projektów Compose, kontenerów, wolumenów i dumpów baz.
8. Wskaż pusty katalog docelowy.
9. Pozostaw zaznaczone „Uruchom projekty Docker Compose po przywróceniu”, jeśli nowy Docker jest już skonfigurowany.
10. Wybierz „Rozpocznij przywracanie” i potwierdź ostrzeżenie.
11. Śledź etap oraz procent na karcie „Stan”. Panel można zamknąć, ponieważ operację prowadzi agent.

Przywracanie:

- najpierw rozpakowuje wszystko do katalogu staging obok celu;
- nie pozwala nadpisać niepustego katalogu ani głównego katalogu dysku;
- odtwarza nazwane wolumeny zgodnie z oficjalnym mechanizmem Docker `tar`;
- dopiero po poprawnym rozpakowaniu przenosi dane do pustego celu;
- opcjonalnie wykonuje `docker compose up -d` dla projektów zapisanych w manifeście;
- po uruchomieniu Nextcloud wykonuje `maintenance:data-fingerprint` i wyłącza maintenance mode;
- po błędzie lub anulowaniu próbuje uruchomić kontenery działające przed rozpoczęciem operacji.

Ostrzeżenie: zawartość nazwanych wolumenów wymienionych w manifeście zostaje zastąpiona. Dlatego przed rozpoczęciem program pokazuje osobne okno potwierdzenia. Testów automatycznych nigdy nie wykonuje się na produkcyjnym `K:\`; używany jest izolowany katalog tymczasowy.

## Dostępność NVDA

- wszystkie funkcje są dostępne klawiszem Tab i Shift+Tab;
- zakładki zmienia się Ctrl+Tab i Ctrl+Shift+Tab;
- polecenia z podkreśloną literą mają skróty Alt+litera;
- pola mają programowe etykiety i opisy;
- komunikaty stanu są regionem aktywnym UI Automation;
- karta „Stan” ma standardowy pasek postępu oraz tylko do odczytu pole przebiegu, aktualizowane automatycznie co sekundę bez przenoszenia fokusu NVDA;
- program używa standardowych kontrolek WPF, kolorów systemowych i dostępnej z klawiatury systemowej ikony zasobnika;
- dziennik i szczegóły historii są zwykłym tekstem możliwym do czytania i kopiowania.

Przed produkcyjnym użyciem należy przejść cały panel aktualną wersją NVDA i wykonać pierwszy ręczny backup kontrolny.

## Historia, stan i logi

Konfiguracja i historia znajdują się w:

```text
C:\ProgramData\ServerBackuper
```

Dzienniki tekstowe znajdują się w podkatalogu `logs`. Historia w panelu zachowuje 100 ostatnich prób. Jeśli agent nie odpowiada dłużej niż 15 sekund, panel pokazuje odpowiednią informację.

## Odzyskiwanie po awarii

Przed włączeniem maintenance i zatrzymaniem kontenerów agent zapisuje ich identyfikatory w trwałym stanie. Jeśli operacja zostanie anulowana, procedura odzyskiwania ignoruje anulowany token. Jeśli proces zostanie zabity albo komputer ulegnie awarii, agent po następnym uruchomieniu najpierw próbuje uruchomić zapisane kontenery i wyłączyć maintenance w rozpoznanym kontenerze Nextcloud. Dopóki odzyskiwanie się nie powiedzie, nie rozpoczyna następnego backupu.

## Odinstalowanie

Uruchom `Uninstall-ServerBackuper.exe` z rozpakowanej paczki albo wybierz Server Backuper w systemowych „Zainstalowanych aplikacjach” i użyj polecenia odinstalowania. Zaakceptuj okno UAC, przeczytaj listę usuwanych elementów i potwierdź operację.

Deinstalator:

- zatrzymuje i usuwa wewnętrzne zadanie agenta;
- zamyka agenta oraz panel działający w zasobniku;
- usuwa skróty i wpisy rejestru;
- usuwa `C:\Program Files\ServerBackuper`;
- usuwa `C:\ProgramData\ServerBackuper` razem z ustawieniami, zaszyfrowanym tokenem Telegram, historią i logami;
- sprawdza, czy wszystkie elementy rzeczywiście zniknęły, i zapisuje wynik w `%TEMP%\ServerBackuper-uninstall.log`.

Jeśli trwa backup, przywracanie albo agent ma kontenery wymagające odzyskania, deinstalator zatrzyma operację, aby nie pozostawić wyłączonych kontenerów lub trybu maintenance. Najpierw bezpiecznie anuluj operację w panelu i poczekaj na odzyskanie kontenerów.

Deinstalator celowo nie usuwa archiwów z `C:\Backupy` ani z NAS. Są to właściwe kopie danych użytkownika, a nie pliki instalacyjne programu.

## Budowa ze źródeł

Uruchom:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Test.ps1
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

Gotowy samodzielny pakiet x64 zostanie zapisany jako `ServerBackuper-win-x64.zip` w katalogu głównym projektu. Na komputerze docelowym nie trzeba instalować środowiska .NET.

Skrypt publikacji uruchamia testy, weryfikuje zawartość ZIP-a, zachowuje najwyżej jedną poprzednią paczkę w `niepotrzebne\poprzednia-wersja`, a następnie automatycznie usuwa odtwarzalne katalogi `bin`, `obj` i `release`. Dzięki temu każda kolejna wersja nie zwiększa stale rozmiaru aktywnego projektu.
