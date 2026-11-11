using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FyxBody.Videos.Api.Localization;

public static class ApiMessageCatalog
{
    private sealed record Message(string Source, string Es, string Ca, string En, string Ar, string[]? Aliases = null)
    {
        public string In(string locale) => locale switch { "ca" => Ca, "en" => En, "ar" => Ar, _ => Es };
        public IEnumerable<string> Templates => new[] { Source, Es, Ca, En, Ar }.Concat(Aliases ?? []);
    }
    private sealed record Pattern(string Id, Regex Regex, string[] Names);
    private static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Dictionary<string, Message> Messages = Load();
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<Pattern> Patterns = [];
    private static readonly Dictionary<string, string> CodeKeys = new(StringComparer.Ordinal)
    {
        ["invalid_request"] = "request.invalid", ["internal_error"] = "request.failed", ["invalid_credentials"] = "auth.sign_in",
        ["invalid_login"] = "auth.login", ["portal_unauthorized"] = "auth.sign_in", ["partner_unauthorized"] = "auth.partner_unavailable", ["forbidden"] = "auth.forbidden",
        ["invalid_csrf"] = "auth.csrf", ["not_found"] = "request.not_found", ["resource_conflict"] = "request.conflict", ["invalid_resource"] = "request.constraint",
        ["database_unavailable"] = "database.unavailable", ["rate_limit_exceeded"] = "rate.limit", ["monthly_quota_exceeded"] = "quota.exceeded",
        ["video_storage_unavailable"] = "storage.unavailable", ["video_unavailable"] = "video.not_ready", ["video_not_found"] = "video.unavailable", ["asset_not_found"] = "asset.unavailable",
        ["asset_in_use"] = "asset.in_use", ["license_unavailable"] = "license.unavailable", ["session_expired"] = "session.expired", ["session_not_found"] = "session.unavailable",
        ["session_license_changed"] = "session.license_changed", ["invalid_pagination"] = "pagination.partner", ["invalid_month"] = "usage.month", ["invalid_idempotency_key"] = "idempotency.required",
        ["idempotency_conflict"] = "idempotency.conflict", ["concurrent_conflict"] = "concurrency.conflict", ["concurrent_operation"] = "concurrency.retry",
        ["invalid_events"] = "events.count", ["invalid_event"] = "events.invalid", ["invalid_event_time"] = "events.time", ["invalid_event_position"] = "events.position", ["event_conflict"] = "events.conflict",
        ["playback_configuration"] = "playback.configuration", ["invalid_password"] = "password.length", ["password_unchanged"] = "password.unchanged", ["invalid_username"] = "user.username",
        ["invalid_display_name"] = "user.display_name", ["invalid_role_partner"] = "user.role", ["partner_not_found"] = "user.partner_not_found", ["username_exists"] = "user.exists",
        ["self_disable_forbidden"] = "user.self_disable", ["last_admin"] = "user.last_admin", ["portal_user_not_found"] = "user.not_found",
        ["upload_too_large"] = "upload.too_large", ["upload_timeout"] = "upload.timeout", ["video_imports_disabled"] = "import.disabled", ["import_configuration"] = "import.configuration",
        ["import_reference_conflict"] = "csv.source_conflict", ["import_metadata_conflict"] = "csv.existing_title",
        ["import_in_progress"] = "import.in_progress", ["import_interrupted"] = "import.interrupted", ["import_timeout"] = "import.timeout", ["import_asset_conflict"] = "import.asset_conflict",
        ["import_failed"] = "import.failed", ["import_invalid_media"] = "import.invalid_media", ["import_invalid_metadata"] = "import.invalid_metadata", ["import_tools_unavailable"] = "import.tools",
        ["import_tool_output_limit"] = "import.output_limit", ["import_workspace_limit"] = "import.workspace_limit", ["import_duration_limit"] = "import.duration_limit",
        ["import_dimensions_limit"] = "import.dimensions", ["import_file_limit"] = "import.file_limit", ["import_transcode_failed"] = "import.transcode",
        ["takeout_source_missing"] = "takeout.source_missing", ["takeout_invalid_manifest"] = "takeout.invalid_manifest", ["takeout_source_limit"] = "takeout.source_limit",
        ["youtube_rate_limited"] = "youtube.rate_limited", ["youtube_unavailable"] = "youtube.unavailable", ["youtube_download_failed"] = "youtube.download", ["youtube_unsupported"] = "youtube.unsupported"
    };
    static ApiMessageCatalog()
    {
        foreach (var (id, message) in Messages)
        foreach (var template in message.Templates.Distinct(StringComparer.Ordinal))
        {
            var matches = Placeholder.Matches(template);
            if (matches.Count == 0) { Exact.TryAdd(template, id); continue; }
            var names = new List<string>(); var expression = new StringBuilder(@"\A"); var offset = 0;
            foreach (Match match in matches)
            {
                expression.Append(Regex.Escape(template[offset..match.Index]));
                expression.Append("(?<p").Append(names.Count).Append(">.*?)"); names.Add(match.Groups[1].Value); offset = match.Index + match.Length;
            }
            expression.Append(Regex.Escape(template[offset..])).Append(@"\z");
            Patterns.Add(new(id, new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100)), names.ToArray()));
        }
    }
    private static Dictionary<string, Message> Load()
    {
        using var stream = typeof(ApiMessageCatalog).Assembly.GetManifestResourceStream("FyxBody.Videos.Api.Localization.messages.json")
            ?? throw new InvalidOperationException("The API localization catalogue is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Message>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The API localization catalogue is invalid.");
    }
    public static string Locale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ca" or "en" or "ar" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : "es";
    public static bool CanTranslate(string message) => Exact.ContainsKey(message) || Patterns.Any(pattern => pattern.Regex.IsMatch(message));
    public static string Translate(string? message, string? code = null)
    {
        if (message is not null && Exact.TryGetValue(message, out var id)) return Messages[id].In(Locale);
        if (message is not null)
        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(message); if (!match.Success) continue;
            var values = pattern.Names.Select((name, index) => (name, value: match.Groups["p" + index].Value)).ToDictionary(item => item.name, item => item.value);
            if (pattern.Id == "resource.not_found" && values.TryGetValue("resource", out var resource)) values["resource"] = ResourceName(resource);
            return Placeholder.Replace(Messages[pattern.Id].In(Locale), token => values.GetValueOrDefault(token.Groups[1].Value, token.Value));
        }
        return ForCode(code);
    }
    public static string ForCode(string? code) => Messages[code is not null && CodeKeys.TryGetValue(code, out var id) ? id : "request.failed"].In(Locale);
    public static string? JobError(string? code, string? storedMessage, string status)
    {
        if (storedMessage is null && status != "failed") return null;
        // Persisted legacy text is not trusted as a template parameter or diagnostic message.
        if (storedMessage is not null && Exact.TryGetValue(storedMessage, out var id) && id != "import.failed") return Messages[id].In(Locale);
        return code is not null && CodeKeys.ContainsKey(code) ? ForCode(code) : Messages["import.failed"].In(Locale);
    }
    private static string ResourceName(string resource)
    {
        var id = resource switch { "partner" => "resource.partner", "license" => "resource.license", "video" => "resource.video", "asset" => "resource.asset", "credential" => "resource.credential", _ => null };
        return id is null ? resource : Messages[id].In(Locale);
    }
}
