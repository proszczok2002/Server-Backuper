# Współpraca przy projekcie

Dziękujemy za zainteresowanie projektem Server Backuper.

## Zgłoszenia

- przed utworzeniem zgłoszenia sprawdź istniejące Issues;
- opisz wersję Windows, Docker Desktop i 7-Zip;
- podaj etap widoczny w panelu oraz odpowiedni fragment logu;
- usuń z logu nazwy hostów, ścieżki prywatne, adresy NAS, tokeny i inne dane wrażliwe;
- dla problemu z NAS podaj rodzaj połączenia oraz zmierzoną prędkość, ale nie publikuj poświadczeń.

## Zmiany w kodzie

1. Utwórz fork i osobną gałąź.
2. Zachowaj istniejący styl C# i dostępne etykiety kontrolek WPF.
3. Dodaj albo zaktualizuj test regresji.
4. Uruchom kompilację oraz `scripts\Test.ps1`.
5. Nie dodawaj `bin`, `obj`, `.tools`, ZIP-ów, logów ani ustawień z `C:\ProgramData\ServerBackuper`.
6. W Pull Request opisz zmianę, ryzyko oraz wykonane testy.

Zmiany dotyczące zatrzymywania kontenerów, retencji, restore, sekretów i deinstalacji wymagają szczególnie ostrożnego przeglądu.
