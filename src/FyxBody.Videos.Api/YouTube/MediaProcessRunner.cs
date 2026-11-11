using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace FyxBody.Videos.Api.YouTube;

public sealed record MediaProcessResult(int ExitCode, string StandardOutput, string StandardError = "");
public interface IMediaProcessRunner
{
    Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken ct);
}

public sealed class MediaProcessRunner(IConfiguration? configuration = null) : IMediaProcessRunner
{
    public const long MaxWorkspaceBytes = 1024L * 1024 * 1024;
    private const int MaxOutputCharacters = 1024 * 1024;
    public async Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // External tools need their runtime and certificates, not database/Azure/application secrets.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SYSTEMROOT", "WINDIR", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        // Only the explicitly authenticated macOS yt-dlp invocation needs the actual profile home.
        var usesLocalVivaldi = OperatingSystem.IsMacOS() && arguments.Contains("--cookies-from-browser") && arguments.Contains("vivaldi:Default");
        start.Environment["HOME"] = usesLocalVivaldi
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : directory;
        start.Environment["TMPDIR"] = directory;
        start.Environment["TMP"] = directory;
        start.Environment["TEMP"] = directory;
        start.Environment["XDG_CACHE_HOME"] = directory;
        start.Environment["XDG_CONFIG_HOME"] = directory;
        using var process = new Process { StartInfo = start };
        void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        try { process.Start(); }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException or InvalidOperationException)
        { throw new ApiException(503, "import_tools_unavailable", "No están disponibles las herramientas de importación de vídeo en el servidor."); }
        process.StandardInput.Close();
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workspaceLimit = MaxWorkspaceBytes;
        if (OperatingSystem.IsMacOS() && configuration?["VideoImports:LargeWorkspaceSource"] is { Length: > 0 } source
            && arguments.Contains(source))
            workspaceLimit = 4 * MaxWorkspaceBytes;
        var outputLimit = MaxOutputCharacters;
        if (OperatingSystem.IsMacOS() && arguments.Contains("--dump-single-json")
            && arguments.LastOrDefault() is { } url
            && (configuration?["VideoImports:ExtendedOutputIds"] ?? "").Split(',').Any(
                id => url == "https://www.youtube.com/watch?v=" + id))
            outputLimit = 8 * MaxOutputCharacters;

        async Task<string> Read(StreamReader reader, bool capture)
        {
            var output = new StringBuilder(); var count = 0; var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0) return output.ToString();
                count += read;
                if (count > outputLimit) { Kill(); throw new ApiException(502, "import_tool_output_limit", "La herramienta de vídeo ha superado el límite de respuesta."); }
                if (capture) output.Append(buffer, 0, read);
            }
        }
        async Task GuardDisk()
        {
            while (!monitorCancellation.IsCancellationRequested)
            {
                long bytes = 0;
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { bytes += file.Length; } catch (FileNotFoundException) { }
                    if (bytes > workspaceLimit) { Kill(); throw new ApiException(413, "import_workspace_limit", "La importación ha superado el espacio temporal permitido."); }
                }
                await Task.Delay(500, monitorCancellation.Token);
            }
        }
        var outputTask = Read(process.StandardOutput, true);
        var errorTask = Read(process.StandardError, true);
        var diskTask = GuardDisk();
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(outputTask, errorTask);
            monitorCancellation.Cancel();
            try { await diskTask; } catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            return new(process.ExitCode, await outputTask, await errorTask);
        }
        catch
        {
            Kill();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
            monitorCancellation.Cancel();
            try { await Task.WhenAll(outputTask, errorTask, diskTask); } catch { }
            throw;
        }
        finally { monitorCancellation.Cancel(); }
    }
}
