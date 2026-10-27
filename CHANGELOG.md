# Changelog

## [0.1.0] - 2026-10-27

First public release.

- Nested map of a whole drive, coloured by content, with animated zooming and search
  (`ext:`, `size:`, `older:`, `newer:`, `cat:` or part of a name).
- Fast scan that reads the NTFS master file table when run as administrator; folder walk otherwise.
- Sizes count hardlinks once, don't follow links, and use real on-disk size for compressed, sparse and
  cloud-only files.
- What grew: compares scans and lists the folders that explain the change.
- Clean up: Recycle Bin, temp files, app and shader caches, unfinished Steam downloads, hibernation file,
  older restore points, Windows component cleanup.
- Permanent delete by default, with in-use detection and delete-at-restart.
- Tray helper with per-drive free-space alerts.
- Installer and portable version for x64 and ARM64, with automatic updates from GitHub Releases.
