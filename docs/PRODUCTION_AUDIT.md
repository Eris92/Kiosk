# Audyt wydania Kiosk — 2026-10-23

## Zakres i wynik

Repozytorium buduje klienta Windows x64 (.NET 10, WinForms/WebView2), opcjonalny natywny kafelek logowania oraz skrypty instalacji i wycofania. Przejrzano przepływ tożsamości i sesji, połączenia RDP, czytnik/CTAP, osadzanie okien, konfigurację, pakowanie i instalację. Usunięto stary mechanizm uruchamiania sesji oraz prywatne przykłady konfiguracji. Publiczna konfiguracja produkcyjna ma `WindowsShell`, `TestMode=false` i pustą listę serwerów RDP.

Kod można przekazać do testów akceptacyjnych na urządzeniu. Ten audyt **nie potwierdza gotowości produkcyjnej środowiska**: na tej stacji nie wykonano rzeczywistego logowania użytkowników A/B, połączenia z docelowym RDS, próby na konsoli blokowanej przez Windows, kompilacji natywnej MSVC ani podpisu certyfikatem organizacji. Paczka CI bez podpisu jest kandydatem testowym.

## Granica tożsamości

| Stanowisko | Tożsamość lokalnych procesów w `WindowsShell` | Profil RDP do sprawdzenia w środowisku |
| --- | --- | --- |
| Konto lokalne, Windows Hello | Token zalogowanego konta lokalnego | Zdalne poświadczenia wymagają własnej konfiguracji; Hello lokalnego konta nie mapuje go automatycznie do RDS. |
| Konto domenowe, certyfikat lub Hello | Token zalogowanego konta domenowego | `SmartCard` przekierowuje kartę; `WindowsCurrentUser` włącza Remote Credential Guard. Serwer, AD/PKI i polityki muszą zaakceptować metodę. |
| Konto Entra, Hello | Token zalogowanego konta Entra | `EntraId` wymaga FQDN oraz wspieranej konfiguracji klienta i hosta. Faktyczną tożsamość sprawdza się po stronie serwera. |

`WindowsShell` uruchamia Kiosk i lokalne EXE jako osobę zalogowaną do Windows. RDP uruchomione w osobnym procesie po funkcji „Zaloguj” również używa uwierzytelnionego tokenu tej osoby i zachowuje ustawioną metodę RDP. Wspólna sesja `Kiosk` bez osobnego logowania pozostaje pod kontem Windows uruchamiającym Kiosk. Odczyt certyfikatu/UPN z karty, UID, sama obecność karty i potwierdzenie Hello bieżącej sesji nie stanowią dowodu, że karta należy do tego tokenu. Karta bez certyfikatu PIV nie staje się kartą RDP.

PIN karty w trybie aplikacyjnym potwierdza podpis świeżego wyzwania wybranym kluczem certyfikatu; uprawnienie administracyjne z `cert:` wymaga takiego dowodu. `RequireCredentialPrompt` steruje oknem poświadczeń kontrolki RDP; Windows lub karta mogą użyć własnej pamięci poświadczeń. Ta opcja nie gwarantuje nowego żądania PIN przy każdym połączeniu. Logowanie Windows Hello i jego zasady PIN należą do Windows/organizacji.

## Zmiany poddane weryfikacji

- Konfiguracja odrzuca nieznaną wersję, błędne profile RDP, duplikaty, niepoprawne adresy i ścieżki, a produkcyjna lista administratorów nie przyjmuje UID jako uwierzytelnienia. Zapis jest atomowy.
- Sesje rozróżniają blokadę, odłożenie i wylogowanie; czas zachowania jest egzekwowany także po powrocie z blokady Windows. Na konsoli używany jest `LockWorkStation`, w sesji zdalnej rozłączenie WTS; brak potwierdzenia blokady powoduje próbę wylogowania.
- Kontrolka RDP stosuje zaufanie serwera/NLA, wybór metody i ustawienie pytania o poświadczenia. Przekierowanie karty jest włączone tylko dla profilu `SmartCard`. Każdy profil ma własną instancję kontrolki; pomocniczy proces zachowuje wybraną metodę i nazwę użytkownika.
- Przeglądarka, Notatnik i zgodne okna Win32 są w obszarze Kiosku. Osadzanie programu nie obejmuje dowolnej aplikacji Store/UWP, podniesionych okien ani każdej dodatkowej ramki/dialogu; zgodność konkretnego EXE wymaga testu.
- Polecenia uruchamiane z menu należą do sesji karty; jej zakończenie zamyka całą grupę procesów polecenia. Test uruchamia proces potomny i sprawdza zamknięcie obu procesów.
- Usługa karty uruchamia się bez inicjalizacji WinForms i zgłasza Windowsowi `Running` dopiero po utworzeniu chronionego pipe’a; gdy nie może go ponownie otworzyć, zgłasza błąd i kończy pracę. Przy zatrzymywaniu utrzymuje `StopPending` do zakończenia wątku obsługi. Skrypty instalacji zapisują stan do wycofania, nie dotykają już działających sesji i ograniczają ścieżki oraz ACL konfiguracji i dzienników. Pakowanie bierze tylko jawnie dozwolone pliki, sprawdza hash każdego składnika i opcjonalnie weryfikuje podpis. Źródłowe testy są oddzielone od binariów produkcyjnych.

## Dowody lokalne

`scripts/Test.ps1` przeszedł na tej stacji: składnia skryptów, JSON/XML, bezpieczny zakres ścieżki danych, odizolowany rollback HKCU, publikacja Release z ostrzeżeniami traktowanymi jako błędy oraz testy migracji konfiguracji, trybów RDP, PC/SC, PIN, sesji A/B, WebView2, okien Win32, menu, notatek i ustawień. Testy używają atrap i lokalnego interfejsu, więc nie dowodzą logowania do RDS ani obsługi fizycznej karty każdego typu.

Mikropomiar `--benchmark` na tym samym stanowisku (po rozgrzaniu, trzy przebiegi, 5000 niezmienionych odświeżeń paska i 1000 odczytów nieistniejącego czytnika):

| Ścieżka | Wcześniejszy build `out/audit-benchmark` | Po optymalizacji paska | Po pominięciu nieobecnego czytnika |
| --- | --- | --- | --- |
| Pasek/menu | 28–32 ms, 9 160 040 B | 0,9–1,1 ms, 40 B | 0,87–0,89 ms, 40 B |
| Odczyt czytnika | 1238–1275 ms, 120 080 B | 1235–1417 ms, 120 080 B | 0,025–0,026 ms, 80 B |

To test pojedynczych ścieżek, nie całej aplikacji; buildy różnią się również wersją .NET. Zmiana paska zmniejszyła koszt powtarzanego odświeżania. Ostatni pomiar czytnika to seria 1000 natychmiastowych pytań o wskazany, nieistniejący czytnik; po pierwszej odpowiedzi kolejne nie odpytują PC/SC, dopóki nie nadejdzie sekundowy termin odświeżenia topologii. Przy działającym, wskazanym czytniku obecność karty nadal jest sprawdzana co 250 ms. Nowy czytnik lub powracająca usługa mogą zostać zauważone do około 1 s później.

Weryfikator paczki przetestowano na izolowanym pakiecie: prawidłowy manifest przechodzi, ukryty plik spoza wykazu, dodatkowy EXE i ścieżka z przejściem do katalogu nadrzędnego są odrzucane. Te testy i parsowanie skryptów przeszły zarówno w PowerShell 7, jak i w Windows PowerShell 5.1. Pobrany artefakt z udanego CI dla rewizji 2eae10b przeszedł ponowną kontrolę pełnego składu po zaostrzeniu reguł; CI na `main` zbudowało natywną DLL i paczkę. Próba kompilacji tej DLL na stacji deweloperskiej kończy się brakiem Visual Studio Build Tools. Paczka pozostaje niepodpisanym kandydatem.

Lokalna usługa `KioskCard` była zarejestrowana jako LocalSystem/Automatic, ale zatrzymana (`ExitCode=1067`); jej binarium w Program Files różni się od bieżącego builda. Dziennik Service Control Manager zawiera wcześniejsze zdarzenia 7034 (nieoczekiwane zakończenie) i 7000 (przekroczony czas startu). Nie dowodzi to przyczyny, lecz wymaga osobnego sprawdzenia po aktualizacji usługi na urządzeniu testowym.

## Bramka akceptacyjna przed produkcją

1. Na urządzeniu testowym sprawdzić osobno lokalne Hello, konto domenowe certyfikat/Hello i Entra Hello wraz z kontem odzyskiwania; potwierdzić SID procesu, przeglądarki, każdej aplikacji i każdego serwera RDP. Sprawdzić kartę A/B, wymianę/wyjęcie, błędny/anulowany PIN, odpięcie czytnika, lock/unlock oraz limit zmiany użytkownika.
2. Zweryfikować docelowe serwery RDS: certyfikat TLS, NLA, mapowanie certyfikatu i PKI/CRL, Remote Credential Guard lub Entra ID, politykę poświadczeń/PIN oraz zachowanie odłączonych sesji. Tylko test TCP nie wystarcza.
3. Sprawdzić Windows Shell Launcher, natywny kafelek, aktualizację i rollback na dedykowanym obrazie Windows; użyć CI Windows z MSVC do kompilacji DLL. Nie zastępować działającej instalacji w Program Files podczas testu.
4. Podpisać finalną paczkę certyfikatem organizacji i sprawdzić `Verify-Package.ps1 -RequireSigned -TrustedSignerThumbprint ...`; dobrać politykę dystrybucji oraz retencję logów.
5. Przejść ręcznie obsługę wielu monitorów/DPI, dozwolone domeny przeglądarki/SSO, okna dialogowe zewnętrznych EXE i treść ekranów bez aktywnego użytkownika.

Polecenia: `scripts/Test.ps1`, `scripts/New-Package.ps1`, `scripts/Verify-Package.ps1 -PackagePath out/package` oraz `scripts/Test-Prerequisites.ps1 -ConfigPath <lokalna-konfiguracja> -CheckNetwork`. Kandydat bez podpisu nie powinien być wdrażany jako finalne wydanie.
