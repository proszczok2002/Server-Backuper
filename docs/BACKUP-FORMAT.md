# Format backupu

Backup jest zaszyfrowanym archiwum 7z z ukrytymi nazwami plików. Program używa metody `Copy` bez kompresji, szyfrowania 7zAES i hasła podanego przez użytkownika.

## Zawartość

Archiwum zawiera:

- pliki lokalnego katalogu źródłowego;
- `.serverbackuper-metadata/backup-manifest.json`;
- listę wykrytych projektów i kontenerów Compose;
- eksporty lokalnych nazwanych wolumenów w `DockerVolumes`;
- logiczne dumpy rozpoznanych baz w `DatabaseDumps`.

Pomijane są:

- `$RECYCLE.BIN`;
- `System Volume Information`;
- katalog roboczy `.serverbackuper-work`;
- zawartość sieciowych wolumenów Docker.

Dowiązania i junctiony są zapisywane jako łącza, bez przechodzenia do wskazywanej zawartości.

## Stan częściowy i poprawny

Podczas tworzenia i kopiowania używana jest nazwa zawierająca `.partial.7z`. Plik lokalny otrzymuje końcową nazwę dopiero po pełnym teście 7-Zip. Plik NAS otrzymuje końcową nazwę dopiero po zgodnym porównaniu SHA-256.

## Przywracanie

Restore najpierw sprawdza hasło, integralność, manifest oraz bezpieczne ścieżki. Dane są rozpakowywane do katalogu staging. Program nie nadpisuje niepustego celu ani katalogu głównego dysku. Nazwane wolumeny są odtwarzane po potwierdzeniu użytkownika.

Format manifestu jest wersjonowany polem `formatVersion`. Zmiana niezgodna wstecznie wymaga zwiększenia numeru formatu i migracji odczytu.
