using System.Text.RegularExpressions;

namespace FyxBody.Videos.Api.Admin;

public static partial class AdminValidation
{
    public static string Text(string? value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum || value.Any(char.IsControl))
            throw Invalid($"{field} debe contener entre 1 y {maximum} caracteres sin caracteres de control.");
        return value.Trim();
    }

    public static string State(string? value, params string[] allowed)
    {
        if (value is null || !allowed.Contains(value)) throw Invalid($"status debe ser uno de: {string.Join(", ", allowed)}.");
        return value;
    }

    public static PartnerInput Partner(PartnerInput input)
    {
        var code = Text(input.Code, 64, "code");
        if (!PartnerCode().IsMatch(code)) throw Invalid("code debe usar minúsculas, números, guion o guion bajo y empezar por letra o número.");
        return input with { Code = code, Name = Text(input.Name, 200, "name"), Status = State(input.Status, "active", "suspended", "revoked") };
    }

    public static LicenseInput License(LicenseInput input)
    {
        if (input.StartsAt.Offset != TimeSpan.Zero || input.EndsAt.Offset != TimeSpan.Zero || input.EndsAt <= input.StartsAt)
            throw Invalid("startsAt y endsAt deben ser UTC y endsAt debe ser posterior a startsAt.");
        if (input.MonthlySessionQuota < 0) throw Invalid("monthlySessionQuota debe ser mayor o igual a cero.");
        return input with { Name = Text(input.Name, 200, "name"), Status = State(input.Status, "active", "suspended", "revoked"), MonthlySessionQuota = input.UnlimitedSessions == true ? 0 : input.MonthlySessionQuota };
    }

    public static VideoInput Video(VideoInput input) => input with
    {
        ExternalExerciseId = Text(input.ExternalExerciseId, 128, "externalExerciseId"),
        Title = Text(input.Title, 300, "title"), Status = State(input.Status, "draft", "active", "archived")
    };

    public static AssetInput Asset(AssetInput input)
    {
        var language = Text(input.Language, 35, "language");
        var rendition = Text(input.Rendition, 50, "rendition");
        var container = Text(input.BlobContainer, 63, "blobContainer");
        var name = Text(input.BlobName, 1024, "blobName");
        if (!Language().IsMatch(language)) throw Invalid("language debe ser una etiqueta de idioma admitida, por ejemplo es o en-US.");
        if (!Container().IsMatch(container)) throw Invalid("blobContainer debe ser un nombre válido de contenedor Azure (3–63 caracteres).");
        if (name.StartsWith('/') || name.Contains('\\') || name.Contains('?') || name.Contains('#') || name.Contains(':') || name.Contains('%') || name.Split('/').Any(x => x is "" or "." or ".."))
            throw Invalid("blobName debe ser una ruta relativa de blob, sin URL, query, codificación ni segmentos vacíos o de navegación.");
        if (input.ContentType != "video/mp4") throw Invalid("contentType debe ser video/mp4.");
        var status = State(input.Status, "pending", "ready", "disabled");
        if (input.SizeBytes < 0 || input.DurationMs < 0 || (status == "ready" && (input.SizeBytes == 0 || input.DurationMs == 0)))
            throw Invalid("sizeBytes y durationMs no pueden ser negativos y deben ser positivos para un asset ready.");
        return input with { Language = language, Rendition = rendition, BlobContainer = container, BlobName = name, Status = status };
    }

    public static Guid[] Catalogue(Guid[]? ids)
    {
        if (ids is null || ids.Length > 1000 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw Invalid("videoIds es obligatorio; máximo 1000 UUID no vacíos, sin duplicados.");
        return ids;
    }

    public static (int Page, int PageSize) Pagination(int? page, int? pageSize)
    {
        var p = page ?? 1;
        var size = pageSize ?? 25;
        if (p < 1 || size is < 1 or > 100 || ((long)p - 1) * size > int.MaxValue)
            throw Invalid("page debe ser positivo y pageSize debe estar entre 1 y 100.");
        return (p, size);
    }

    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)] private static partial Regex PartnerCode();
    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)] private static partial Regex Language();
    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)] private static partial Regex Container();
}
