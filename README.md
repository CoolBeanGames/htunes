# hTunes

A Windows music library and iPod companion built with C# and WPF.

## Current library prototype

- Browse by artist, album, genre, or song
- Single-panel navigation: artists → albums → songs, albums/genres → songs, and podcast shows → episode pages, with Back buttons
- Show source format and bitrate for library and iPod music
- Import audio using File → Add files or drag files/folders from Explorer; folder imports include all nested folders
- Multi-select with Ctrl/Shift and batch-edit metadata and artwork
- Show embedded or assigned album artwork as albums and songs are selected
- Create playlists and drag selected songs onto them
- Play local tracks and track play counts
- Detect a mounted stock-OS iPod and show its name and storage capacity
- Browse music currently stored on the iPod from a connection-only iPod tab
- Sync selected songs, albums, artists, or genres by dragging them onto the iPod strip
- Drag a local playlist onto the iPod strip to sync its music and named playlist in order
- Sync the complete compatible library with random space-filling when capacity is limited
- Optionally transcode during sync to MP3, AAC, or Apple Lossless at selectable bitrates
- Replace an existing iPod copy when the selected sync format or bitrate changes
- Back up and transactionally update the stock iPod music database
- Reconcile play counts in both directions at startup, hot-plug, and during connected playback
- Safely eject the iPod through Windows
- Persist the library under the current Windows user's local application data
- Remember the selected transcoding preset between launches
- Right-click songs, artists, albums, genres, playlists, podcast shows/episodes, search results, or the iPod strip for relevant actions
- Context menus preserve Ctrl/Shift selections and support playing, syncing, metadata/artwork editing, playlist membership, download management, and removal
- File/Edit/View/Playback menus expose the same actions, with working tab/category navigation and a persistent Settings window
- Undo/Redo supports up to 100 local edits per session: imports, metadata/artwork, library removal, playlist edits, subscriptions, sync rules, and manually changed podcast played state
- Search Apple Podcasts or subscribe directly with an RSS feed URL
- Browse subscribed shows and episode artwork, numbers, dates, played state, and download state
- Download, play, delete, and mark individual podcast episodes played or unplayed
- Configure each show to sync a chosen number of its newest or oldest unplayed episodes
- Download missing episodes during podcast sync and mirror those retention rules to the iPod
- Write the stock iPod podcast groups and browse on-device episodes under the iPod tab's Podcasts category
- Remove completed podcast downloads from both hTunes and the iPod when playback finishes
- Synchronize podcast bookmark positions, show elapsed time and percentage, and treat 50% playback as played by default (configurable)
- Preserve podcast and music play-count changes during the same startup/hot-plug reconciliation

Run with `dotnet run --project HTunes.App`.

## FFmpeg

Library imports never transcode audio. By default files stay in place; Settings also supports verified copies or moves into a managed music folder. Conversion only happens while copying music to an iPod, using temporary files that are removed after the sync.

On startup, hTunes checks for missing FFmpeg and yt-dlp installations and compares installed copies with the latest published releases. If either is missing or outdated, it offers to download the needed tools into the current user's local hTunes data folder. The downloaded files are checked against their published SHA-256 values and do not require an administrator installation. Use **File → Update FFmpeg and yt-dlp…** to update or reinstall both tools at any time.

For a transcoding preset, hTunes can also use `ffmpeg.exe` in one of these locations:

- Beside the hTunes executable
- In a `tools` folder beside the hTunes executable or current Visual Studio working directory
- In the location named by the `FFMPEG_PATH` environment variable
- Anywhere on Windows `PATH`

Selecting **Do not transcode (original)** does not require FFmpeg.

## Context-menu checks

Run `dotnet run --project tests/HTunes.ContextMenuChecks` to check list/grid multi-selection, top-menu rebuilding, undo/redo branching and limits, failure handling, settings round-trips and validation, safe copy/move imports, podcast selection/played policies, URL redaction, Settings UI layout, and preservation of play counts during metadata undo. These checks use disposable test files and isolated WPF controls; they do not launch the main app, load your library, change startup registration, or access an iPod.

**Remove from library** leaves original audio files on disk. **Remove from this playlist** only changes playlist membership; **Delete playlist** leaves its tracks in the library. Podcast **Delete downloaded files** removes local downloads but retains episode entries and played state.

## Menus and undo/redo

- **File:** add files/folders, create playlists, find/subscribe to shows, sync music/podcasts, eject, update tools, and exit.
- **Edit:** Undo/Redo, Select all, and actions for the last selected track/group/playlist/show/episode.
- **View:** switch tabs and library categories, search, and refresh.
- **Playback:** play/resume, pause, stop, previous, and next.
- **Settings:** storage locations, import behavior, iPod automation, future yt-dlp options, podcast defaults/policies, tool updates, and debug logging.

Shortcuts: `Ctrl+Z` Undo, `Ctrl+Y` or `Ctrl+Shift+Z` Redo, `Ctrl+A` Select all, `Ctrl+O` Add files, `Ctrl+N` New playlist, and `Ctrl+F` Search. Text fields retain their native text-editing commands.

### Single-panel browsing

Music keeps the category/playlist sidebar, but the working area shows only one page at a time. Click an artist to open their albums, then an album to open its songs. Albums and genres open directly to their song lists; Songs shows the full library. The iPod tab uses the same layout. Back (or `Alt+Left`, also available under View) returns one level and reselects the item you opened. Clicking a sidebar category returns to that category's root.

An ordinary click opens a group on mouse release; `Ctrl`/`Shift` clicks and `Ctrl+A` select groups without navigating, and dragging keeps the current page open. You can also select with the keyboard and press Enter to open a single item. Context menus still act on all selected artists/albums/genres. Search filters the current music page, and refresh keeps the current drill-down. Playlists display their songs full-width in playlist order.

Podcasts starts with a full-width subscribed-show list and search. Click a show (or select it and press Enter) to open its artwork, sync rule, actions, and episodes. Back returns to the show list; right-clicking a show selects it without opening it. Download/playback refreshes keep the show page open. Find/Search returns to the show list so the search box is available.

The automated checks exercise these pages using an isolated MainWindow with services disabled: no real library, preferences, network refresh, device detection, or timers. For optional sample-data layout snapshots, set `HTUNES_UI_CHECK_OUTPUT` to an output directory before running the checks.

History is session-only and does not reverse iPod sync/eject, tool updates, downloads/file deletion, or automatic listening counts/progress. Undoing a podcast unsubscribe or manual played-state change restores its local state, **not deleted downloads or iPod copies**; download/sync again as needed. Metadata edits and their undo affect library metadata only, as before.

## Settings

Preferences are saved in `%LOCALAPPDATA%\hTunes\settings.json`, with the previous version in `settings.json.bak`. Existing preset-only settings receive defaults for the new options. Settings use Save/Cancel; opening the dialog has no effect. Changing the sync-bar transcode preset preserves all other preferences.

### Storage and imports

- Choose separate locations for future Download-tab output, managed music, and podcasts.
- **Reference** (default) leaves imported music in place. **Copy** keeps the source and imports a verified copy. **Move** makes and verifies a copy, saves the library entry, then verifies both files again before removing the source. A failed copy/save/verification keeps the original.
- Existing destination files are never overwritten; filename collisions receive numbered suffixes. Files already inside the managed folder are referenced in place.
- Changing a location does not relocate old files. Previously downloaded episodes retain their recorded paths. New podcast downloads and artwork use the chosen podcast folder.
- Undoing an import changes library entries only; it does not move originals back or delete copied files.

### iPod automation

Both automation switches default **off**. “Open hTunes when an iPod is connected” enables a notification-area watcher and registers `hTunes --watch-ipod` for the current Windows user's sign-in. Closing the library window keeps the watcher running; the tray menu can reopen it or exit. File → Exit stops it until the next manual launch or Windows sign-in. Turning the setting off removes the startup entry and restores normal close-to-exit behavior. Keep the executable in a stable location; after moving it, toggle the setting off/on to update its registration. Only one hTunes instance runs per Windows session.

Automatic connection sync runs once after opening/connecting and reconciling listening progress, when startup checks and other operations are finished. Choose music, podcasts, or both. Music uses the remembered transcode preset and existing random-fill behavior when space is limited; podcasts use saved per-show rules and the global policies below. Changing settings does not immediately start a sync on an already connected device. Errors remain visible and are logged when enabled; successful automatic syncs do not interrupt with summary dialogs.

### yt-dlp options

Save audio format, quality/bitrate, embedded metadata/artwork, playlist-name-as-album, whole-playlist behavior, and playlist subfolders. These are **preparatory settings for the upcoming Download tab**, not a working YouTube downloader yet. The argument builder uses individual arguments rather than a shell command. Audio conversion and metadata options follow the [official yt-dlp documentation](https://github.com/yt-dlp/yt-dlp#post-processing-options).

### Podcasts

- Default episode count and newest/oldest order apply to **new subscriptions**; existing shows keep their individual rules.
- Include or exclude manually downloaded unplayed episodes outside the per-show rule (included by default).
- Refresh feeds on first opening the Podcasts tab (default on), and optionally download the rule-selected episodes after a refresh (default off).
- Download missing episodes on sync (default on). If disabled, all selected episodes must already be downloaded, otherwise sync stops before modifying the iPod.
- Mirror selections during **Sync all podcasts** (default on). Turning this off adds/updates selected episodes without pruning other unplayed managed episodes. Explicit per-episode/show sync remains additive.
- Choose the played threshold (1–100%, default 50%) for both app progress and iPod bookmarks. An iPod-reported completed play also counts as played. Changing the threshold does not unmark episodes already played.
- Automatically delete local played downloads (default on). Active app playback keeps its file open until playback stops. Played iPod copies are still removed during reconciliation; disabling local deletion does not change iPod cleanup. Explicit Delete Download still removes files.

### Tools and troubleshooting

Enable/disable startup update checks for FFmpeg and yt-dlp; missing-tool warnings remain enabled. Both File and Settings offer a manual update/reinstall action with the existing download confirmation and checksum verification.

Debug logging is opt-in. Logs live at `%LOCALAPPDATA%\hTunes\logs\debug.log`; Settings has an Open logs folder button. Startup, device detection, imports, FFmpeg, music/podcast sync, listening reconciliation, feed/download operations, settings, tool installation, and failures are logged. Files rotate at 5 MB with three backups. HTTP(S) addresses are redacted, but file paths and filenames may contain personal information: review logs before sharing them. Logging failures never crash the app.
