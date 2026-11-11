using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FyxBody.Videos.Api;
using FyxBody.Videos.Api.YouTube;
using FyxBody.Videos.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FyxBody.Videos.Tests;

public sealed partial class YouTubeImportTests
{
    private static string CsvText(string id, string reference = "", string title = "", string heights = "1080p;720p;480p") =>
        $"Referencia,Titulo,URL,Calidades\r\n{reference},{title},https://youtu.be/{id},{heights}\r\n";
    private static async Task<VideoImportCsvReport> Csv(TestApp app, string csv, bool enqueue = true)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<YouTubeImportQueue>().Csv(new(csv), enqueue, default);
        if (enqueue) foreach (var row in result.Rows.Where(row => row.JobId is not null)) app.Jobs.Add(row.JobId!.Value);
        return result;
    }

    [Theory]
    [InlineData("Referencia,Título,URL,Calidades\r\nREF1,\"Sentadilla, con \"\"peso\"\"\",https://youtu.be/WhpknPP0xKU,1080p;720;720p")]
    [InlineData("\uFEFFREFERENCIA;TÍTULO;url;calidades\nREF1;\"Sentadilla, con \"\"peso\"\"\";https://youtu.be/WhpknPP0xKU;\"1080p;720;720p\"")]
    public void CsvAcceptsBomAccentsBothDelimitersAndEscapedQuotes(string csv)
    {
        var result = VideoImportCsv.Parse(csv); Assert.True(result.CanImport); var row = Assert.Single(result.Rows);
        Assert.Equal(2, row.RowNumber); Assert.Equal("REF1", row.Reference); Assert.Equal("Sentadilla, con \"peso\"", row.Title);
        Assert.Equal(new[] {1080,720}, row.Heights); Assert.Equal("REF1", row.EffectiveReference);
    }
    [Fact]
    public void CsvOptionalHeadersAndCellsUseDefaultsWhileRequiredValuesDoNot()
    {
        var row = Assert.Single(VideoImportCsv.Parse("URL,Calidades\nhttps://youtu.be/WhpknPP0xKU,360").Rows);
        Assert.Null(row.Reference); Assert.Null(row.Title); Assert.Equal("youtube:WhpknPP0xKU", row.EffectiveReference);
        var invalid = VideoImportCsv.Parse("Referencia,Titulo,URL,Calidades\nREF1,Title,,\n");
        Assert.False(invalid.CanImport); Assert.Contains(invalid.Rows[0].Errors, error => error.Code == "csv_url"); Assert.Contains(invalid.Rows[0].Errors, error => error.Code == "csv_heights");
    }
    [Theory]
    [InlineData("URL,Calidades\n\"https://youtu.be/WhpknPP0xKU,360", "csv_syntax")]
    [InlineData("URL,Calidades\n\"https://youtu.be/WhpknPP0xKU\"oops,360", "csv_syntax")]
    [InlineData("URL,URL,Calidades\na,b,c", "csv_headers")]
    [InlineData("URL,Titulo\na,b", "csv_headers")]
    [InlineData("", "csv_empty")]
    public void CsvStructuralErrorsAreReported(string csv, string code)
    { var result = VideoImportCsv.Parse(csv); Assert.False(result.CanImport); Assert.Contains(result.Errors, error => error.Code == code); }
    [Fact]
    public void CsvPhysicalRowNumbersSurviveQuotedNewlinesAndLimitsCountAllRecords()
    {
        var result = VideoImportCsv.Parse("Referencia,Titulo,URL,Calidades\r\nREF1,\"First\r\nSecond\",https://youtu.be/WhpknPP0xKU,360\r\nREF2,Title,https://youtu.be/Abcdef12345,480");
        Assert.Equal(2, result.Rows[0].RowNumber); Assert.Equal(4, result.Rows[1].RowNumber); Assert.Contains(result.Rows[0].Errors, error => error.Code == "csv_title"); Assert.Empty(result.Errors);
        var many = VideoImportCsv.Parse("URL,Calidades\n" + string.Concat(Enumerable.Repeat("https://youtu.be/WhpknPP0xKU,360\n", 503)));
        Assert.Equal(503, many.TotalRows); Assert.Contains(many.Errors, error => error.Code == "csv_row_limit");
        Assert.Contains(VideoImportCsv.Parse(new string('a', VideoImportCsv.MaximumBytes + 1)).Errors, error => error.Code == "csv_too_large");
        Assert.Contains(VideoImportCsv.Parse("\ud800").Errors, error => error.Code == "csv_encoding");
    }
    [Fact]
    public void CsvRejectsReservedReferencesDuplicateSourcesAndReferences()
    {
        var reserved = VideoImportCsv.Parse(CsvText("WhpknPP0xKU", "youtube:Abcdef12345"));
        Assert.Contains(reserved.Rows[0].Errors, error => error.Code == "csv_reference");
        var duplicate = VideoImportCsv.Parse(CsvText("WhpknPP0xKU", "REF") + "REF,Other,https://youtu.be/Abcdef12345,360\n");
        Assert.All(duplicate.Rows, row => Assert.Contains(row.Errors, error => error.Code == "csv_duplicate"));
        var same = VideoImportCsv.Parse(CsvText("WhpknPP0xKU", "REF") + "REF2,Other,https://youtu.be/WhpknPP0xKU,360\n");
        Assert.All(same.Rows, row => Assert.Contains(row.Errors, error => error.Code == "csv_duplicate"));
    }
    [Fact]
    public async Task CsvMalformedJsonAndOversizedBodiesAreBoundedWithoutDatabase()
    {
        await using var app = new TestApp(database: false); using var client = app.Admin();
        foreach (var path in new[] {"/admin/v1/video-imports/csv", "/admin/v1/video-imports/csv/preview"})
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { csv = 17 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { other = "value" })).StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(path, new StringContent("data"))).StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(path, new StringContent(new string(' ', VideoImportCsv.MaximumBodyBytes + 1), Encoding.UTF8, "application/json"))).StatusCode);
        }
    }
    [PostgresFact]
    public async Task CsvPreviewIsReadOnlyAndCommitPersistsOptionalMetadataForWorker()
    {
        await using var app = new TestApp(); var id = Id(); var reference = "CSV-" + Guid.NewGuid(); var text = CsvText(id, reference, "Custom exercise", "720;480p");
        var preview = await Csv(app, text, false); Assert.True(preview.CanImport); Assert.False(preview.Queued); Assert.Null(preview.Rows[0].JobId);
        await using (var scope = app.App.Services.CreateAsyncScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<VideosDbContext>().VideoImportJobs.AnyAsync(job => job.YoutubeId == id));
        var queued = await Csv(app, text); Assert.True(queued.Queued); var claimed = (await app.Claim())!;
        Assert.Equal(reference, claimed.Reference); Assert.Equal("Custom exercise", claimed.RequestedTitle);
        await app.Run(claimed); var done = await app.Get(queued.Rows[0].JobId!.Value); Assert.Equal("completed", done.Status); Assert.Equal("Custom exercise", done.Title);
        await using var check = app.App.Services.CreateAsyncScope(); var video = await check.ServiceProvider.GetRequiredService<VideosDbContext>().Videos.SingleAsync(video => video.Id == done.VideoId);
        Assert.Equal(reference, video.ExternalExerciseId); Assert.Equal("Custom exercise", video.Title); Assert.Equal(new[] {720,480}, Assert.Single(app.Media.Calls));
    }
    [PostgresFact]
    public async Task CsvBlankMetadataUsesYouTubeTitleAndCanonicalReference()
    {
        await using var app = new TestApp(); var id = Id(); var queued = await Csv(app, CsvText(id)); await app.Run((await app.Claim())!);
        var done = await app.Get(queued.Rows[0].JobId!.Value); Assert.Equal("completed", done.Status); Assert.Equal("Media title", done.Title);
        await using var scope = app.App.Services.CreateAsyncScope(); Assert.Equal("youtube:" + id, (await scope.ServiceProvider.GetRequiredService<VideosDbContext>().Videos.SingleAsync(video => video.Id == done.VideoId)).ExternalExerciseId);
    }
    [PostgresFact]
    public async Task CsvAnyInvalidRowPreventsAllJobsAndConcurrentReferencesHaveOneWinner()
    {
        await using var app = new TestApp(); var first = Id(); var second = Id(); var reference = "CSV-" + Guid.NewGuid();
        var invalid = await Csv(app, CsvText(first) + $",,https://youtu.be/{second},\n"); Assert.False(invalid.Queued); Assert.Equal(1, invalid.InvalidRows);
        await using (var scope = app.App.Services.CreateAsyncScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<VideosDbContext>().VideoImportJobs.AnyAsync(job => job.YoutubeId == first || job.YoutubeId == second));
        var results = await Task.WhenAll(Csv(app, CsvText(first, reference)), Csv(app, CsvText(second, reference)));
        Assert.Single(results, report => report.Queued); Assert.Single(results, report => !report.Queued && report.Rows[0].Errors.Any(error => error.Code == "csv_reference_conflict"));
        await using var check = app.App.Services.CreateAsyncScope(); Assert.Equal(1, await check.ServiceProvider.GetRequiredService<VideosDbContext>().VideoImportJobs.CountAsync(job => job.Reference == reference));
    }
    [PostgresFact]
    public async Task CsvReusesOnlyMatchingActiveMetadataAndCoveredHeightsWithSingleEndpointRace()
    {
        await using var app = new TestApp(); var id = Id();
        var single = app.Enqueue(id, [720]); var bulk = Csv(app, CsvText(id, heights:"720"));
        await Task.WhenAll(single, bulk); Assert.True(bulk.Result.Queued);
        Assert.Equal(single.Result.Id, bulk.Result.Rows[0].JobId);
        var reused = await Csv(app, CsvText(id, heights:"720")); Assert.True(reused.Queued); Assert.True(reused.Rows[0].Reused);
        var extra = await Csv(app, CsvText(id, heights:"1080")); Assert.False(extra.Queued); Assert.Contains(extra.Rows[0].Errors, error => error.Code == "csv_import_in_progress");
        var title = await Csv(app, CsvText(id, title:"Changed", heights:"720")); Assert.False(title.Queued); Assert.Contains(title.Rows[0].Errors, error => error.Code == "csv_metadata_conflict");
        var reference = await Csv(app, CsvText(id, "OTHER", heights:"720")); Assert.False(reference.Queued);
    }
    [PostgresFact]
    public async Task CsvPreservesLegacyNullReferenceAndExistingTitlesAndReadyAssets()
    {
        await using var app = new TestApp(); var id = Id(); var video = await Existing(app, id, 720); var old = new VideoImportJob { YoutubeId = id, SourceUrl = "https://www.youtube.com/watch?v=" + id, RequestedHeights = [720], UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        await using (var scope = app.App.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>(); db.VideoImportJobs.Add(old); await db.SaveChangesAsync(); app.Jobs.Add(old.Id); }
        var reused = await Csv(app, CsvText(id, heights:"720")); Assert.True(reused.Queued); Assert.True(reused.Rows[0].Reused); Assert.Equal(old.Id, reused.Rows[0].JobId);
        await app.Run((await app.Claim())!); Assert.Empty(app.Media.Calls); Assert.Empty(app.Storage.Uploads); Assert.Equal(video.Id, (await app.Get(old.Id)).VideoId);
        var conflict = await Csv(app, CsvText(id, title:"Different", heights:"720")); Assert.False(conflict.Queued); Assert.Contains(conflict.Rows[0].Errors, error => error.Code == "csv_metadata_conflict");
    }
    [PostgresFact]
    public async Task CsvRejectsUnknownCustomSourceAndRunnerRejectsCreationRaceBeforeStorage()
    {
        await using var app = new TestApp(); var reference = "CSV-" + Guid.NewGuid(); var id = Id();
        var queued = await Csv(app, CsvText(id, reference, "Requested", "360")); var claimed = (await app.Claim())!;
        app.Media.BeforePrepare = async _ => {
            await using var scope = app.App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VideosDbContext>();
            db.Videos.Add(new Video { ExternalExerciseId = reference, Title = "Manual unrelated" }); await db.SaveChangesAsync();
        };
        await app.Run(claimed); var failed = await app.Get(queued.Rows[0].JobId!.Value); Assert.Equal("failed", failed.Status); Assert.Equal("import_reference_conflict", failed.ErrorCode);
        Assert.Null(failed.VideoId); Assert.Empty(app.Storage.Uploads);
        var again = await Csv(app, CsvText(Id(), reference, heights:"360")); Assert.False(again.Queued); Assert.Contains(again.Rows[0].Errors, error => error.Code == "csv_reference_conflict");
    }
    [PostgresFact]
    public async Task CsvCustomSourceRetryReusesProofAndReferenceChangedDuringUploadIsCompensated()
    {
        await using var app = new TestApp(); var reference = "CSV-" + Guid.NewGuid(); var id = Id();
        var first = await Csv(app, CsvText(id, reference, "Custom", "360")); await app.Run((await app.Claim())!); Assert.Equal("completed", (await app.Get(first.Rows[0].JobId!.Value)).Status);
        var retry = await Csv(app, CsvText(id, reference, heights:"360")); Assert.True(retry.Queued); await app.Run((await app.Claim())!); Assert.Single(app.Media.Calls); Assert.Single(app.Storage.Uploads);
        var extra = await Csv(app, CsvText(id, reference, heights:"480"));
        app.Storage.OnUpload = async (asset, _) => { await using var scope = app.App.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<VideosDbContext>().Videos.Where(video => video.Id == asset.VideoId).ExecuteUpdateAsync(update => update.SetProperty(video => video.ExternalExerciseId, reference + "-changed")); };
        await app.Run((await app.Claim())!); Assert.Equal("import_reference_conflict", (await app.Get(extra.Rows[0].JobId!.Value)).ErrorCode); Assert.Single(app.Storage.Deletes);
    }
    [Theory]
    [InlineData("es", "URL es obligatorio")]
    [InlineData("ca", "URL és obligatori")]
    [InlineData("en", "URL is required")]
    [InlineData("ar", "URL إلزامي")]
    public void CsvRowErrorsAreLocalized(string language, string expected)
    {
        var before = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language); var row = VideoImportCsv.Parse("URL,Calidades\n,360").Rows[0]; Assert.Contains(expected, row.Errors[0].Message); }
        finally { CultureInfo.CurrentUICulture = before; }
    }
}
