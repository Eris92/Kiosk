# Configuration, central sync and management

## Source map
- src/Kiosk.Client/Configuration.cs: config records, JSON load/validation/save operations and compatibility.
- src/Kiosk.Client/Config.cs: effective configuration model and options.
- src/Kiosk.Client/CentralConfig.cs: ConfigSource share sync and local cached snapshot; machine shared config uses service-owned cache.
- src/Kiosk.Client/ConfigManager.cs and src/Kiosk.ConfigTool/Program.cs: folder-based configurations manager; launcher uses Kiosk.Client.exe --configs.
- src/Kiosk.Client/SettingsView.cs, AdminSettings.cs, FirstRunSetup.cs: settings UI, privileged actions and first-run.
- src/Kiosk.Client/SharedAccounts.cs: accounts.kiosk and machine-protected key; scripts/Set-KioskSharedAccount.ps1 provisions it.
- config/client.json, config/client.example.json, config/client.development.json: public/default and example configurations; config/intune/ShellLauncher.xml: example CSP.

## Runtime/config rules
- Installed config: C:\ProgramData\Kiosk\client.json; central ConfigSource may override runtime settings with a validated cached copy.
- Config editor entry: Ctrl+Alt+K (Windows admin) or authorized in-app UI. Settings reloading is part of the UI lifecycle.
- Machine sync and shared secrets cross the KioskCard service boundary; user-writable cache must not override machine configuration.
- Install/upgrade preserves existing configuration unless SourceConfig is explicitly supplied. Validate intended mode and authentication before shell changes.
- Do not persist production credentials, per-site secrets or local config exports in source.

## Checks
- Validate JSON plus client semantic rules via built-in --validate-config (called by build/deployment checks).
- Run scripts/Test.ps1 for config/edit conflict checks. Verify permissions and UNC access as machine identity on the target PC.
