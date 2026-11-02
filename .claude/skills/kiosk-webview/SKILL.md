---
name: kiosk-webview
description: Troubleshoot WebView2 sessions, Edge SSO, embedded apps, profile isolation, RDP and window lifecycle.
---
Load .ai/modules/browser-workloads.md. Trace identity acquisition from KioskForm/session to BrowserView/ApplicationView/RdpConfiguration and cleanup on sign-out. Keep per-user profile isolation, navigation/download controls, RDP TLS/NLA and safe child-process teardown. Validate exact call sites, then run tests with -ErrorsOnly. Real SSO and RDP acceptance require target systems.
