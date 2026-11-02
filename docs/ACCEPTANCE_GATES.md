# Bramki odbioru na urządzeniu

Zielone `scripts/Test.ps1`, CI i weryfikacja niepodpisanej paczki dowodzą tylko budowy i regresji automatycznych. Nie dowodzą SSO, fizycznej karty, Shell Launcher ani podpisu wydawcy. Gotowość produkcyjna wymaga zamknięcia poniższych bramek oraz pozostałych otwartych błędów.

Wynik wpisuje osoba wykonująca próbę na urządzeniu. Puste pola oznaczają brak potwierdzonej próby na tej wersji. Tryb `CardBroker` wymaga osobnej próby na dokładnym obrazie Windows; wcześniejsze próby `WindowsSession` nie zaliczają tych bramek.

| Bramka | Zakres | Wynik | Data | Obraz Windows | Commit |
| --- | --- | --- | --- | --- | --- |
| [#9](https://github.com/Eris92/Kiosk/issues/9) | SSO: dwa konta Entra, profil przeglądarki, docelowe witryny | | | | |
| [#10](https://github.com/Eris92/Kiosk/issues/10) | Local: PIN, przypisane konto Windows, izolacja programów | | | | |
| [#11](https://github.com/Eris92/Kiosk/issues/11) | Sesje: przełączanie A→B→C→A z zachowaniem aplikacji | | | | |
| [#18](https://github.com/Eris92/Kiosk/issues/18) | Shell Launcher, aktualizacja i rollback na dedykowanym obrazie | | | | |
| [#19](https://github.com/Eris92/Kiosk/issues/19) | Podpisana paczka, certyfikat organizacji, jawna konfiguracja | | | | |
| [#20](https://github.com/Eris92/Kiosk/issues/20) | Konto odzyskiwania, błędny/anulowany PIN, bezczynność | | | | |

## Przed wypchnięciem do `main`

Podaj procent zamkniętych bramek (zaliczone / 6) i listę pozostałych. Przy każdym wyniku podaj commit, ponieważ wynik nie przenosi się na późniejszą wersję bez ponownej oceny.
