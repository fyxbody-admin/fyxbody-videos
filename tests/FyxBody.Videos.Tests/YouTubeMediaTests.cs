using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FyxBody.Videos.Tests;

public sealed class YouTubeMediaTests
{
    private const string Id = "WhpknPP0xKU";
    private sealed class FakeRunner : IMediaProcessRunner
    {
        public int SourceHeight { get; init; } = 720;
        public long SourceLength { get; init; } = 32;
        public int ExitCode { get; init; }
        public List<string[]> Calls { get; } = [];
        public Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray());
            if (ExitCode != 0) return Task.FromResult(new MediaProcessResult(ExitCode, "private tool output must never reach errors"));
            if (arguments.Contains("--dump-single-json")) return Task.FromResult(new MediaProcessResult(0, JsonSerializer.Serialize(new { id = Id, title = "Test\nexercise", duration = 44.35, is_live = false })));
            if (arguments.Contains("--output")) { Write(Path.Combine(directory, "source.mp4"), SourceLength); return Task.FromResult(new MediaProcessResult(0, "")); }
            if (arguments.Contains("-show_entries"))
            {
                var name = Path.GetFileName(arguments[^1]);
                var height = name == "source.mp4" ? SourceHeight : int.Parse(name[..^5], CultureInfo.InvariantCulture);
                return Task.FromResult(new MediaProcessResult(0, JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "video", codec_name = "h264", width = height * 16 / 9, height } }, format = new { duration = "44.350" } })));
            }
            Write(arguments[^1], 32); return Task.FromResult(new MediaProcessResult(0, ""));
        }
        private static void Write(string path, long length)
        {
            using var file = File.Create(path); file.Write(new byte[] { 0, 0, 0, 32, 102, 116, 121, 112, 105, 115, 111, 109 }); file.SetLength(length);
        }
    }
    private static (string Directory, YouTubeMediaProcessor Processor) Create(FakeRunner runner, bool useVivaldi = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fyxbody-media-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VideoImports:TemporaryDirectory"] = directory, ["VideoImports:UseLocalVivaldi"] = useVivaldi.ToString() }).Build();
        return (directory, new(config, runner, NullLogger<YouTubeMediaProcessor>.Instance));
    }
    [Fact]
    public async Task TakeoutReadsMappedFileWithoutCallingYouTubeOrUpscaling()
    {
        var directory = Directory.CreateTempSubdirectory("fyxbody-takeout-test-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.mp4");
            File.WriteAllBytes(source, new byte[32]);
            File.WriteAllText(Path.Combine(directory.FullName, "youtube-id-map.json"),
                JsonSerializer.Serialize(new Dictionary<string, string> { [Id] = "source.mp4" }));
            var config = TakeoutConfiguration(directory.FullName);
            var runner = new FakeRunner();
            using var processor = new YouTubeMediaProcessor(config, runner, NullLogger<YouTubeMediaProcessor>.Instance, new(config));
            await using var media = await processor.PrepareAsync(Id, [1080, 720, 480], _ => Task.CompletedTask, default);
            Assert.Equal(new[] { 720, 480 }, media.Renditions.Select(output => output.Height));
            Assert.Contains(runner.Calls, args => args.Contains(source));
            Assert.DoesNotContain(runner.Calls, args => args.Contains("--no-playlist"));
            runner.Calls.Clear();
            var error = await Assert.ThrowsAsync<ApiException>(() => processor.PrepareAsync("dQw4w9WgXcQ", [360], _ => Task.CompletedTask, default));
            Assert.Equal("takeout_source_missing", error.Code);
            Assert.Empty(runner.Calls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("/outside.mp4")]
    [InlineData("folder\\outside.mp4")]
    public void TakeoutRejectsPathsOutsideItsRoot(string path)
    {
        var directory = Directory.CreateTempSubdirectory("fyxbody-takeout-test-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "youtube-id-map.json"),
                JsonSerializer.Serialize(new Dictionary<string, string> { [Id] = path }));
            Assert.Equal("takeout_invalid_manifest", Assert.Throws<ApiException>(() => new TakeoutSources(TakeoutConfiguration(directory.FullName)).SourcePath(Id)).Code);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static IConfiguration TakeoutConfiguration(string root) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> {
            ["VideoImports:TemporaryDirectory"] = root, ["VideoImports:TakeoutOnly"] = "true", ["VideoImports:TakeoutDirectory"] = root
        }).Build();

    [Fact]
    public async Task GeneratesOnlyAvailableRequestedHeightsAndCleansTemporaryFiles()
    {
        var runner = new FakeRunner(); var (directory, processor) = Create(runner);
        try
        {
            var progress = new List<MediaImportProgress>();
            await using (var media = await processor.PrepareAsync(Id, [1080, 720, 480], p => { progress.Add(p); return Task.CompletedTask; }, CancellationToken.None))
            {
                Assert.Equal("Test exercise", media.Title);
                Assert.Equal(new[] { 720, 480 }, media.Renditions.Select(r => r.Height));
                Assert.All(media.Renditions, r => Assert.True(File.Exists(r.FilePath)));
                Assert.Contains(progress, p => p.Stage == "downloading");
                Assert.Contains(progress, p => p.Stage == "transcoding");
                Assert.All(runner.Calls.Where(c => c.Contains("--no-playlist")), args => { Assert.Contains("--ignore-config", args); Assert.Contains("--no-plugin-dirs", args); Assert.Equal("https://www.youtube.com/watch?v=" + Id, args[^1]); });
                Assert.All(runner.Calls.Where(c => c.Contains("-vf")), args => Assert.Contains("file,pipe", args));
            }
            Assert.Empty(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VivaldiRequiresLocalMacOptInAndOnlySleepsOnDownload(bool useVivaldi)
    {
        var runner = new FakeRunner(); var (directory, processor) = Create(runner, useVivaldi);
        try
        {
            await using var media = await processor.PrepareAsync(Id, [720], _ => Task.CompletedTask, CancellationToken.None);
            var probe = Assert.Single(runner.Calls, args => args.Contains("--dump-single-json"));
            var download = Assert.Single(runner.Calls, args => args.Contains("--output"));
            var enabled = useVivaldi && OperatingSystem.IsMacOS();
            Assert.Equal(enabled, probe.Contains("vivaldi:Default"));
            Assert.Equal(enabled, download.Contains("vivaldi:Default"));
            Assert.DoesNotContain("--sleep-interval", probe);
            Assert.Equal(enabled, download.Contains("--sleep-interval"));
            if (enabled)
            {
                Assert.Equal("1", download[Array.IndexOf(download, "--sleep-interval") + 1]);
                Assert.Equal("3", download[Array.IndexOf(download, "--max-sleep-interval") + 1]);
            }
            Assert.DoesNotContain(runner.Calls, args => args.Contains("--cookies"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(240, 240)]
    [InlineData(239, 238)]
    public async Task FallsBackToOriginalResolutionWhenEveryRequestedHeightWouldUpscale(int sourceHeight, int expectedHeight)
    {
        var (directory, processor) = Create(new FakeRunner { SourceHeight = sourceHeight });
        try { await using var result = await processor.PrepareAsync(Id, [1080, 720, 480], _ => Task.CompletedTask, CancellationToken.None); Assert.Equal(expectedHeight, Assert.Single(result.Renditions).Height); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RejectsOversizedDownloadAndRemovesWorkspace()
    {
        var (directory, processor) = Create(new FakeRunner { SourceLength = YouTubeMediaProcessor.MaxFileBytes + 1 });
        try
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => processor.PrepareAsync(Id, [720], _ => Task.CompletedTask, CancellationToken.None));
            Assert.Equal("import_file_limit", error.Code); Assert.Empty(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ToolErrorDoesNotDiscloseItsOutputAndCleansWorkspace()
    {
        var (directory, processor) = Create(new FakeRunner { ExitCode = 1 });
        try
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => processor.PrepareAsync(Id, [720], _ => Task.CompletedTask, CancellationToken.None));
            Assert.Equal("youtube_unavailable", error.Code); Assert.DoesNotContain("private tool output", error.Message); Assert.Empty(Directory.GetDirectories(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("../../file")]
    [InlineData("https://127.0.0.1")]
    [InlineData("WhpknPP0xKU;id")]
    public async Task InvalidIdsNeverLaunchProcesses(string id)
    {
        var runner = new FakeRunner(); var (directory, processor) = Create(runner);
        try { await Assert.ThrowsAsync<ApiException>(() => processor.PrepareAsync(id, [720], _ => Task.CompletedTask, CancellationToken.None)); Assert.Empty(runner.Calls); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RunnerPassesArgumentsLiterallyWithoutShellAndStripsApplicationEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "fyxbody-runner-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("FYXBODY_MEDIA_TEST_SECRET", "test-only-private");
        try
        {
            var runner = new MediaProcessRunner();
            var output = await runner.RunAsync("/bin/echo", ["hello; touch should-not-exist"], directory, CancellationToken.None);
            Assert.Equal("hello; touch should-not-exist", output.StandardOutput.Trim()); Assert.False(File.Exists(Path.Combine(directory, "should-not-exist")));
            var environment = await runner.RunAsync("/usr/bin/env", [], directory, CancellationToken.None);
            Assert.DoesNotContain("FYXBODY_MEDIA_TEST_SECRET", environment.StandardOutput); Assert.DoesNotContain("ConnectionStrings__", environment.StandardOutput);
        }
        finally { Environment.SetEnvironmentVariable("FYXBODY_MEDIA_TEST_SECRET", null); Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RunnerStopsLongProcessWhenCancelled()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "fyxbody-runner-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaProcessRunner().RunAsync("/bin/sleep", ["30"], directory, cancellation.Token));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(7));
        }
        finally { Directory.Delete(directory, true); }
    }
}
