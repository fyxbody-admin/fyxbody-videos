using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace FyxBody.Videos.Api.Localization;

public static class ApiRequestLocalization
{
    public static IServiceCollection AddApiRequestLocalization(this IServiceCollection services)
    {
        services.Configure<RequestLocalizationOptions>(options => {
            options.SetDefaultCulture("es").AddSupportedCultures("es", "ca", "en", "ar").AddSupportedUICultures("es", "ca", "en", "ar");
            options.FallBackToParentCultures = true; options.FallBackToParentUICultures = true;
            options.ApplyCurrentCultureToResponseHeaders = true;
            options.RequestCultureProviders = [new QualityAcceptLanguageProvider()];
        });
        return services;
    }
    private sealed class QualityAcceptLanguageProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
        {
            try
            {
                var values = context.Request.GetTypedHeaders().AcceptLanguage?.Where(value => (value.Quality ?? 1) > 0)
                    .OrderByDescending(value => value.Quality ?? 1).Take(10).Select(value => value.Value).ToArray();
                return Task.FromResult(values is { Length: > 0 } ? new ProviderCultureResult(values) : null);
            }
            catch (FormatException) { return NullProviderCultureResult; }
        }
    }
    public static IApplicationBuilder UseApiRequestLocalization(this IApplicationBuilder app)
    {
        app.UseRequestLocalization(app.ApplicationServices.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
        app.Use(async (context, next) => {
            context.Response.OnStarting(() => {
                var culture = context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.TwoLetterISOLanguageName ?? "es";
                context.Response.Headers.ContentLanguage = culture;
                var vary = context.Response.Headers.Vary.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (!vary.Contains("Accept-Language", StringComparer.OrdinalIgnoreCase) && !vary.Contains("*")) context.Response.Headers.AppendCommaSeparatedValues("Vary", "Accept-Language");
                return Task.CompletedTask;
            });
            await next();
        });
        return app;
    }
}
