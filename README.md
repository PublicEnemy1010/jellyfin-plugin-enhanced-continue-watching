# Enhanced Continue Watching

## A Jellyfin Plugin

Continue Watching updates Jellyfin's default resume list to work like you expect it to.

## Credits

This is a fork of [Continue Watching](https://github.com/tim-vu/jellyfin-plugin-continue-watching)
by [tim-vu](https://github.com/tim-vu). The original plugin, its design, and the full
upstream commit history are their work; this fork branches from upstream `v1.0.1.0`. All credit for
the original plugin goes to them. Please report issues with unmodified behavior upstream.

### Changes in this fork

- Home videos are tracked alongside movies and series.
- Marking an item played or unplayed updates Continue Watching immediately, advances to the next
  unwatched episode, and resurfaces finished shows when new episodes arrive.
- "Remove from Continue Watching" in the card and details-page menus.
- Play counts accumulate correctly, count one viewing once, and survive an unwatch.
- Entries that haven't been played in 180 days drop off automatically.
- Resume positions and played state are cleared correctly when rewatching or marking watched.
- Hardening so plugin event handlers and incomplete episodes can't crash Jellyfin.
- Fewer redundant Continue Watching refreshes in Jellyfin Web.

## Features

- Tracks movies and series.
- One entry per in-progress series.
- Home Screen Sections integration.
- No separate client patching required.

## Installation

### Prerequisites

- Jellyfin `12.0.0` or later
- Optional: the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  plugin, for the "Remove from Continue Watching" menu entry and instant row refreshes in Jellyfin
  Web. Without it the resume list still works in every client.

### Steps

1. In the Jellyfin dashboard, open the plugin repository settings and add this repository:

   ```text
   https://plugins.enemyvault.com/manifest.json
   ```

2. Install `Enhanced Continue Watching` from the plugin catalogue.
3. Restart Jellyfin.
4. The Continue Watching section will only reappear once you start watching a movie or series.

### Switching from upstream Continue Watching

This fork has its own plugin ID, so it installs separately. Uninstall `Continue Watching` and restart
Jellyfin before installing `Enhanced Continue Watching`; running both at once is not supported. Your
existing Continue Watching entries carry over.

### Home Screen Sections

If you use Home Screen Sections, use the new `Continue Watching` section at the bottom instead of the built-in `Continue Watching` up top.

---

This project is licensed under the GPL-3.0 License, as is the upstream project it is derived from.
See the [LICENSE](LICENSE) file for details. Original work © tim-vu; modifications © PublicEnemy1010.

AI was used to aid the development of the project.
