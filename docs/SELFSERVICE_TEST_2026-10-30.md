# SelfService / WindowsSession — stan próby 2026-10-30

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
