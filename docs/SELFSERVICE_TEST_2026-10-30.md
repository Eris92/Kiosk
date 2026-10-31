# SelfService — stan prób 2026-10-30

Aktualny pilot używa `SessionMode=SelfService` i jawnego `CardAuthentication=Sso` lub `Local` w konfiguracji. Konto stanowiska pozostaje zalogowane do Windows, a zakończenie sesji zamyka tylko konto osoby w Kiosku. Poniższy raport początkowy o `SelfServiceAuthentication=WindowsSession` dokumentuje starszy plan wdrożenia i nie opisuje obecnego przebiegu. `Test-Prerequisites.ps1` pokazuje aktualne pole `CardAuthentication`; repozytoryjny `config/client.json` ustawia `Sso` jawnie. Bieżące urządzenie jest Entra joined, ale konto LASER59\\kris ma `AzureAdPrt=NO` i `NgcSet=NO`; certyfikat Code Signing z kluczem prywatnym nie jest dostępny. Nie zaliczono jeszcze bramki dwóch zwykłych użytkowników i docelowych witryn SSO.

Punkt odniesienia: `main` na `dcea0baddbc3591697237d397bd2b0a2204a3d2b`.
To jest raport z przygotowania stanowiska i testów automatycznych, a nie odbiór produkcyjny.
Nie zapisano PIN-ów, haseł ani tokenów.

## Stan stanowiska

| Kontrola | Wynik |
| --- | --- |
| System | Windows 11 Business x64, build 26300; urządzenie dołączone do Entra |
| Bieżąca sesja | Konto lokalne; `NgcSet=NO`, `AzureAdPrt=NO` |
| Narzędzia | C++ Build Tools obecne; przypięty .NET SDK 10.0.402 doinstalowany w katalogu użytkownika |
| Bieżąca instalacja Kiosku | Konfiguracja w ProgramData istnieje, ma `SessionMode=WindowsShell`; powłoka to `explorer.exe`, usługa `KioskCard` jest wyłączona |
| Konfiguracja repozytorium | `SessionMode=SelfService`, `SelfServiceAuthentication=WindowsSession`; lista RDP pusta |
| Certyfikat organizacji | Nie znaleziono certyfikatu Code Signing z kluczem prywatnym w `CurrentUser\My` ani `LocalMachine\My` |

Bieżącej konfiguracji w ProgramData nie zmieniano. Ten komputer w obecnym stanie nie zapewnia sesji dwóch zwykłych użytkowników Entra ani dedykowanego obrazu kiosku.

## Wykonane kontrole

| Kontrola | Wynik |
| --- | --- |
| `scripts/Test-Prerequisites.ps1 -ConfigPath config/client.json` | Zaliczone; potwierdza konfigurację `SelfService/WindowsSession` i stan urządzenia, nie potwierdza SSO osoby |
| `scripts/Test.ps1` w PowerShell 7 i jego testy PowerShell 5.1 | Zaliczone po instalacji SDK 10.0.402; kompilacja .NET bez ostrzeżeń i błędów; testy skryptów, brokera, klienta i edytora przeszły |
| `scripts/Build-CredentialProvider.ps1` | Zaliczone; DLL MSVC z `/W4 /WX` zbudowana lokalnie |
| `scripts/New-Package.ps1` i `scripts/Verify-Package.ps1 -PackagePath out/package` | Zaliczone wyłącznie dla niepodpisanego kandydata: 307 plików, manifest SHA-256, `SourceRevision=dcea0ba...`, czyste źródło w chwili pakowania |
| `scripts/Install-KioskShell.ps1 -SourcePath out/package/client -SourceConfig config/client.json -WhatIf` | Zaliczone; jawnie wybrano plik `config/client.json`; na tej edycji Windows wybrano metodę `DefaultProfile`. Niczego nie zainstalowano |

Testy automatyczne sprawdzają logikę sesji, konfiguracji, przeglądarki i instalatora w izolacji. Nie zastępują interaktywnej próby Windows, Entra i witryn organizacji.

## Bramka akceptacyjna — otwarte próby

1. **Tożsamość i SSO:** na dedykowanym obrazie zalogować dwóch zwykłych użytkowników przez dopuszczone metody Hello lub kartę; osobno potwierdzić SID procesu Kiosku, profil WebView2 oraz SSO każdej docelowej witryny. Adresy witryn i konta testowe nie były dostępne w tej sesji.
2. **Granice sesji:** dla obu użytkowników sprawdzić „Zakończ sesję”, limit bezczynności, blokadę i odblokowanie, błędny oraz anulowany PIN i konto odzyskiwania. Zapisać tylko wynik i identyfikator scenariusza, bez sekretów.
3. **Obraz i wdrożenie:** sprawdzić Shell Launcher, natywny kafelek, aktualizację wersjonowanej instalacji oraz rollback na dedykowanym obrazie. Bieżący Windows 11 Business wybiera `DefaultProfile`, więc nie stanowi próby Shell Launcher. Instalację testową wykonać z jawnym `-SourceConfig` ustawiającym `SelfService/WindowsSession`, po sprawdzeniu istniejącego pliku w ProgramData.
4. **Ekrany i przeglądarka:** ręcznie sprawdzić wiele aktywnych monitorów i skalowania DPI, dozwolone domeny oraz przekierowania SSO, a także ekrany bez aktywnego użytkownika. Obecna publiczna konfiguracja ma pustą listę `BrowserAllowedDomains` i przykładową witrynę Google; nie jest konfiguracją docelowych witryn.
5. **Podpis:** uzyskać certyfikat Code Signing organizacji, zbudować `New-Package.ps1 -RequireSigned -SigningCertificateThumbprint <thumbprint>` na czystym checkout i sprawdzić `Verify-Package.ps1 -PackagePath <ścieżka> -RequireSigned -TrustedSignerThumbprint <thumbprint>`.

Starszy `CardBroker` nie został uznany za zweryfikowany. Tryb stanowiskowy i generowanie jego sesji pozostają poza tym odbiorem.

Stan bramki z `docs/PRODUCTION_AUDIT.md`: **0 z 4 punktów zamkniętych**. Testy automatyczne i niepodpisana paczka są przygotowaniem, nie dowodem gotowości produkcyjnej.

## Dalsza próba interaktywna — zmiana wymagań

Użytkownik wybrał następnie logowanie kartą wewnątrz Kiosku przy pozostawieniu konta stanowiska zalogowanego w Windows. Pilot przełączono jawnie na `CardBroker`, z `TestMode=false`, w osobnym katalogu i konfiguracji `KioskPilot`. Nie zmieniano konfiguracji dotychczasowej instalacji.

Na tym obrazie potwierdzono logowanie jednej osoby kartą i uruchomienie procesu strony pod jej kontem. Uruchamianie klienta z prywatnego katalogu użytkownika powodowało błąd `CreateProcessWithTokenW`; przeniesienie pilota do Program Files umożliwiło start procesu strony. Następnie ujawniono pustą stronę: nawigacja kończyła się sukcesem, lecz formularz i jego kontrolki pozostawały niewidoczne. Jawne `ShowWindow(SW_SHOW)` przy przywracaniu okna nakładki przywróciło stan widoczności formularza i przeglądarki.

Zgłoszono również powtarzający się ekran przycisków podczas uwierzytelniania, opóźniony komunikat czytnika, miganie zakładek i czarny ekran przed filmem. Lokalne poprawki ukrywają pola/przyciski podczas uwierzytelniania, pozwalają nowszemu komunikatowi czytnika zastąpić żądanie ponownego przyłożenia, pokazują nowy widok przed ukryciem poprzedniego i pokazują film po zdarzeniu rozpoczęcia odtwarzania. Wymagają ponownej próby interaktywnej; nie zmierzono jeszcze końcowego czasu reakcji czytnika ani nie potwierdzono SSO witryn i dwóch kont. Zimny start brokera nadal wymaga pomiaru i dalszej pracy.

Kolejna korekta UI ujednolica nagłówki etapów i dodaje animowany wskaźnik podczas uwierzytelniania. Na stanowisku usługa `KioskCard` pozostaje zatrzymana i wyłączona; pilot uruchamia broker administratora. Szybkie kolejne logowania wymagają dalszej pracy: sprzątanie starego brokera obejmuje procesy użytkownika w sesji Windows i przed rozgrzewaniem kolejnego logowania należy zawęzić je do własności konkretnej sesji Kiosku, zwłaszcza dla ponownego logowania tej samej osoby.

## Bieżąca przebudowa wspólnego interfejsu

Użytkownik potwierdził ustąpienie zawieszania po błędnym PIN-ie. Nie potwierdza to jeszcze czasu poprawnego logowania ani działania Local. Dodano cztery tryby, wspólny portal, wybór położenia nawigacji, ukrywanie pustych sekcji oraz integrację Local z przypisaniem konta. Szczegóły i otwarte bramki: UNIFIED_MODES_WORK.md.

31 października użytkownik potwierdził na uruchomionym pilocie Cleanup_20261030_2145, że zakończenie filmu przechodzi płynnie do następnego slajdu bez powrotu pierwszej klatki. To jest ręczny odbiór tego jednego przejścia; pozostałe scenariusze wideo i karty są nadal otwarte. Pełne lokalne `scripts/Test.ps1` po usunięciu starego pola SelfServiceAuthentication z formularza zakończyło się kodem 0. Zmiana formularza zachowuje starą wartość JSON przy zapisie i nie jest jeszcze w uruchomionym pilocie.

Nowe zgłoszenie dla Stanowiska: po zablokowaniu sesji przycisk „Wznów sesję” wymagał Windows Hello i zwracał `DeviceNotPresent`. Istniejąca konfiguracja Stanowiska ma jednocześnie `CardAuthentication=Sso` i starszy `UnlockMethod=CardAndWindowsHello`. Kod roboczy daje pierwszeństwo aktywnemu trybowi karty: przy powrocie do zaparkowanej sesji żąda PIN-u karty, a okna i procesy osoby pozostają przypisane do jej sesji. Osobna konfiguracja pilota z `ChangeUserAfterSeconds=0` ma utrzymywać trzy sesje do ręcznego wylogowania; istniejącego pliku w ProgramData nie zmieniono. Pełne testy automatyczne przeszły, lecz automatyczna kontrola wykonania odrzuciła kopiowanie pilota do Program Files (`blocked by policy`). Nie ma jeszcze ręcznej próby A/B/C z nową wersją ani sprawdzenia poprawnego PIN-u przy powrocie. `out/package-station` jest niepodpisana, zweryfikowana tylko manifestem SHA-256.

Wcześniejszy opis pokazywania nowego widoku przed ukryciem poprzedniego jest nieaktualny: obecnie poprzedni widok jest ukrywany przed pokazaniem kolejnego. Film korzysta z podglądu pierwszej klatki i odrzucania zdarzeń starszej generacji. Obie zmiany wymagają oceny wizualnej najnowszego pilota; same testy automatyczne nie dowodzą braku migania.
