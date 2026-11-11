using System.Text.RegularExpressions;
using FyxBody.Videos.Api.Admin;
using Microsoft.AspNetCore.WebUtilities;

namespace FyxBody.Videos.Api.YouTube;

public static class YouTubeImportValidation
{
    public static readonly int[] AvailableHeights = [1080, 720, 480, 360];
    public static readonly int[] DefaultHeights = [1080, 720, 480];
    public static (string YoutubeId, string SourceUrl, int[] Heights) Input(VideoImportInput input)
    {
        var url = AdminValidation.Text(input.Url, 512, "url");
        var match = Regex.Match(url, @"\A(?i:https://)(?<host>(?i:youtube\.com|www\.youtube\.com|m\.youtube\.com|youtu\.be))(?<path>/[^?#\s]*)?(?<query>\?[^#\s]*)?\z");
        if (!match.Success) throw Invalid();
        var host = match.Groups["host"].Value.ToLowerInvariant();
        var path = match.Groups["path"].Value;
        var query = QueryHelpers.ParseQuery(match.Groups["query"].Value);
        var tracking = new[] { "si", "feature", "t", "start" };
        if (query.Keys.Any(key => !tracking.Contains(key, StringComparer.Ordinal) && !(host != "youtu.be" && path == "/watch" && key == "v"))) throw Invalid();
        string id;
        if (host == "youtu.be") { if (!path.StartsWith('/')) throw Invalid(); id = path[1..]; }
        else if (path == "/watch") { if (!query.TryGetValue("v", out var values) || values.Count != 1) throw Invalid(); id = values.ToString(); }
        else if (path.StartsWith("/shorts/", StringComparison.Ordinal) || path.StartsWith("/embed/", StringComparison.Ordinal))
        { id = path[(path.IndexOf('/', 1) + 1)..]; }
        else throw Invalid();
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z")) throw Invalid();
        var heights = input.Heights ?? DefaultHeights;
        if (heights.Length is < 1 or > 4 || heights.Distinct().Count() != heights.Length || heights.Any(height => !AvailableHeights.Contains(height)))
            throw new ApiException(400, "invalid_request", "heights must contain one to four distinct values from 1080, 720, 480 and 360.");
        return (id, $"https://www.youtube.com/watch?v={id}", heights.OrderDescending().ToArray());
    }
    private static ApiException Invalid() => new(400, "invalid_request", "Use an HTTPS YouTube watch, shorts, embed or youtu.be URL with a single eleven-character video ID. Only si, feature, t and start tracking parameters are accepted and discarded.");
}
