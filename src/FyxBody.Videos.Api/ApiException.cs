using System.Security.Claims;
using FyxBody.Videos.Data;
namespace FyxBody.Videos.Api;

public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record PartnerIdentity(Guid PartnerId, Guid CredentialId, Guid? PortalUserId = null, Guid? PortalSecurityStamp = null)
{
    public static PartnerIdentity From(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
        Guid.Parse(user.FindFirstValue("credential_id")!),
        Guid.TryParse(user.FindFirstValue("portal_user_id"), out var portalId) ? portalId : null,
        Guid.TryParse(user.FindFirstValue("security_stamp"), out var stamp) ? stamp : null);
}

public interface IVideoUrlSigner
{
    Task<Uri> CreateReadUrlAsync(VideoAsset asset, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
