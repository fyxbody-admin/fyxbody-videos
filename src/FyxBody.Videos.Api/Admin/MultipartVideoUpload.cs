using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FyxBody.Videos.Api.Admin;

public sealed class MultipartVideoUpload : IAsyncDisposable
{
    public const long MaximumFileBytes = 200L * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumFileBytes + 64 * 1024;
    public FileStream File { get; }
    public string Language { get; }
    public string Rendition { get; }
    public int DurationMs { get; }
    public string? Title { get; }
    public string? PartnerReference { get; }
    private MultipartVideoUpload(FileStream file, string language, string rendition, int durationMs, string? title, string? partnerReference)
    { File = file; Language = language; Rendition = rendition; DurationMs = durationMs; Title = title; PartnerReference = partnerReference; }

    public static async Task<MultipartVideoUpload> ReadAsync(HttpRequest request, CancellationToken ct, bool privateUpload = false)
    {
        if (request.ContentLength > MaximumRequestBytes) throw TooLarge();
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaximumRequestBytes;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Use multipart/form-data with file, language, rendition and durationMs.");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw Invalid("The multipart boundary is invalid.");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var path = Path.Combine(Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath(), $"fyxbody-video-{Guid.NewGuid():N}.tmp");
        var file = new FileStream(path, options);
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var reader = new MultipartReader(boundary, request.Body) { HeadersCountLimit = 8, HeadersLengthLimit = 8192 };
            var seenFile = false;
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("Invalid multipart section.");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                {
                    if (seenFile || name != "file") throw Invalid("Exactly one file field is required.");
                    seenFile = true;
                    var filename = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "";
                    if (!filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw Invalid("The file must have an .mp4 extension.");
                    if (!MediaTypeHeaderValue.TryParse(section.ContentType, out var type) || !(type.MediaType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) || type.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
                        throw Invalid("The file content type must be video/mp4 or application/octet-stream.");
                    await CopyLimited(section.Body, file, MaximumFileBytes, ct);
                }
                else
                {
                    if ((name is not ("language" or "rendition" or "durationMs") && !(privateUpload && name is ("title" or "partnerReference"))) || fields.ContainsKey(name)) throw Invalid("Unknown or duplicate multipart field.");
                    using var value = new MemoryStream();
                    await CopyLimited(section.Body, value, 1024, ct, field: true);
                    fields.Add(name, Encoding.UTF8.GetString(value.ToArray()));
                }
            }
            if (!seenFile || file.Length < 16 || !fields.TryGetValue("language", out var language) || !fields.TryGetValue("rendition", out var rendition)
                || !fields.TryGetValue("durationMs", out var duration) || !int.TryParse(duration, NumberStyles.None, CultureInfo.InvariantCulture, out var durationMs) || durationMs <= 0)
                throw Invalid("A nonempty MP4 file, language, rendition and positive integer durationMs are required.");
            file.Position = 0;
            var header = new byte[16]; await file.ReadExactlyAsync(header, ct);
            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (!header.AsSpan(4, 4).SequenceEqual("ftyp"u8) || boxSize < 16 || boxSize > file.Length) throw Invalid("The file must start with an MP4 ftyp box.");
            file.Position = 0;
            return new(file, language, rendition, durationMs, fields.GetValueOrDefault("title"), fields.GetValueOrDefault("partnerReference"));
        }
        catch (InvalidDataException) { await file.DisposeAsync(); throw Invalid("The multipart request could not be read."); }
        catch { await file.DisposeAsync(); throw; }
    }

    private static async Task CopyLimited(Stream input, Stream output, long limit, CancellationToken ct, bool field = false)
    {
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > limit) throw field ? Invalid("Multipart field is too long.") : TooLarge();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    private static ApiException TooLarge() => new(413, "upload_too_large", "The MP4 file must be no larger than 200 MiB.");
    public ValueTask DisposeAsync() => File.DisposeAsync();
}
