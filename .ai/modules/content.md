# SelfService content, news and help flows

## Source map
- src/Kiosk.Client/News.cs, NewsSlides.cs, NewsVideo.cs: kiosk-news.json data, slides, images/videos and rendering behavior.
- src/Kiosk.Client/SharePointNews.cs, NewsAccount.cs: remote SharePoint news integration and account context.
- src/Kiosk.Client/Portal.cs, Help.cs, KioskForm.SelfService.cs: SelfService tiles, categories, help actions and pages.
- src/Kiosk.NewsEditor/Program.cs, MainForm.cs, NewsFile.cs, MediaCopy.cs, StatsView.cs: KioskKomunikaty editor, media and statistics.
- src/Kiosk.Client/Stats.cs, Retention.cs: local statistics and retention.
- config/news/ and config/news/kiosk-news.json: example messages and assets.

## Flow / boundaries
- News editor writes content assets and a news manifest; runtime reads local/share/SharePoint sources selected by configuration.
- External links, media paths, content actions and SharePoint authentication cross untrusted data / remote system boundaries.
- Respect size limits, path validation, session cleanup and safe network handling in relevant code.
- Avoid scanning binary .mp4/.png assets for ordinary code requests.

## Validation
- scripts/Test-SharePointNewsScript.mjs (when Node.js is available); scripts/Test.ps1 also runs editor self-tests.
- UI/media/SharePoint behavior on real tenant and target device must be checked separately.
