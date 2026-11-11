using System.Text.Json;
using System.Text.RegularExpressions;

namespace FyxBody.Videos.Api.YouTube;

public sealed class TakeoutSources(IConfiguration configuration)
{
    private const string ManifestName = "youtube-id-map.json";
    private const long MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly Regex YouTubeId = new(@"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);
    private readonly Lazy<(string Root, Dictionary<string, string> Paths)> manifest =
        new(() => Load(configuration["VideoImports:TakeoutDirectory"]));

    public bool Enabled => configuration.GetValue<bool>("VideoImports:TakeoutOnly");

    public string[] Ids => [.. manifest.Value.Paths.Keys];

    public string[] AvailableIds => [.. manifest.Value.Paths
        .Where(entry => IsUsable(new FileInfo(Path.Combine(manifest.Value.Root, entry.Value))))
        .Select(entry => entry.Key)];

    private static bool IsUsable(FileInfo file) =>
        file.Exists && file.Length is > 0 and <= MediaProcessRunner.MaxWorkspaceBytes;

    public string SourcePath(string id)
    {
        var (root, paths) = manifest.Value;
        if (!paths.TryGetValue(id, out var relative))
            throw new ApiException(404, "takeout_source_missing", "No local Takeout file is mapped to this YouTube ID.");
        var path = Resolve(root, relative);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
            throw new ApiException(404, "takeout_source_missing", "No local Takeout file is mapped to this YouTube ID.");
        if (file.Length > MediaProcessRunner.MaxWorkspaceBytes)
            throw new ApiException(413, "takeout_source_limit", "The local Takeout source exceeds the maximum size of 1 GiB.");
        return path;
    }

    private static (string Root, Dictionary<string, string> Paths) Load(string? configuredRoot)
    {
        if (string.IsNullOrEmpty(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
            throw InvalidManifest();
        var root = Path.GetFullPath(configuredRoot);
        var manifestFile = new FileInfo(Resolve(root, ManifestName));
        if (!Directory.Exists(root) || !manifestFile.Exists || manifestFile.Length > MaximumManifestBytes)
            throw InvalidManifest();

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = manifestFile.OpenRead();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 2 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidManifest();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!YouTubeId.IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString() is not { Length: > 0 } relative)
                throw InvalidManifest();
            var path = Resolve(root, relative);
            if (Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv")
                || !paths.TryAdd(entry.Name, relative) || !uniquePaths.Add(path))
                throw InvalidManifest();
        }
        return (root, paths);
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Any(char.IsControl) || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw InvalidManifest();
        var path = root;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw InvalidManifest();
        }
        return path;
    }

    private static ApiException InvalidManifest() =>
        new(400, "takeout_invalid_manifest", "The Takeout manifest must contain unique YouTube IDs and safe relative video paths.");
}
