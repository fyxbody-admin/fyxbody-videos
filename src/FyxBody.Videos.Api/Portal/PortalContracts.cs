namespace FyxBody.Videos.Api.Portal;

public sealed record PortalLoginRequest(string Username, string Password);
public sealed record PortalPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record PortalUserRequest(string Username, string DisplayName, string Role, Guid? PartnerId, string? Password);
public sealed record PortalUserUpdate(string DisplayName, bool IsActive);
public sealed record PortalUserResponse(Guid Id, string Username, string DisplayName, string Role, Guid? PartnerId, bool MustChangePassword);
public sealed record PortalUserAdminResponse(Guid Id, string Username, string DisplayName, string Role, Guid? PartnerId, bool MustChangePassword, bool IsActive, DateTimeOffset CreatedAt);
public sealed record PortalSessionResponse(PortalUserResponse? User, string CsrfToken);
public sealed record PortalUserCreatedResponse(PortalUserAdminResponse User, string? TemporaryPassword);
public sealed record PortalUserPage(int Page, int PageSize, int Total, PortalUserAdminResponse[] Items);
