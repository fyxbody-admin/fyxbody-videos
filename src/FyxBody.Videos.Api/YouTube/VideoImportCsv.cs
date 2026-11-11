using System.Globalization;
using System.Text;
using FyxBody.Videos.Api.Localization;

namespace FyxBody.Videos.Api.YouTube;

public sealed record VideoImportCsvInput(string? Csv);
public sealed record VideoImportCsvError(string Code, string Message);
public sealed class VideoImportCsvRow
{
    public int RowNumber { get; init; }
    public string? Reference { get; init; }
    public string? Title { get; init; }
    public string Url { get; set; } = "";
    public int[] Heights { get; set; } = [];
    public string? YoutubeId { get; set; }
    public string? EffectiveReference { get; set; }
    public List<VideoImportCsvError> Errors { get; init; } = [];
    public Guid? JobId { get; set; }
    public bool Reused { get; set; }
    public void Error(string code, string message) => Errors.Add(VideoImportCsv.Error(code, message));
}
public sealed record VideoImportCsvReport(bool Queued, bool CanImport, int TotalRows, int ValidRows, int InvalidRows,
    IReadOnlyList<VideoImportCsvRow> Rows, IReadOnlyList<VideoImportCsvError> Errors);

public static class VideoImportCsv
{
    public const int MaximumRows = 500;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumBodyBytes = 2 * 1024 * 1024;
    public static VideoImportCsvError Error(string code, string message) => new(code, ApiMessageCatalog.Translate(message));
    public static VideoImportCsvReport Report(List<VideoImportCsvRow> rows, List<VideoImportCsvError> errors, bool queued = false)
    {
        var invalid = rows.Count(row => row.Errors.Count > 0);
        return new(queued, errors.Count == 0 && invalid == 0 && rows.Count > 0, rows.Count, rows.Count - invalid, invalid, rows, errors);
    }
    private sealed record Record(int Line, string[] Cells);
    public static VideoImportCsvReport Parse(string? csv)
    {
        var rows = new List<VideoImportCsvRow>(); var errors = new List<VideoImportCsvError>();
        void Global(string code, string message) => errors.Add(Error(code, message));
        if (string.IsNullOrWhiteSpace(csv)) { Global("csv_empty", "Select a CSV file containing at least one video."); return Report(rows, errors); }
        try { if (new UTF8Encoding(false, true).GetByteCount(csv) > MaximumBytes) { Global("csv_too_large", "The CSV file must not exceed 1 MiB."); return Report(rows, errors); } }
        catch (EncoderFallbackException) { Global("csv_encoding", "The CSV file must use valid UTF-8 encoding."); return Report(rows, errors); }
        csv = csv.TrimStart('\uFEFF');
        var delimiter = Delimiter(csv);
        var records = Read(csv, delimiter, out var malformed);
        if (malformed) Global("csv_syntax", "The CSV contains malformed quoted fields.");
        if (records.Count == 0) { Global("csv_empty", "Select a CSV file containing at least one video."); return Report(rows, errors); }
        var headers = records[0].Cells.Select(Header).ToArray();
        if (headers.Any(header => header is null) || headers.Distinct().Count() != headers.Length || !headers.Contains("url") || !headers.Contains("heights"))
        { Global("csv_headers", "Use unique Referencia, Titulo, URL and Calidades headers; URL and Calidades are required."); return Report(rows, errors); }
        if (records.Count - 1 > MaximumRows) Global("csv_row_limit", "The CSV must contain at most 500 video rows.");
        foreach (var record in records.Skip(1).Take(MaximumRows + 1))
        {
            string Cell(string name) { var index = Array.IndexOf(headers, name); return index >= 0 && index < record.Cells.Length ? record.Cells[index].Trim() : ""; }
            var row = new VideoImportCsvRow { RowNumber = record.Line, Reference = Null(Cell("reference")), Title = Null(Cell("title")), Url = Cell("url") };
            rows.Add(row);
            if (record.Cells.Length != headers.Length) row.Error("csv_columns", "The row must contain the same number of columns as the header.");
            if (row.Reference is { } reference && (reference.Length > 128 || reference.Any(char.IsControl))) row.Error("csv_reference", "Referencia must contain at most 128 characters without control characters.");
            if (row.Title is { } title && (title.Length > 300 || title.Any(char.IsControl))) row.Error("csv_title", "Titulo must contain at most 300 characters without control characters.");
            var quality = Cell("heights");
            var tokens = quality.Split(';', StringSplitOptions.TrimEntries);
            var heights = new List<int>();
            foreach (var token in tokens)
            {
                var number = token.EndsWith('p') || token.EndsWith('P') ? token[..^1] : token;
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var height) || !YouTubeImportValidation.AvailableHeights.Contains(height))
                { row.Error("csv_heights", "Calidades is required; use 1080p;720p;480p;360p or a subset separated by semicolons."); break; }
                heights.Add(height);
            }
            row.Heights = heights.Distinct().OrderDescending().ToArray();
            try
            {
                var canonical = YouTubeImportValidation.Input(new(row.Url, [360]));
                row.YoutubeId = canonical.YoutubeId; row.Url = canonical.SourceUrl;
                row.EffectiveReference = row.Reference ?? "youtube:" + row.YoutubeId;
                if (row.Reference?.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) == true && row.Reference != "youtube:" + row.YoutubeId)
                    row.Error("csv_reference", "A youtube: reference must match the URL video ID.");
            }
            catch (ApiException) { row.Error("csv_url", "URL is required and must be an accepted HTTPS YouTube video URL."); }
        }
        if (rows.Count == 0) Global("csv_empty", "Select a CSV file containing at least one video.");
        foreach (var group in rows.Where(row => row.EffectiveReference is not null).GroupBy(row => row.EffectiveReference, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var row in group) row.Error("csv_duplicate", "The CSV contains a repeated reference.");
        // One source can have only one active job. Avoid ambiguous duplicate metadata or resolution requests in a batch.
        foreach (var group in rows.Where(row => row.YoutubeId is not null).GroupBy(row => row.YoutubeId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var row in group) if (!row.Errors.Any(error => error.Code == "csv_duplicate")) row.Error("csv_duplicate", "The CSV contains a repeated YouTube video.");
        return Report(rows, errors) with { TotalRows = records.Count - 1 };
    }
    private static string? Null(string value) => value.Length == 0 ? null : value;
    private static string? Header(string text)
    {
        var normalized = string.Concat(text.Trim().Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        return normalized switch { "referencia" or "reference" => "reference", "titulo" or "title" => "title", "url" => "url", "calidades" or "qualities" => "heights", _ => null };
    }
    private static char Delimiter(string text)
    {
        var quoted = false; var commas = 0; var semicolons = 0;
        foreach (var character in text)
        {
            if (character == '"') quoted = !quoted;
            if (quoted) continue;
            if (character is '\r' or '\n') break;
            if (character == ',') commas++; if (character == ';') semicolons++;
        }
        return semicolons > commas ? ';' : ',';
    }
    private static List<Record> Read(string text, char delimiter, out bool malformed)
    {
        var records = new List<Record>(); var cells = new List<string>(); var cell = new StringBuilder();
        var quoted = false; var closed = false; var line = 1; var start = 1; var touched = false; malformed = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"') { if (index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; } else { quoted = false; closed = true; } }
                else { cell.Append(character); if (character == '\n' || character == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')) line++; }
                continue;
            }
            if (character == '"') { if (cell.Length != 0 || closed) { malformed = true; break; } quoted = true; touched = true; continue; }
            if (character == delimiter) { cells.Add(cell.ToString()); cell.Clear(); closed = false; touched = true; continue; }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                if (touched || cells.Count > 0 || cell.Length > 0) { cells.Add(cell.ToString()); records.Add(new(start, cells.ToArray())); }
                cells.Clear(); cell.Clear(); closed = false; touched = false; line++; start = line; continue;
            }
            if (closed) { malformed = true; break; }
            cell.Append(character); touched = true;
        }
        if (quoted) malformed = true;
        if (!malformed && (touched || cells.Count > 0 || cell.Length > 0)) { cells.Add(cell.ToString()); records.Add(new(start, cells.ToArray())); }
        return records;
    }
}
