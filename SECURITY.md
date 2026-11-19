# Bezpieczeństwo

## Zgłaszanie podatności

Nie publikuj podatności, tokenów, haseł ani prywatnych logów w zwykłym Issue.

Użyj prywatnego zgłoszenia bezpieczeństwa GitHub:

<https://github.com/proszczok2002/Server-Backuper/security/advisories/new>

W zgłoszeniu podaj wersję programu, opis wpływu, kroki odtworzenia i — jeśli to możliwe — bezpieczny przykład bez prawdziwych danych.

## Obsługiwane wersje

Poprawki bezpieczeństwa są przygotowywane dla najnowszego publicznego wydania.

## Granice odpowiedzialności

- program działa z uprawnieniami konta Windows, które go uruchamia;
- dostęp do Dockera oznacza możliwość zarządzania kontenerami tego użytkownika;
- bezpieczeństwo udziału NAS, migawek, kosza i poświadczeń SMB pozostaje odpowiedzialnością administratora NAS;
- hasło archiwum i token Telegram są chronione DPAPI, dlatego ustawienia są związane z kontem i instalacją Windows;
- pliki wykonywalne nie są obecnie podpisane Authenticode;
- przed wdrożeniem produkcyjnym należy wykonać kontrolny backup i próbę restore na izolowanych danych.
