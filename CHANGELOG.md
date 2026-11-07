# Changelog

## 1.1.0 — 2026-11-07

- **Start with Windows is on by default.** Desktop widgets and the wallpaper slideshow come back after a restart without opening the app. If you turn it off in Settings, that choice is remembered. A startup task deleted outside the app is recreated.
- **Version-aware install.** A newer exe updates the installed copy; an older exe no longer downgrades it.
- **Automatic update checks** against GitHub Releases (daily, can be turned off). One-click install from Settings → Updates; downloads are verified by SHA-256 and, for signed builds, by publisher.
- Optional code signing and a SHA-256 checksum file in `build.bat`.

## 1.0.0

- First release: virus check, ROG / Windows service cleanup, browser and storage cleanup, wallpaper and lock screen, desktop widgets.
