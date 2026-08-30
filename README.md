# hTunes

A Windows music library and iPod companion built with C# and WPF.

## Current library prototype

- Browse by artist, album, genre, or song
- Import audio using File → Add files or drag-and-drop from Explorer
- Multi-select with Ctrl/Shift and batch-edit metadata and artwork
- Create playlists and drag selected songs onto them
- Play local tracks and track play counts
- Detect a mounted stock-OS iPod and show its name and storage capacity
- Browse music currently stored on the iPod from a connection-only iPod tab
- Safely eject the iPod through Windows
- Persist the library under the current Windows user's local application data

Run with `dotnet run --project HTunes.App`.
