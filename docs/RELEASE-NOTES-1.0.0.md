# Server Backuper 1.0.0

Pierwsze publiczne wydanie programu Server Backuper.

## Najważniejsze elementy

- szyfrowane archiwa 7z bez wymuszania kompresji;
- pełny test integralności lokalnego archiwum;
- opcjonalne kopiowanie na NAS z porównaniem SHA-256;
- osobna retencja kopii lokalnych i NAS;
- obsługa projektów Docker Compose, dumpów baz danych oraz lokalnych wolumenów;
- panel WPF z ikoną w zasobniku systemowym;
- instalator i bezpieczny deinstalator dla Windows x64.

## Instalacja

1. Pobierz `ServerBackuper-win-x64.zip`.
2. Rozpakuj całą paczkę.
3. Uruchom `Install-Agent.exe` i potwierdź UAC.
4. Skonfiguruj program i wykonaj pierwszy ręczny backup kontrolny.

Pliki wykonywalne nie są podpisane certyfikatem Authenticode, dlatego Windows SmartScreen może wyświetlić ostrzeżenie.

## Kontrola pliku

- rozmiar: `193608377` bajtów;
- SHA-256: `D2894EDE504B99A928EEAB50C38777F11AE40980D17FCC0A2E3C3C2AC3131280`.

Wydanie przeszło 17 testów regresji. Nie wykonano jeszcze tą wersją pełnego produkcyjnego backupu około 70 GB ani kompletnego ręcznego testu z aktualnym NVDA.
