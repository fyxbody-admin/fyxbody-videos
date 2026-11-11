using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text.RegularExpressions;
namespace FyxBody.Videos.Api;

public static class ApiOpenApi
{
    public static void Configure(OpenApiOptions options, string documentName)
    {
        options.ShouldInclude = description => documentName == "v1" || description.GroupName == documentName;
        options.AddDocumentTransformer((document, context, _) =>
        {
            document.Info = new OpenApiInfo { Title = $"FYXBODY Videos API ({documentName})", Version = "1.0.0",
                Description = "Server-to-server video catalogue and playback API. API keys must never be embedded in a browser or mobile application." };
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            document.Components.Schemas["ApiProblem"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object, Required = new HashSet<string> { "status", "title", "code", "requestId" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["requestId"] = new OpenApiSchema { Type = JsonSchemaType.String }
                }
            };
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                ["PartnerKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "keyId.secret", Description = "Partner backend credential, returned once at creation." },
                ["AdminKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = "X-Admin-Key", In = ParameterLocation.Header, Description = "FYXBODY administrator only; never share with a partner." },
                ["PortalCookie"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = context.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment() ? "fyxbody.portal" : "__Host-fyxbody.portal", Description = "Personal browser session. HttpOnly cookie; unsafe requests require X-CSRF-TOKEN obtained from /portal/api/session." }
            };
            foreach (var path in document.Paths)
            foreach (var operation in path.Value.Operations ?? [])
            {
                var portalPath = path.Key.StartsWith("/portal/api/", StringComparison.Ordinal);
                if (!portalPath && !path.Key.StartsWith("/v1/", StringComparison.Ordinal) && !path.Key.StartsWith("/admin/v1/", StringComparison.Ordinal)) continue;
                operation.Value.OperationId ??= operation.Key.ToString().ToLowerInvariant() + "_" + Regex.Replace(path.Key.Trim('/'), "[^a-zA-Z0-9]+", "_").TrimEnd('_');
                var scheme = path.Key.StartsWith("/admin/", StringComparison.Ordinal) ? "AdminKey" : "PartnerKey";
                operation.Value.Security = portalPath
                    ? path.Key is "/portal/api/session" or "/portal/api/login" ? [] : [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("PortalCookie", document)] = [] }]
                    : [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] }, new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("PortalCookie", document)] = [] }];
                if (operation.Key.ToString().ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE")
                {
                    operation.Value.Parameters ??= [];
                    operation.Value.Parameters.Add(new OpenApiParameter { Name = "X-CSRF-TOKEN", In = ParameterLocation.Header, Required = portalPath,
                        Description = "Required for browser cookie authentication; obtain a current token from /portal/api/session. API-key clients do not send it.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
                }
                operation.Value.Responses ??= new OpenApiResponses();
                foreach (var status in new[] { "400", "401", "403", "404", "409", "422", "429", "503" })
                    operation.Value.Responses.TryAdd(status, new OpenApiResponse
                    {
                        Description = "Problem Details with stable code and requestId; 429 includes Retry-After.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ApiProblem", document) } }
                    });
                var privateUpload = path.Key.EndsWith("/private-videos", StringComparison.Ordinal)
                    && operation.Key.ToString().Equals("Post", StringComparison.OrdinalIgnoreCase);
                if (privateUpload || path.Key.EndsWith("/assets/upload", StringComparison.Ordinal))
                {
                    operation.Value.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["multipart/form-data"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Object, Required = new HashSet<string> { "file", "language", "rendition", "durationMs" },
                                    Properties = new Dictionary<string, IOpenApiSchema>
                                    {
                                        ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", Description = "MP4 file, 1–209715200 bytes, with ftyp header. Server generates the blob path; original filename is never used as a storage path." },
                                        ["language"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 35 },
                                        ["rendition"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[A-Za-z0-9_-]{1,50}$", MaxLength = 50 },
                                        ["durationMs"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "2147483647", Description = "Positive duration in milliseconds supplied by the client; not verified by server-side decoding." }
                                    }
                                }
                            }
                        }
                    };
                    if (privateUpload && operation.Value.RequestBody.Content!["multipart/form-data"].Schema is OpenApiSchema schema)
                    {
                        schema.Required!.Add("title");
                        schema.Properties!["title"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 300 };
                        schema.Properties["partnerReference"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 128 };
                        var idempotency = operation.Value.Parameters?.OfType<OpenApiParameter>()
                            .SingleOrDefault(parameter => parameter.Name == "Idempotency-Key");
                        if (idempotency is not null) idempotency.Required = true;
                    }
                    foreach (var status in new[] { "413", "504" })
                        operation.Value.Responses[status] = new OpenApiResponse { Description = status == "413" ? "upload_too_large: maximum file size 200 MiB." : "upload_timeout: ten-minute upload limit exceeded.",
                            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ApiProblem", document) } } };
                }
                if (path.Key.EndsWith("/playback-sessions", StringComparison.Ordinal))
                {
                    operation.Value.Parameters ??= [];
                    var header = operation.Value.Parameters.FirstOrDefault(p => p.Name == "Idempotency-Key" && p.In == ParameterLocation.Header);
                    if (header is OpenApiParameter mutable)
                    {
                        mutable.Required = true;
                        mutable.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 };
                        continue;
                    }
                    if (header is not null) operation.Value.Parameters.Remove(header);
                    operation.Value.Parameters.Add(new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                        Description = "Unique per partner and logical playback request. Retries must reuse this key and the same payload.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 128 } });
                }
            }
            return Task.CompletedTask;
        });
    }
}
