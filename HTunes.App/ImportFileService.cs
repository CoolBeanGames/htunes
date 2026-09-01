using System.IO;
using System.Security.Cryptography;

namespace HTunes.App;

internal sealed record ImportedFile(string SourcePath, string LibraryPath, bool DeleteSourceAfterSave, byte[]? Hash);

internal static class ImportFileService
{
    public static ImportedFile Prepare(string sourcePath, AppPreferences settings, string? artist = null, string? albumArtist = null, string? album = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var directory = Path.GetFullPath(settings.ImportDirectory);
        if (settings.ImportMode == ImportFileMode.Reference || source.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new(source, source, false, null);
        var owner = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist;
        if (!string.IsNullOrWhiteSpace(owner)) directory = Path.Combine(directory, SafeFolder(owner));
        if (!string.IsNullOrWhiteSpace(album)) directory = Path.Combine(directory, SafeFolder(album));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(source));
        for (var index = 1; File.Exists(destination) || Directory.Exists(destination); index++)
            destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(source)} ({index}){Path.GetExtension(source)}");
        var temporary = Path.Combine(directory, $".htunes-import-{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] hash;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                input.Position = 0;
                hash = SHA256.HashData(input);
                using var verification = File.OpenRead(temporary);
                if (!hash.SequenceEqual(SHA256.HashData(verification))) throw new IOException("The imported copy failed verification. The original was retained.");
            }
            File.Move(temporary, destination); // No overwrite, including a concurrent import collision.
            DebugLog.Write("Import", $"Verified {settings.ImportMode}: {source} -> {destination}");
            return new(source, destination, settings.ImportMode == ImportFileMode.Move, hash);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SafeFolder(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    public static void CompleteMove(ImportedFile imported)
    {
        if (!imported.DeleteSourceAfterSave) return;
        // Caller must save the library first. Recheck both files before removing the original.
        using var source = new FileStream(imported.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var destination = File.OpenRead(imported.LibraryPath);
        if (imported.Hash is null || !SHA256.HashData(source).SequenceEqual(imported.Hash) || !SHA256.HashData(destination).SequenceEqual(imported.Hash))
            throw new IOException("A file changed after import; the original was retained.");
        File.Delete(imported.SourcePath);
        DebugLog.Write("Import", $"Move completed after library save: {imported.SourcePath}");
    }
}
