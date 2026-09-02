using HTunes.App;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    private static void CheckYtDlp()
    {
        Require(YtDlpDownloadService.ParseLinks("\r\n https://example.test/one?a=1&b=2 \r\n\nhttps://example.test/two\n").Count == 2, "Accept one link per nonempty line.");
        foreach (var text in new[] { "", "--exec calc", "file:///C:/test.mp3", "https://example.test/a second" })
            ExpectFailure(() => YtDlpDownloadService.ParseLinks(text), "Reject non-URL command text and local file URLs.");
        InTemporaryDirectory(directory =>
        {
            var settings = new AppPreferences { DownloadDirectory = directory };
            var archive = Path.Combine(directory, "archive.txt");
            var url = "https://example.test/watch?v=x&list=y";
            var start = YtDlpDownloadService.BuildStartInfo("yt-dlp.exe", @"C:\tools with spaces\ffmpeg.exe", url, settings, archive, "TEST:");
            var arguments = start.ArgumentList.ToList();
            Require(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardError && start.RedirectStandardOutput, "Run without a shell and drain both output pipes.");
            Require(arguments[^2] == "--" && arguments[^1] == url, "URLs must remain a single literal argument after the option separator.");
            Require(arguments[arguments.IndexOf("--ffmpeg-location") + 1] == @"C:\tools with spaces\ffmpeg.exe", "yt-dlp must use hTunes' exact FFmpeg path.");
            Require(arguments.Contains("--no-simulate") && arguments.Contains("--no-quiet") && arguments.Any(value => value.StartsWith("after_move:TEST:")), "Print events must not turn the download into a quiet simulation.");
            var path = Path.Combine(directory, "Song [AbCdEf123_-].mp3"); File.WriteAllText(path, "synthetic file");
            var completion = "TEST:" + JsonSerializer.Serialize(new { type = "complete", path, extractor = "Youtube", id = "AbCdEf123_-", title = "Quoted \"title\"\nUnicode café", index = 2, count = 5 });
            Require(YtDlpDownloadService.TryParseLine(completion, "TEST:", directory, out var update, out var file) && file is not null && file.Identity == "youtube AbCdEf123_-" && update?.Count == 5, "Completion events must parse JSON-escaped titles and source identity.");
            Require(YtDlpDownloadService.TryParseLine("TEST:{\"type\":\"progress\",\"title\":\"Song\",\"index\":\"1\",\"count\":\"NA\",\"playlist\":\"NA\",\"bytes\":50,\"total\":100}", "TEST:", directory, out update, out _) && update?.Percent == 50 && update.Count == 1, "Single-track byte progress must report 1 of 1 and the percentage.");
            Require(YtDlpDownloadService.TryParseLine("TEST:{\"type\":\"progress\",\"title\":\"Song\",\"index\":\"1\",\"count\":\"NA\",\"playlist\":\"a playlist\",\"total\":\"NA\"}", "TEST:", directory, out update, out _) && update?.Percent is null && update?.Count is null, "Unknown playlist totals/size must remain unknown.");
            Require(!YtDlpDownloadService.TryParseLine("TEST:not JSON", "TEST:", directory, out _, out _), "Malformed markers must not crash the output reader.");
            Require(!YtDlpDownloadService.IsCompletedAudioPath(path + ".part", directory) && !YtDlpDownloadService.IsCompletedAudioPath(path, Path.Combine(directory, "other")), "Do not import partial files or files outside the configured folder.");
            var tracks = new[] { new Track { FilePath = path, DownloadIdentity = "youtube AbCdEf123_-" }, new Track { FilePath = path, DownloadIdentity = "youtube otherID" }, new Track { FilePath = path + "missing", DownloadIdentity = "youtube missing" } };
            Require(YtDlpDownloadService.LibraryArchive(tracks).SequenceEqual(["youtube AbCdEf123_-", "youtube otherID"]), "Archive must contain only IDs with an existing library file.");
            Require(YtDlpDownloadService.LibraryArchive([new Track { FilePath = path }]).Single() == "youtube AbCdEf123_-", "Recognize manually imported YouTube filenames containing exact IDs.");
            Require(!YtDlpDownloadService.IsArchiveIdentity("youtube id\nmalicious entry"), "Archive entries must not allow newline injection.");
        });

        var output = new ConcurrentQueue<string>();
        var normal = YtDlpDownloadService.RunProcessAsync(TestChildStart("output"), output.Enqueue, CancellationToken.None).GetAwaiter().GetResult();
        Require(normal.ExitCode == 7 && output.Contains("unicode café ✓") && output.Count >= 1001, "Runner must preserve UTF-8, nonzero exits, and heavy stdout/stderr output without blocking.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = false;
        var result = YtDlpDownloadService.RunProcessAsync(TestChildStart("wait"), line => { if (line == "READY") { ready = true; cancel.Cancel(); } }, cancel.Token).GetAwaiter().GetResult();
        Require(ready && result.Aborted, "Abort must terminate an active process and drain its output.");
        using var treeCancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var descendantId = 0;
        var tree = YtDlpDownloadService.RunProcessAsync(TestChildStart("tree"), line =>
        {
            if (line.StartsWith("CHILD=")) descendantId = int.Parse(line[6..]);
            if (line == "READY") treeCancel.Cancel();
        }, treeCancel.Token).GetAwaiter().GetResult();
        Require(tree.Aborted && descendantId > 0, "Process-tree cancellation test must start a descendant.");
        try
        {
            using var descendant = Process.GetProcessById(descendantId);
            var stopped = descendant.WaitForExit(5000);
            if (!stopped) descendant.Kill(); // Cleanup only this test's known child if the assertion fails.
            Require(stopped, "Abort must terminate child conversion processes too.");
        }
        catch (ArgumentException) { } // The descendant has already exited and been reaped.
        CheckDownloadedLibraryImport();
    }

    private static void CheckDownloadedLibraryImport()
    {
        InTemporaryDirectory(directory =>
        {
            var library = Path.Combine(directory, "library.json");
            var window = new MainWindow(false, library);
            var settings = new AppPreferences { DownloadDirectory = Path.Combine(directory, "downloads"), ImportDirectory = Path.Combine(directory, "managed"), ImportMode = ImportFileMode.Copy };
            Directory.CreateDirectory(settings.DownloadDirectory);
            var source = Path.Combine(settings.DownloadDirectory, "tone.wav"); File.WriteAllBytes(source, SyntheticWav());
            var import = typeof(MainWindow).GetMethod("ImportYtAudioAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Import(MainWindow target, string path, string id) => RunUiTask(() => (Task)import.Invoke(target, [new YtCompletedFile(path, "generic " + id, "Test tone"), settings, null])!);
            try
            {
                Import(window, source, "tone");
                var saved = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(library))!;
                Require(saved.Tracks.Count == 1 && saved.Tracks[0].DownloadIdentity == "generic tone" && saved.Tracks[0].FilePath.StartsWith(settings.ImportDirectory) && File.Exists(source), "Completed downloads must be copied to managed storage, added to the library, and persisted with source identity.");
                Require(saved.Tracks[0].Format == "WAV", "Downloaded audio must have its real metadata/format read on import.");
                Import(window, source, "tone");
                Require(JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(library))!.Tracks.Count == 1, "Importing an already-present download must not duplicate the library entry.");
                settings.ImportMode = ImportFileMode.Move;
                var moveSource = Path.Combine(settings.DownloadDirectory, "second.wav"); File.WriteAllBytes(moveSource, SyntheticWav());
                Import(window, moveSource, "second");
                Require(!File.Exists(moveSource) && JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(library))!.Tracks.Count == 2, "Move import must save the new library entry before removing the downloaded source.");
                var failingWindow = new MainWindow(false, directory); // A directory is not a writable library JSON file.
                try
                {
                    var retained = Path.Combine(settings.DownloadDirectory, "retained.wav"); File.WriteAllBytes(retained, SyntheticWav());
                    Import(failingWindow, retained, "retained");
                    Require(File.Exists(retained), "A failed library save must keep the downloaded original even with Move selected.");
                }
                finally { failingWindow.Close(); }
            }
            finally { window.Close(); }
        });
    }

    private static void RunUiTask(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static ProcessStartInfo TestChildStart(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--yt-test-child"); start.ArgumentList.Add(mode);
        return start;
    }

    private static int YtTestChild(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Last() == "sleep") { new ManualResetEvent(false).WaitOne(); return 0; }
        if (args.Last() == "tree")
        {
            using var child = Process.Start(TestChildStart("sleep"))!;
            Console.WriteLine("CHILD=" + child.Id); Console.WriteLine("READY"); Console.Out.Flush();
            new ManualResetEvent(false).WaitOne(); return 0;
        }
        if (args.Last() == "wait") { Console.WriteLine("READY"); Console.Out.Flush(); new ManualResetEvent(false).WaitOne(); return 0; }
        Console.WriteLine("unicode café ✓");
        for (var i = 0; i < 500; i++) { Console.WriteLine("stdout " + i); Console.Error.WriteLine("stderr " + i); }
        return 7;
    }

    private static void CheckLocalYtDlp(string executable, string ffmpeg)
    {
        InTemporaryDirectory(directory =>
        {
            var wav = SyntheticWav();
            using var serverCancellation = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                try
                {
                    while (!serverCancellation.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(serverCancellation.Token);
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                        var request = await reader.ReadLineAsync();
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wav.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers);
                        if (request?.StartsWith("HEAD ") != true) await stream.WriteAsync(wav);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) when (serverCancellation.IsCancellationRequested) { }
            });
            try
            {
                var settings = new AppPreferences { DownloadDirectory = Path.Combine(directory, "downloads"), ImportDirectory = Path.Combine(directory, "managed"),
                    YtAudioFormat = "mp3", YtAudioQuality = "128K", YtEmbedArtwork = false, YtPlaylistSubfolders = false, YtPlaylistAsAlbum = true };
                var archive = Path.Combine(directory, "archive.txt"); File.WriteAllText(archive, "");
                var messages = new ConcurrentQueue<YtDownloadUpdate>();
                var progress = new InlineProgress<YtDownloadUpdate>(messages.Enqueue);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                YtLinkResult Run() => YtDlpDownloadService.DownloadLinkAsync(executable, ffmpeg, $"http://127.0.0.1:{port}/test-tone.wav", settings, archive, progress, timeout.Token).GetAwaiter().GetResult();
                var first = Run();
                if (first.ExitCode != 0 || first.Files.Count != 1) throw new InvalidOperationException("Local yt-dlp test failed: " + string.Join("\n", messages.Select(message => message.Log).Where(line => line is not null)));
                using (var audio = TagLib.File.Create(first.Files[0].Path))
                {
                    Require(audio.Properties.Duration.TotalMilliseconds > 900 && audio.Properties.AudioBitrate > 0, "Actual yt-dlp and FFmpeg must produce valid audio.");
                    Require(audio.Tag.Album is not ("null" or "NA"), "A standalone link must not acquire a fake playlist-album placeholder.");
                }
                Require(messages.Any(message => message.Count == 1), "Real single-video progress must report one total track.");
                Require(!Directory.EnumerateDirectories(Path.Combine(settings.DownloadDirectory, ".htunes-work")).Any(), "Completed conversions must clean their private staging directories.");
                settings.ImportMode = ImportFileMode.Move;
                var moved = ImportFileService.Prepare(first.Files[0].Path, settings);
                var libraryTrack = new Track { FilePath = moved.LibraryPath, DownloadIdentity = first.Files[0].Identity };
                File.WriteAllText(Path.Combine(directory, "test-library.json"), JsonSerializer.Serialize(libraryTrack));
                ImportFileService.CompleteMove(moved);
                File.WriteAllLines(archive, YtDlpDownloadService.LibraryArchive([libraryTrack]));
                var second = Run();
                Require(second.ExitCode == 0 && second.Files.Count == 0 && !File.Exists(moved.SourcePath), "Duplicate source ID must skip downloading even after moving the file into the managed library.");
                File.Delete(moved.LibraryPath); File.WriteAllLines(archive, YtDlpDownloadService.LibraryArchive([libraryTrack]));
                var third = Run();
                Require(third.ExitCode == 0 && third.Files.Count == 1, "A missing library file must be downloadable again.");
                Console.WriteLine("PASS: actual yt-dlp + FFmpeg loopback download, MP3 verification, moved-file duplicate skip, and missing-file retry.");
            }
            finally { serverCancellation.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); }
        });
    }

    private static byte[] SyntheticWav()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        const int samples = 44100;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / samples) * 2000));
        writer.Flush(); return stream.ToArray();
    }
}
