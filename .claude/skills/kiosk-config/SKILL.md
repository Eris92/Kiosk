---
name: kiosk-config
description: Change Kiosk JSON configuration, central config sync, editor, shared accounts or Intune templates safely.
---
Load .ai/modules/configuration.md. Identify exact config property and Configuration.cs/Config.cs validation, readers, writers and compatibility before modifying. Keep client.json free of secrets. Check machine-vs-user cache, ACL, ConfigSource fallback, concurrent save and explicit SourceConfig deployment semantics. Validate JSON and run targeted config/self-tests with -ErrorsOnly when possible.
