# Kiosk

Kiosk jest pulpitem pracy dla współdzielonego komputera Windows: dolne menu udostępnia nazwane połączenia RDP, przeglądarkę, Notatnik i skonfigurowane programy. Okna aplikacji można przełączać i rozmieszczać na monitorach. Zablokowanie lub odłożenie sesji ukrywa jej okna; zakończenie sesji zamyka jej lokalne programy. Zdalne sesje RDS są rozłączane bez wylogowania użytkownika serwera.

## Uruchomienie lokalne

Wymagane są Windows 11 x64, .NET 10 SDK do kompilacji oraz Microsoft Edge WebView2 Runtime do przeglądarki. Publikacja zawiera własny runtime .NET; na komputerze docelowym nie trzeba instalować SDK ani runtime .NET. RDP korzysta z systemowej kontrolki ActiveX. Czytnik musi obsługiwać PC/SC; uwierzytelnianie certyfikatem i operacje FIDO2 mają dodatkowe wymagania sprzętowe/polityk.

```powershell
.\scripts\Build.ps1
if (-not (Test-Path .\client.local.json)) { Copy-Item .\config\client.development.json .\client.local.json }
.\out\client\Kiosk.Client.exe .\client.local.json
```

Domyślna konfiguracja produkcyjna ma WindowsShell, TestMode=false, Notatnik, stronę example.org i pustą listę RDP. Do lokalnego testu użyj config/client.development.json (Kiosk, TestMode=true). Nie zawiera adresów, kont ani ścieżek konkretnego stanowiska. `client.local.json` jest ignorowany przez Git i **nigdy nie jest automatycznie kopiowany do publikacji**. Lokalny skrót powinien przekazywać pełną ścieżkę tego pliku jako pierwszy argument EXE. Zmiana binariów nie wymaga zmiany pliku lokalnego.

Jeżeli potrzebny jest osobny skonfigurowany katalog testowy:

```powershell
.\scripts\Build.ps1 -OutputPath .\out\local -ConfigPath .\client.local.json
.\out\local\Kiosk.Client.exe
```

`Build.ps1` publikuje do czystego katalogu tymczasowego i podmienia wskazany katalog dopiero po powodzeniu. Ponowna publikacja usuwa stare pliki z tego katalogu wynikowego. Dane i własne pliki konfiguracyjne przechowuj poza katalogiem publikacji. Nie uruchamiaj kompilacji do katalogu działającej instalacji w Program Files.

## Konfiguracja i tożsamość

**Menu → Konfiguracja** pozwala edytować listę RDP, aplikacje, zakładki i zasady sesji. Uprawnienia administratora Windows wynikają wyłącznie z faktycznie uwierzytelnionego tokenu Windows; nazwa/UPN certyfikatu karty nie dowodzi członkostwa w grupie administratorów. W produkcji wpis `cert:` na liście kart administratorów wymaga potwierdzenia PIN i podpisania świeżego wyzwania kluczem certyfikatu przed otwarciem ustawień; sam UID karty nie nadaje uprawnień. Jawnie włączony tryb testowy ma odrębne zasady. Nieznane pola i nieprawidłowa wersja konfiguracji są odrzucane. Plik produkcyjny powinien być zapisywalny wyłącznie przez administratorów. `config/client.example.json` jest wzorcem do dostosowania, a nie gotową konfiguracją sieci.

| Ustawienie | Znaczenie |
| --- | --- |
| `RdpConnections` | Lista unikalnych nazw, hostów, portów i metod logowania: `SmartCard`, `WindowsCurrentUser`, `EntraId`. |
| `RequireCredentialPrompt` | Żądanie systemowego okna poświadczeń RDP. Wyłączenie pozwala Windows użyć dostępnych poświadczeń; nie wyłącza NLA ani wymagań serwera/PIN karty. |
| `AutoConnectRdpName` | Nazwa istniejącego profilu otwieranego po odblokowaniu; pusty tekst wyłącza automatyczne łączenie. |
| `UnlockMethod` | `Card` = sama obecność rozpoznanej karty; `CardAndPin` = sprawdzenie PIN-u karty; `CardAndWindowsHello` = potwierdzenie przez Hello bieżącej sesji Windows. |
| `LockAfterSeconds` | Czas bezczynności do blokady; 10–86400 sekund. |
| `ChangeUserAfterSeconds` | Czas zachowania odłożonej sesji; 0 zachowuje ją do ręcznego zakończenia. Po upływie limitu lokalne aplikacje są zamykane. |
| `ReaderName` | Dokładna nazwa fizycznego czytnika; pusty tekst wymaga jednoznacznego wykrycia jednej karty. |
| `Bookmarks`, `BrowserUrl` | Nazwane adresy HTTP/HTTPS i strona startowa przeglądarki. |
| `BrowserOnlyBookmarks`, `BrowserAllowedDomains` | Ograniczenie nawigacji do domen zakładek i dodatkowych dozwolonych domen; logowanie SSO może wymagać dopisania domen dostawcy tożsamości. |
| `Applications` | Unikalna nazwa, pełna ścieżka EXE, argumenty i opcjonalny katalog roboczy; `kiosk:notatnik` oznacza wbudowany edytor. |
| `Commands` | Polecenia EXE/CMD/BAT/PS1 uruchamiane z menu w tle. `AsUser` wymaga zalogowania danej osoby w trybie Kiosk; proces i jego potomkowie kończą się z sesją. |
| `IdleScreens`, `IdleAfterSeconds` | Treści stron/aplikacji na monitorach i czas powrotu ekranu głównego do treści podczas braku aktywnego użytkownika. |
| `TestMode` | Dostępność funkcji testowych/wyjścia; pełny ekran sam w sobie nie ogranicza funkcji systemu Windows. |

Sama obecność/UID/ATR karty nie uwierzytelnia konta Windows ani Entra. Hello potwierdza użytkownika już zalogowanego do Windows. Dostęp lokalny, poświadczenia RDP oraz rzeczywisty użytkownik procesu są osobnymi elementami konfiguracji.

- **`SessionMode: WindowsShell`**: osoba loguje się do Windows własnym kontem przez dopuszczoną na urządzeniu metodę (lokalne Hello, domenowy certyfikat lub Hello, Entra Hello), a Kiosk działa jako jej powłoka. Programy działają w kontekście tokenu tego konta; SSO zależy od systemu, aplikacji i polityk. Na fizycznej konsoli blokada/wyjęcie karty blokuje Windows; sesja RDP komputera Kiosk jest rozłączana. Po upływie limitu zachowania sesji Kiosk może wylogować użytkownika i zamknąć programy.
- **`SessionMode: Kiosk`**: Kiosk przełącza własne sesje kart w jednej sesji Windows. Domyślnie programy mają tożsamość konta uruchamiającego Kiosk. To nie jest izolacja osobnych kont systemowych.
- **`SignInAsUser`**: opcjonalne logowanie innym kontem Windows/Entra w trybie Kiosk. System sprawdza login/hasło; aplikacje uruchamiane są jako uwierzytelniony użytkownik. Opcjonalne zapamiętanie konta na karcie FIDO2 przechowuje zaszyfrowaną kopię poświadczeń chronioną sekretem sprzętowym karty. To oddzielna funkcja od logowania Windows natywną kartą/Hello; oceń ją zgodnie z zasadami przechowywania poświadczeń organizacji.

Dla prawdziwych osobnych profili użytkowników i SSO użyj WindowsShell na prawidłowo przyłączonym/zarządzanym urządzeniu. RDP uruchamiane pod innym uwierzytelnionym kontem ma osobny proces pomocniczy `--rdp-host`; ten parametr jest wewnętrznym protokołem aplikacji, nie opcją zapisywania haseł w skrótach.

Na komputerze lokalnym Hello potwierdza tylko lokalne konto Windows. Nie tworzy samo mapowania tego konta do użytkownika domeny/Entra ani zdalnego RDS. Podobnie certyfikat odczytany z karty lub jego UPN nie wiąże automatycznie karty z sesją odblokowaną przez Hello. Jeśli wymagany jest ten sam użytkownik we wszystkich aplikacjach i RDP, zapewnij osobne logowanie do Windows każdego użytkownika i sprawdź tożsamość otrzymaną na serwerze dla każdego profilu RDP. W trybie wspólnego konta Kiosk tego wymagania nie można zagwarantować samą kartą i Hello.

Osadzanie zewnętrznych programów obsługuje zgodne okna Win32. Nie każda aplikacja Store/UWP, program wymagający podwyższenia uprawnień lub ponownie używający istniejącej instancji może zostać osadzony. Gdy aplikacja to wspiera, ustaw argument nowej instancji. Dodatkowe okna dialogowe mogą pozostać osobnymi oknami. Kiosk nie przejmuje cudzych wcześniej uruchomionych procesów. Zakończenie sesji zamyka jej uruchomione procesy: zapisz pracę przed wylogowaniem/wygaśnięciem sesji.

Przeglądarka w sesji Kiosk używa osobnego profilu InPrivate, niezależnego od prywatnego Edge. W kontekście zalogowanego konta Windows może korzystać z jego kontekstu/profilu dla SSO. Otwarcie strony nie dowodzi, że strona uwierzytelniła właściwego użytkownika.

## RDP i wymagania środowiska

Skonfiguruj rzeczywiste hosty FQDN i konta. Dla `SmartCard` potrzebne są certyfikaty logowania, mapowanie kont AD, zaufany łańcuch i osiągalne punkty CRL/OCSP, odpowiedni middleware oraz host RDS z NLA i zaufanym certyfikatem TLS. Karta FIDO2 bez certyfikatu PIV nie jest automatycznie kartą certyfikatową RDP. Metoda `EntraId` wymaga pełnej nazwy DNS oraz poprawnego dołączenia, polityk i zgodności urządzeń. Remote Credential Guard wymaga obsługi i odpowiednich zasad środowiska; samo ustawienie trybu w Kiosku nie konfiguruje tych zależności.

```powershell
.\scripts\Test-Prerequisites.ps1 -ConfigPath .\client.local.json
.\scripts\Test-Prerequisites.ps1 -ConfigPath .\client.local.json -CheckNetwork
certutil.exe -scinfo
```

Test TCP jest opcjonalny i ograniczony czasem; nie potwierdza uwierzytelniania RDP. Sprawdź kolekcję RDS: pojedyncza sesja na użytkownika, limity odłączonych sesji i zasady niewylogowywania. Kiosk domyślnie ogranicza przekierowania i automatyczne ponowne łączenie; polityki serwera muszą odpowiadać wdrożeniu. RD Gateway i pełna obsługa brokerów/farm nie są konfigurowane przez te skrypty.

Dokumentacja Microsoft: [smart cards and RDS](https://learn.microsoft.com/en-us/windows/security/identity-protection/smart-cards/smart-card-and-remote-desktop-services), [Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/), [WESL_UserSetting](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting).

## Sprawdzenie i paczka wdrożeniowa

```powershell
.\scripts\Test.ps1
.\scripts\New-Package.ps1 -OutputPath .\out\package-001
.\scripts\Verify-Package.ps1 -PackagePath .\out\package-001
```

Testy mają osobną publikację `out/self-test` z włączonym `KioskSelfTest`; zwykłe EXE nie zawiera testowych komend ani testowego generatora okien. Testy sprawdzają między innymi migracje konfiguracji, sesje/karty, osadzanie okien, ustawienia, notatki i WebView2. Testy skryptów wykonują walidację składni/JSON/XML oraz rollback na tymczasowym kluczu HKCU; nie instalują usług ani nie zmieniają polityk logowania.

Natywna DLL kafelka wymaga Visual Studio Build Tools z narzędziami C++ x64 i Windows SDK. Build stosuje `/W4 /WX`, statyczny CRT, CFG, ASLR i NX. Brak kompilatora jest błędem, a nie pominiętym testem. CI buduje/testuje na Windows, kompiluje również DLL i publikuje niepodpisany kandydat wdrożeniowy **Kiosk-win-x64-candidate**; wyniki testów są osobnym artefaktem.

`New-Package.ps1` zawsze buduje w nowym katalogu, kopiuje z listy tylko konfigurację domyślną, przykład i szablon Intune oraz wskazane skrypty wdrożenia. Plik rozwojowy `client.development.json` nie wchodzi do paczki. `package.json` zawiera rewizję źródeł, informację o zmianach śledzonych i nieśledzonych, stan Authenticode i SHA256 każdego pliku. Skrypt nie nadpisuje istniejącego pakietu. `Verify-Package.ps1` wykrywa pliki spoza wykazu, brak wymaganych składników oraz tryb testowy lub tryb sesji inny niż WindowsShell.

SHA256 potwierdza spójność paczki; nie potwierdza tożsamości wydawcy. Repozytorium nie zawiera certyfikatu ani prywatnego klucza podpisu. W kontrolowanym środowisku wydawania użyj certyfikatu organizacji z EKU Code Signing i kluczem prywatnym w magazynie `Cert:\CurrentUser\My` lub `Cert:\LocalMachine\My`. Podpisanie wymaga czystego drzewa Git i znanej rewizji; skrypt podpisuje własne EXE/DLL/skrypty przed obliczeniem hashy. `-RequireSigned` sprawdza zaufanie Authenticode, a `-TrustedSignerThumbprint` dodatkowo konkretny certyfikat. Serwer znacznika czasu dla PowerShell podaj jako `http://` zgodnie z obsługą `Set-AuthenticodeSignature`.

```powershell
.\scripts\New-Package.ps1 -OutputPath .\out\release-001 -SigningCertificateThumbprint '<ODCISK_CERTYFIKATU_ORGANIZACJI>' -TimestampServer 'http://timestamp.example.org'
.\scripts\Verify-Package.ps1 -PackagePath .\out\release-001 -RequireSigned -TrustedSignerThumbprint '<ODCISK_CERTYFIKATU_ORGANIZACJI>'
```

Weryfikator paczki odrzuca ukryte pliki spoza wykazu, dowiązania i junctions oraz nieprzewidziane skrypty i EXE. Opcja -RequireSigned sprawdza zaufany podpis każdego EXE/DLL i skryptu wdrożenia; -TrustedSignerThumbprint wiąże własne pliki Kiosku z certyfikatem organizacji. Hash manifestu nie uwierzytelnia samego manifestu, dlatego finalny pakiet należy pobrać z zaufanego kanału wydawania.

Nie publikuj plików PFX/kluczy, lokalnych konfiguracji ani zaszyfrowanego magazynu kont użytkowników. Podpisu organizacji i zachowania na docelowym Windows nie da się potwierdzić na samej maszynie deweloperskiej.

## Instalacja powłoki i opcjonalnych składników

Poniższe skrypty wykonuj w 64-bitowym PowerShell jako administrator. Obsługują `-WhatIf`. Uruchamianie klienta i testów nie wymaga instalacji powłoki, usługi ani kafelka. Przed zmianą ekranu logowania sprawdź procedurę odzyskania na urządzeniu testowym. Skrypty nie omijają execution policy; stosuj podpisy i politykę organizacji.

```powershell
.\scripts\Install-KioskShell.ps1 -WhatIf
.\scripts\Install-KioskShell.ps1 -SourceConfig .\config\client.json
# Opcjonalnie, jeżeli używasz weryfikacji FIDO2 przez usługę:
.\scripts\Install-KioskCardService.ps1
# Opcjonalne tło/kafelek systemowego ekranu logowania:
.\scripts\Set-KioskSignInScreen.ps1 -WhatIf
.\scripts\Install-KioskCredentialProvider.ps1 -WhatIf
```

Instalacja powłoki tworzy nowy katalog wersji poniżej Program Files i wskazuje go jako powłokę przyszłych logowań; działające sesje nie są zatrzymywane ani aktualizowane w miejscu. Konfiguracja jest ustawiana na WindowsShell i TestMode=false. Przed zmianami instalator sprawdza docelową konfigurację tym samym walidatorem co klient przy starcie; publikacja również odrzuca nieprawidłową konfigurację. Konfiguracja jest następnie podmieniana atomowo, bez chwilowego pustego pliku i z zachowaniem ACL. Plik docelowy musi leżeć bezpośrednio w `%ProgramData%\Kiosk`; skrypt ogranicza ACL katalogu i pliku do SYSTEM/administratorów (odczyt dla użytkowników). Przy ponownej instalacji zachowuje istniejącą konfigurację, więc zmiany `-SourceConfig` wymagają osobnej aktualizacji tego pliku. Enterprise/Education/IoT Enterprise używa Shell Launcher; administratorzy otrzymują Explorer. `DefaultProfile` jest wariantem dla nowych profili na pozostałych edycjach: już istniejące profile pozostają bez zmian. Funkcja Windows może wymagać restartu; skrypt zgłasza to przed zmianą powłoki. `config/intune/ShellLauncher.xml` jest osobnym szablonem dla zarządzanej dystrybucji do stałej ścieżki. Zachowuje Explorer jako domyślną powłokę i wymaga wpisania konkretnych istniejących kont standardowych do przypisania profilu Kiosk; wpis grupy Administrators nie jest zamiennikiem indywidualnego konta w tym szablonie. Dopasuj konta/ścieżki i sprawdź profil na urządzeniu testowym.

Usługa KioskCard instaluje odizolowaną wersję binariów w Program Files jako LocalSystem. Stan `Running` jest zgłaszany dopiero po przygotowaniu chronionego pipe’a. Instalator sprawdza też dwie kolejne odpowiedzi usługi bez używania karty lub PIN; przy błędzie przywraca poprzednią ścieżkę i sposób startu usługi. Usuwanie usługi pozostawia pliki. Stare katalogi wersji powłoki/usługi pozostają do kontrolowanego usunięcia po zakończeniu używających ich sesji.

Kafelek Kiosk opakowuje systemowy provider klucza zabezpieczeń; nie realizuje samodzielnie uwierzytelniania Windows. Domyślnie pozostawia standardowe opcje logowania. `-ReplaceWindowsSecurityKey`, `-MakeDefault`, `-HideOtherOptions` i `Set-KioskSignInScreen.ps1 -SecurityKeyOnly` to jawne zmiany polityk. Ukrywanie hasła/Hello lub `Install-KioskShell.ps1 -RequireSmartCard` może pozbawić lokalne konta możliwości zalogowania; wymaga osobnej walidacji rzeczywistego logowania i konta odzyskiwania.

### Rollback

```powershell
.\scripts\Install-KioskCredentialProvider.ps1 -Uninstall -WhatIf
.\scripts\Set-KioskSignInScreen.ps1 -Uninstall -WhatIf
.\scripts\Install-KioskShell.ps1 -Uninstall -WhatIf
.\scripts\Install-KioskCardService.ps1 -Uninstall -WhatIf
```

Po sprawdzeniu zakresu usuń `-WhatIf`. Skrypty zapisują poprzednie wartości w chronionym `%ProgramData%\Kiosk\DeploymentState`. Odinstalowanie odtwarza zarejestrowany stan, zamiast wymuszać wartości domyślne. Wartości polityk zmienione później przez inne wdrożenie są pozostawiane z ostrzeżeniem; listy wykluczonych providerów zachowują wpisy innych składników. Powłoka odmawia nadpisania późniejszej zmiany domyślnego shell, powłoki administratorów lub stanu Shell Launcher. Pliki instalacji, konfiguracja, obrazy i wcześniej utworzone profile nie są automatycznie usuwane. Rollback Shell Launcher nie odinstalowuje opcjonalnej funkcji Windows.

Instalacja starszą wersją skryptów nie ma dziennika: automatyczny rollback nie zgaduje poprzednich zasad. Przywróć wcześniejsze ustawienia z eksportu/GPO/Intune. Awaryjne wyłączenie starego kafelka wymaga usunięcia **wyłącznie jego** klucza `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{7A4D2C1E-5B3F-4E8A-9C6D-2F1B8E4A7C30}` oraz przywrócenia poprzedniej listy `ExcludedCredentialProviders`/domyślnego providera; w razie braku logowania wykonuje to administrator przez środowisko odzyskiwania i załadowany hive SOFTWARE. Nie kasuj całej listy providerów ani niezwiązanych polityk.

## Akceptacja przed wdrożeniem

Przejście kompilacji i testów lokalnych nie potwierdza produkcyjnego logowania. Sprawdź na rzeczywistym środowisku:

1. Kartę A/B, PIN/Hello, anulowanie i błędny PIN, wyjęcie/wymianę karty podczas dialogu, odpięcie czytnika i utratę sieci. Na fizycznej konsoli sprawdź odbiór zdarzeń blokady/odblokowania Windows oraz przełączanie kont. Osoba B nie może zobaczyć pulpitu ani programów A.
2. Wszystkie profile RDP: zaufany TLS/NLA, faktyczną tożsamość procesu i użytkownika zdalnego, wymagane ponowne żądanie PIN oraz zachowanie sesji RDS po rozłączeniu.
3. Blokadę i wygaśnięcie sesji, ponowne przyłożenie tej samej karty, zapis notatek i zgodność poszczególnych EXE z osadzaniem.
4. Przeglądarkę, SSO, zakładki/dozwolone domeny, profile użytkowników, zawartość wielu monitorów i różne skalowanie DPI.
5. Instalację, aktualizację, odinstalowanie i odzyskanie powłoki/kafelka na dedykowanym urządzeniu testowym; polityki organizacji i zachowanie już działających sesji. Potwierdzenie RDS/Entra i podpis certyfikatem organizacji są bramkami przed wdrożeniem produkcyjnym.

Logi klienta: `%LOCALAPPDATA%\Kiosk\Logs\YYYY-MM-DD.jsonl`. Logowanie diagnostyczne kafelka jest opcjonalne; sprawdź zasady retencji danych o użytkownikach. Ostateczne potwierdzenie obejmuje logi Windows/RDS i zachowanie rzeczywistych kart, czytników, kont i polityk.
