# Kiosk — stan i pozostała praca (2026-10-28)

## Gdzie jesteśmy

`main` zawiera połączone tryby `SelfService`, `Kiosk` i `WindowsShell`. Pierwszy planowany do wdrożenia jest `SelfService` z `SelfServiceAuthentication=WindowsSession`: Windows loguje osobę kartą lub Hello, a Kiosk pokazuje Szybką pomoc w jej profilu WebView2. Kod i paczka przechodzą lokalny `scripts/Test.ps1` oraz CI, w tym budowę natywnego kafelka i weryfikację zawartości niepodpisanej paczki. Są to dowody dla kodu i pakowania, nie dla docelowego SSO ani RDP.

Na tym komputerze działa usługa `KioskCard` ze starszej wersji `release-db6189b36536431287400bb7f9b6d941`. Zainstalowany `%ProgramData%\Kiosk\client.json` ma tryb `SelfService`, ale nie zawiera pola `SelfServiceAuthentication`, więc zachowuje starszą ścieżkę `CardBroker`. Bieżącego klienta, usługi i konfiguracji produkcyjnej nie aktualizowano do ostatniego commitu; pilotaż należy prowadzić na odizolowanym stanowisku.

## Etap 1 — uruchomienie SelfService

| Zadanie | Stan | Dowód zakończenia |
| --- | --- | --- |
| Ekran Szybkiej pomocy, komunikaty, strony HTTPS, licznik i zakończenie sesji | Kod i lokalne testy gotowe | Ręczne przejście całej ścieżki na stanowisku docelowym. |
| WindowsSession i SSO | Kod i lokalny test konta Windows gotowe | Dwie rzeczywiste osoby logują się kartą/Hello na komputerze Entra lub hybrydowym; docelowe strony otwierają się z ich tożsamością bez ponownego podawania hasła. |
| Bezpieczeństwo sesji | Część logiki sprawdzona testami | Próba karty A/B, wyjęcia, błędnego i anulowanego PIN-u, blokady/odblokowania, uśpienia, limitu bezczynności, wylogowania i konta odzyskiwania. |
| Instalacja jako powłoka Windows | Skrypty i `-WhatIf` sprawdzone | Instalacja, aktualizacja, restart i wycofanie na dedykowanym obrazie Windows, razem z Shell Launcher i właściwą polityką klawiatury. |
| Podpis i dystrybucja | Weryfikator i niepodpisany kandydat gotowe | Paczka podpisana certyfikatem organizacji przechodzi `Verify-Package.ps1 -RequireSigned -TrustedSignerThumbprint`; ustalone są kanał dystrybucji i retencja logów. |
| Wygląd i urządzenia | Lokalne testy UI gotowe | Ręczna kontrola wielu monitorów, DPI, czytnika, treści ekranów i okien dialogowych na docelowym stanowisku. |

Komputer testowy Entra nie jest teraz dostępny. Z tego powodu brak potwierdzenia prawdziwego tokenu Entra i SSO na firmowych witrynach. Starszy `CardBroker` pozostaje dla istniejącej instalacji, lecz jego zależność od konkretnej wersji Windows wymaga osobnej próby z fizyczną kartą; docelowa ścieżka SSO pierwszego wdrożenia to `WindowsSession`.

## Etap 2 — stanowisko z aplikacjami i RDP

Menu, osadzona przeglądarka, notatki, pliki, aplikacje i lista profili RDP są w kodzie i mają lokalne testy. Przed wdrożeniem trzeba:

1. Dokończyć wariant logowania hasłem domenowym z przypisaniem karty dla komputerów bez Entra. Tego przepływu nie ma jeszcze jako kompletnej, zweryfikowanej funkcji.
2. Na komputerach lokalnych, domenowych i Entra sprawdzić, że procesy przeglądarki, aplikacji i każdej sesji RDP działają w zamierzonej tożsamości Windows. Lokalny Hello nie przekazuje automatycznie poświadczeń do zdalnego RDS.
3. Sprawdzić docelowe serwery RDP: TLS, NLA, certyfikaty, PKI/CRL, mapowanie użytkownika, metodę Entra lub Remote Credential Guard, pytanie o poświadczenia i zachowanie po rozłączeniu.
4. Przejść ręcznie aplikacje, które otwierają okna pomocnicze lub używają już działającej instancji, oraz test przełączania wielu osób i monitorów.

## Trwający audyt kodu

Zakończono szeroki przegląd konfiguracji, sesji, CTAP/PCSC, WebView2, RDP, okien Win32, usługi, pakowania i instalatorów. Usunięto przestarzały aktualizator w miejscu oraz nieużywany stary mechanizm sesji. Przegląd końcowych ścieżek awarii procesów i długotrwałej pracy nadal trwa; wykryte poprawki są testowane i wypychane do `main` w osobnych commitach. Ostatnie przykłady to asynchroniczny odczyt potoku usługi, migawki uprawnień brokera i ograniczenie pamięci podręcznej kont.

Orientacyjnie pozostaje około 39% pracy do wydania produkcyjnego. Liczba obejmuje testy integracyjne i wdrożeniowe, nie jest miarą pokrycia kodu. Wyniki prób na docelowym sprzęcie mogą zmienić zakres pozostałych poprawek. Szczegółowe ustalenia i bramka akceptacyjna są w `docs/PRODUCTION_AUDIT.md`.
