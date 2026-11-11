using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FyxBody.Videos.Api.YouTube;

public sealed class YouTubeMediaProcessor(IConfiguration configuration, IMediaProcessRunner runner, ILogger<YouTubeMediaProcessor> logger, TakeoutSources? takeout = null) : IYouTubeMediaProcessor, IDisposable
{
    private readonly SemaphoreSlim transcodes = new(YouTubeImportQueue.TranscodeSlots(configuration));

    public void Dispose() => transcodes.Dispose();

    public const long MaxFileBytes = 200L * 1024 * 1024;
    internal static long FileLimit(IConfiguration configuration, string? youtubeId = null) =>
        OperatingSystem.IsMacOS() && configuration.GetValue<bool>("VideoImports:TakeoutOnly")
            ? youtubeId is not null && (configuration["VideoImports:LargeRetryId"] ?? "").Split(',').Contains(youtubeId)
                ? (configuration["VideoImports:LargeWorkspaceId"] == youtubeId ? 4 : 1) * MediaProcessRunner.MaxWorkspaceBytes - 1
                : Math.Clamp(configuration.GetValue("VideoImports:LocalMaxFileBytes", MaxFileBytes), MaxFileBytes, MediaProcessRunner.MaxWorkspaceBytes - 1)
            : MaxFileBytes;

    internal static bool ExtendedDuration(IConfiguration configuration, string youtubeId) =>
        OperatingSystem.IsMacOS() && configuration.GetValue<bool>("VideoImports:TakeoutOnly")
        && (configuration["VideoImports:ExtendedDurationIds"] ?? "").Split(',').Contains(youtubeId);

    private sealed record MediaInfo(int Width, int Height, int DurationMs, string Codec, string PixelFormat);
    public async Task<PreparedYouTubeVideo> PrepareAsync(string youtubeId, IReadOnlyList<int> heights,
        Func<MediaImportProgress, Task> progress, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(youtubeId, @"\A[A-Za-z0-9_-]{11}\z") || heights.Count is < 1 or > 4 || heights.Any(h => h is not (1080 or 720 or 480 or 360)))
            throw new ApiException(400, "invalid_request", "El identificador de YouTube o las resoluciones no son válidos.");
        var maxFileBytes = FileLimit(configuration, youtubeId);
        var temporaryRoot = configuration["VideoImports:TemporaryDirectory"] ?? Path.GetTempPath();
        if (!Directory.Exists(temporaryRoot)) throw new ApiException(503, "import_tools_unavailable", "No está disponible el directorio temporal de importación.");
        var directory = Path.Combine(temporaryRoot, "fyxbody-import-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        void Cleanup()
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger.LogWarning("A media import temporary directory requires cleanup; error {ErrorType}.", error.GetType().Name); }
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(18));
            var ct = timeout.Token;
            var ytdlp = configuration["VideoImports:YtDlpPath"] ?? "yt-dlp";
            var ffmpeg = configuration["VideoImports:FfmpegPath"] ?? "ffmpeg";
            var ffprobe = configuration["VideoImports:FfprobePath"] ?? "ffprobe";
            var local = takeout?.Enabled == true;
            var source = local ? takeout!.SourcePath(youtubeId) : Path.Combine(directory, "source.mp4");
            var inputFormat = Path.GetExtension(source).ToLowerInvariant() is ".webm" or ".mkv" ? "matroska" : "mov";
            string rawTitle;
            var maxDuration = ExtendedDuration(configuration, youtubeId) ? int.MaxValue / 1000 - 1
                : Math.Clamp(configuration.GetValue("VideoImports:MaxDurationSeconds", 600), 1, 600);
            var url = "https://www.youtube.com/watch?v=" + youtubeId;
            var useVivaldi = OperatingSystem.IsMacOS() && configuration.GetValue<bool>("VideoImports:UseLocalVivaldi");
            List<string> Common() => ["--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--no-progress", "--no-color", "--socket-timeout", "20", "--retries", "2", "--fragment-retries", "2"];
            void Runtime(List<string> args)
            {
                if (useVivaldi) args.AddRange(["--cookies-from-browser", "vivaldi:Default"]);
                if (configuration["VideoImports:JavaScriptRuntime"] is { Length: > 0 } runtime)
                    args.AddRange(["--no-js-runtimes", "--js-runtimes", runtime]);
            }
            async Task<string> Run(string executable, IReadOnlyList<string> args, string code, string message)
            {
                var result = await runner.RunAsync(executable, args, directory, ct);
                if (result.ExitCode != 0 && executable == ytdlp && Regex.IsMatch(result.StandardError,
                    @"HTTP Error 429|too many requests|rate[- ]limit|sign in to confirm you.re not a bot", RegexOptions.IgnoreCase))
                    throw new ApiException(429, YouTubeImportQueue.RateLimited, "YouTube paused downloads. The import will resume automatically.");
                if (result.ExitCode != 0) throw new ApiException(502, code, message);
                return result.StandardOutput;
            }
            await progress(new("probing", 5));
            if (local)
            {
                rawTitle = Path.GetFileNameWithoutExtension(source);
            }
            else
            {
                var infoArgs = Common(); Runtime(infoArgs);
                if ((configuration["VideoImports:CompactMetadataIds"] ?? "").Split(',').Contains(youtubeId))
                    infoArgs.AddRange(["--skip-download", "--print", """{"id":%(id)j,"title":%(title)j,"duration":%(duration)j,"is_live":%(is_live)j}"""]);
                else infoArgs.AddRange(["--skip-download", "--dump-single-json"]);
                infoArgs.AddRange(["--", url]);
                using var metadata = JsonDocument.Parse(await Run(ytdlp, infoArgs, "youtube_unavailable", "No se ha podido consultar el vídeo. Comprueba que sea público u oculto y que la URL sea correcta."), new JsonDocumentOptions { MaxDepth = 64 });
                var item = metadata.RootElement;
                if (!item.TryGetProperty("id", out var id) || id.GetString() != youtubeId ||
                    (item.TryGetProperty("_type", out var type) && type.GetString() is "playlist" or "multi_video") ||
                    (item.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True))
                    throw new ApiException(400, "youtube_unsupported", "Solo se admiten vídeos individuales terminados; no directos ni listas.");
                if (!item.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > maxDuration)
                    throw new ApiException(400, "import_duration_limit", $"El vídeo debe durar como máximo {maxDuration} segundos.");
                rawTitle = item.TryGetProperty("title", out var title) ? title.GetString() ?? youtubeId : youtubeId;
                await progress(new("downloading", 12));
                var download = Common(); Runtime(download);
                if (useVivaldi) download.AddRange(["--sleep-interval", "1", "--max-sleep-interval", "3"]);
                download.AddRange(["--no-simulate", "--restrict-filenames", "--max-filesize", maxFileBytes.ToString(CultureInfo.InvariantCulture),
                    "--format", "bestvideo[ext=mp4][vcodec^=avc1][height<=1080]+bestaudio[ext=m4a]/best[ext=mp4][height<=1080]/bestvideo[ext=mp4][vcodec^=avc1][height<=1080]",
                    "--merge-output-format", "mp4", "--output", Path.Combine(directory, "source.%(ext)s")]);
                if (Path.GetDirectoryName(ffmpeg) is { Length: > 0 } location) download.AddRange(["--ffmpeg-location", location]);
                download.AddRange(["--", url]);
                await Run(ytdlp, download, "youtube_download_failed", "No se ha podido descargar el vídeo de YouTube. Puedes reintentar la importación más tarde.");
                CheckFile(source, maxFileBytes);
            }
            var safeTitle = new string(rawTitle.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (safeTitle.Length == 0) safeTitle = youtubeId;
            if (safeTitle.Length > 300) safeTitle = safeTitle[..300];
            async Task<MediaInfo> Probe(string path, string format)
            {
                using var result = JsonDocument.Parse(await Run(ffprobe,
                    ["-v", "error", "-protocol_whitelist", "file,pipe", "-f", format, "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt:format=duration", "-of", "json", path],
                    "import_invalid_media", "No se ha podido comprobar el archivo de vídeo."));
                var stream = result.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
                if (stream.ValueKind == JsonValueKind.Undefined) throw new ApiException(400, "import_invalid_media", "El archivo no contiene una pista de vídeo válida.");
                var actualSeconds = double.Parse(result.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                if (!double.IsFinite(actualSeconds) || actualSeconds <= 0 || actualSeconds > maxDuration + 1)
                    throw new ApiException(400, "import_duration_limit", "La duración real del archivo supera el límite permitido.");
                var width = stream.GetProperty("width").GetInt32(); var height = stream.GetProperty("height").GetInt32();
                if (width < 2 || width > 4096 || height < 2 || height > (local ? 4096 : 1080))
                    throw new ApiException(400, "import_dimensions_limit", "Las dimensiones del vídeo no son compatibles con la importación.");
                return new(width, height, checked((int)Math.Round(actualSeconds * 1000)), stream.GetProperty("codec_name").GetString() ?? "",
                    stream.TryGetProperty("pix_fmt", out var pixelFormat) ? pixelFormat.GetString() ?? "" : "");
            }
            // Release the PostgreSQL download slot before waiting for a converter.
            await progress(new("transcoding", 35));
            await transcodes.WaitAsync(ct);
            try
            {
                var sourceInfo = await Probe(source, inputFormat);
                if (local && inputFormat == "mov" && heights.Count == 1 && heights[0] == sourceInfo.Height
                    && configuration["VideoImports:DirectOriginalId"] == youtubeId)
                {
                    var bytes = CheckFile(source, maxFileBytes);
                    return new(safeTitle, sourceInfo.Height, sourceInfo.DurationMs,
                        [new(sourceInfo.Height, source, bytes, sourceInfo.DurationMs)],
                        () => { Cleanup(); return ValueTask.CompletedTask; });
                }
                var targets = heights.Distinct().Where(h => h <= sourceInfo.Height).OrderDescending().ToArray();
                if (targets.Length == 0) targets = [sourceInfo.Height % 2 == 0 ? sourceInfo.Height : sourceInfo.Height - 1];
                var renditions = new List<PreparedYouTubeRendition>();
                var singlePass = local && configuration.GetValue<bool>("VideoImports:LocalSinglePass")
                    && sourceInfo is { Height: 1080, Codec: "h264", PixelFormat: "yuv420p" }
                    && new FileInfo(source).Length < maxFileBytes - 1024 * 1024;
                var cachedHeights = new HashSet<int>();
                if (local && OperatingSystem.IsMacOS() && targets.Any(height => height < 1080 || sourceInfo.Height >= 1440)
                    && configuration["VideoImports:LocalHevcCacheDirectory"] is { Length: > 0 } cache)
                {
                    await using var original = File.OpenRead(source);
                    var key = Convert.ToHexString(await SHA256.HashDataAsync(original, ct)).ToLowerInvariant();
                    foreach (var height in targets.Where(height => height < 1080 || sourceInfo.Height >= 1440))
                    {
                        var output = Path.Combine(directory, $"{height}p.mp4");
                        var candidate = Path.Combine(cache, $"{key}-{height}p.mp4");
                        if (!File.Exists(candidate)) continue;
                        try
                        {
                            CheckFile(candidate, maxFileBytes);
                            File.Copy(candidate, output);
                            var rendered = await Probe(output, "mov");
                            if (rendered.Height == height && rendered.Codec == "hevc"
                                && Math.Abs(rendered.DurationMs - sourceInfo.DurationMs) <= 1000)
                            {
                                cachedHeights.Add(height);
                                logger.LogInformation("Reused local HEVC {Height}p for {YouTubeId}.", height, youtubeId);
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ApiException or JsonException)
                        {
                            logger.LogWarning("Local HEVC cache entry could not be used for {YouTubeId}: {ErrorType}.",
                                youtubeId, error.GetType().Name);
                        }
                        if (!cachedHeights.Contains(height)) File.Delete(output);
                    }
                }
                singlePass |= cachedHeights.Count > 0 && sourceInfo is { Height: 1080, Codec: "h264", PixelFormat: "yuv420p" }
                    && new FileInfo(source).Length < maxFileBytes - 1024 * 1024;
                if (singlePass)
                {
                    var encodeTargets = targets.Where(height => !cachedHeights.Contains(height)).ToArray();
                    var lower = encodeTargets.Where(height => height != 1080).ToArray();
                    var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                        "-max_alloc", "67108864", "-threads", "2", "-protocol_whitelist", "file,pipe",
                        "-f", inputFormat, "-i", source };
                    if (lower.Length > 0)
                    {
                        var filters = lower.Select(height => $"[v{height}]scale=-2:{height}[out{height}]");
                        var graph = lower.Length == 1 ? $"[0:v:0]scale=-2:{lower[0]}[out{lower[0]}]"
                            : $"[0:v:0]split={lower.Length}{string.Concat(lower.Select(height => $"[v{height}]"))};{string.Join(';', filters)}";
                        arguments.AddRange(["-filter_complex_threads", "2", "-filter_complex", graph]);
                    }
                    foreach (var height in encodeTargets)
                    {
                        arguments.AddRange(["-map", height == 1080 ? "0:v:0" : $"[out{height}]", "-map", "0:a:0?", "-map_metadata", "-1"]);
                        if (height == 1080) arguments.AddRange(["-c:v", "copy"]);
                        else arguments.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "22", "-threads", "2", "-pix_fmt", "yuv420p"]);
                        arguments.AddRange(["-c:a", "aac", "-b:a", "128k", "-fs", (maxFileBytes + 1).ToString(CultureInfo.InvariantCulture),
                            "-movflags", "+faststart", Path.Combine(directory, $"{height}p.mp4")]);
                    }
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    if (encodeTargets.Length > 0) await Run(ffmpeg, arguments, "import_transcode_failed", "No se ha podido generar una de las versiones MP4.");
                    logger.LogInformation("Local single pass encoded {YouTubeId} in {ElapsedMilliseconds} ms.", youtubeId, started.ElapsedMilliseconds);
                }
                for (var index = 0; index < targets.Length; index++)
                {
                    var height = targets[index];
                    await progress(new("transcoding", 35 + index * 40 / targets.Length));
                    var output = Path.Combine(directory, height.ToString(CultureInfo.InvariantCulture) + "p.mp4");
                    if (!singlePass && !cachedHeights.Contains(height)) await Run(ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-max_alloc", "67108864", "-threads", "2",
                        "-protocol_whitelist", "file,pipe", "-f", inputFormat, "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1",
                        "-vf", $"scale=-2:{height}", "-c:v", "libx264", "-preset", "fast", "-crf", "22", "-threads", "2", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "128k", "-fs", (maxFileBytes + 1).ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", output],
                        "import_transcode_failed", "No se ha podido generar una de las versiones MP4.");
                    var bytes = CheckFile(output, maxFileBytes);
                    var rendered = await Probe(output, "mov");
                    if (rendered.Height != height || rendered.Codec != (cachedHeights.Contains(height) ? "hevc" : "h264") || Math.Abs(rendered.DurationMs - sourceInfo.DurationMs) > 1000)
                        throw new ApiException(400, "import_invalid_media", "Una versión generada no coincide con el vídeo original o se ha truncado.");
                    renditions.Add(new(height, output, bytes, rendered.DurationMs));
                }
                await progress(new("transcoding", 78));
                return new(safeTitle, sourceInfo.Height, sourceInfo.DurationMs, renditions, () => { Cleanup(); return ValueTask.CompletedTask; });
            }
            finally { transcodes.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { Cleanup(); throw new ApiException(504, "import_timeout", "La preparación del vídeo ha superado el tiempo máximo permitido."); }
        catch (JsonException) { Cleanup(); throw new ApiException(502, "import_invalid_metadata", "La herramienta no ha devuelto metadatos de vídeo válidos."); }
        catch (Exception error) when (error is KeyNotFoundException or FormatException or OverflowException)
        { Cleanup(); throw new ApiException(502, "import_invalid_metadata", "No se han podido comprobar los metadatos del vídeo."); }
        catch { Cleanup(); throw; }
    }
    private static long CheckFile(string path, long maxFileBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 12) throw new ApiException(400, "import_invalid_media", "No se ha generado un archivo MP4 válido.");
        if (file.Length > maxFileBytes) throw new ApiException(413, "import_file_limit", "El vídeo supera el tamaño máximo por archivo.");
        using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header[4..].SequenceEqual("ftyp"u8)) throw new ApiException(400, "import_invalid_media", "El archivo descargado no es un MP4 compatible.");
        return file.Length;
    }
}

public static class YouTubeMediaServices
{
    public static IServiceCollection AddYouTubeMediaProcessing(this IServiceCollection services)
    {
        services.AddSingleton<TakeoutSources>();
        services.AddSingleton<IMediaProcessRunner, MediaProcessRunner>();
        services.AddSingleton<IYouTubeMediaProcessor, YouTubeMediaProcessor>();
        return services;
    }
}
