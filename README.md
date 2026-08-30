# hTunes

A Windows music library and iPod companion built with C# and WPF.

## Current library prototype

- Browse by artist, album, genre, or song
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
- File/Edit/View/Playback menus expose the same actions, with working tab/category navigation and a Settings placeholder
- Undo/Redo supports up to 100 local edits per session: imports, metadata/artwork, library removal, playlist edits, subscriptions, sync rules, and manually changed podcast played state
- Search Apple Podcasts or subscribe directly with an RSS feed URL
- Browse subscribed shows and episode artwork, numbers, dates, played state, and download state
- Download, play, delete, and mark individual podcast episodes played or unplayed
- Configure each show to sync a chosen number of its newest or oldest unplayed episodes
- Download missing episodes during podcast sync and mirror those retention rules to the iPod
- Write the stock iPod podcast groups and browse on-device episodes under the iPod tab's Podcasts category
- Remove completed podcast downloads from both hTunes and the iPod when playback finishes
- Synchronize podcast bookmark positions, show elapsed time and percentage, and treat 50% playback as played
- Preserve podcast and music play-count changes during the same startup/hot-plug reconciliation

Run with `dotnet run --project HTunes.App`.

## FFmpeg

Library imports always keep their original files. Conversion only happens while copying music to an iPod, using temporary files that are removed after the sync.

On startup, hTunes checks for missing FFmpeg and yt-dlp installations and compares installed copies with the latest published releases. If either is missing or outdated, it offers to download the needed tools into the current user's local hTunes data folder. The downloaded files are checked against their published SHA-256 values and do not require an administrator installation. Use **File → Update FFmpeg and yt-dlp…** to update or reinstall both tools at any time.

For a transcoding preset, hTunes can also use `ffmpeg.exe` in one of these locations:

- Beside the hTunes executable
- In a `tools` folder beside the hTunes executable or current Visual Studio working directory
- In the location named by the `FFMPEG_PATH` environment variable
- Anywhere on Windows `PATH`

Selecting **Do not transcode (original)** does not require FFmpeg.

## Context-menu checks

Run `dotnet run --project tests/HTunes.ContextMenuChecks` to check list/grid multi-selection, top-menu rebuilding, undo/redo branching and limits, failure handling, and preservation of play counts during metadata undo. These checks do not launch hTunes, load your library, or access an iPod.

**Remove from library** leaves original audio files on disk. **Remove from this playlist** only changes playlist membership; **Delete playlist** leaves its tracks in the library. Podcast **Delete downloaded files** removes local downloads but retains episode entries and played state.

## Menus and undo/redo

- **File:** add files/folders, create playlists, find/subscribe to shows, sync music/podcasts, eject, update tools, and exit.
- **Edit:** Undo/Redo, Select all, and actions for the last selected track/group/playlist/show/episode.
- **View:** switch tabs and library categories, search, and refresh.
- **Playback:** play/resume, pause, stop, previous, and next.
- **Settings:** opens a placeholder window for the next feature step; no new preferences are present yet.

Shortcuts: `Ctrl+Z` Undo, `Ctrl+Y` or `Ctrl+Shift+Z` Redo, `Ctrl+A` Select all, `Ctrl+O` Add files, `Ctrl+N` New playlist, and `Ctrl+F` Search. Text fields retain their native text-editing commands.

History is session-only and does not reverse iPod sync/eject, tool updates, downloads/file deletion, or automatic listening counts/progress. Undoing a podcast unsubscribe or manual played-state change restores its local state, **not deleted downloads or iPod copies**; download/sync again as needed. Metadata edits and their undo affect library metadata only, as before.
