# hTunes

A Windows music library and iPod companion built with C# and WPF.

## Current library prototype

- Browse by artist, album, genre, or song
- Show source format and bitrate for library and iPod music
- Import audio using File → Add files or drag-and-drop from Explorer
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
