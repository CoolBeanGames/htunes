namespace HTunes.App;

internal sealed record TranscodePreset(string Id, string DisplayName, string Extension, string? Codec, int? BitrateKbps)
{
    public bool IsOriginal => Codec is null;
}

internal static class TranscodePresets
{
    private static readonly IReadOnlyDictionary<string, TranscodePreset> Values = new[]
    {
        new TranscodePreset("original", "Do not transcode (original)", "", null, null),
        new TranscodePreset("mp3-128", "MP3 — 128 kbps", ".mp3", "libmp3lame", 128),
        new TranscodePreset("mp3-192", "MP3 — 192 kbps", ".mp3", "libmp3lame", 192),
        new TranscodePreset("mp3-256", "MP3 — 256 kbps", ".mp3", "libmp3lame", 256),
        new TranscodePreset("mp3-320", "MP3 — 320 kbps", ".mp3", "libmp3lame", 320),
        new TranscodePreset("aac-128", "AAC — 128 kbps", ".m4a", "aac", 128),
        new TranscodePreset("aac-192", "AAC — 192 kbps", ".m4a", "aac", 192),
        new TranscodePreset("aac-256", "AAC — 256 kbps", ".m4a", "aac", 256),
        new TranscodePreset("alac", "Apple Lossless", ".m4a", "alac", null)
    }.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static TranscodePreset Get(string? id) =>
        id is not null && Values.TryGetValue(id, out var preset) ? preset : Values["original"];
}
