namespace MiniERP.Api.Models;

// ------------------------------------------------------------------ security

public record LoginRequest(string Username, string Password);

public record AuthUserDto(
    int Id,
    string Username,
    string? FullName,
    string? Department,
    IReadOnlyList<string> Roles
);

public record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    DateTimeOffset ExpiresAtUtc,
    AuthUserDto User,
    string? RefreshToken = null
);

public record RefreshTokenRequest(string RefreshToken);

public record RevokeTokenRequest(string? RefreshToken = null);

public record TokenRefreshResponse(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresInSeconds,
    DateTimeOffset ExpiresAtUtc
);

public record AuthMeResponse(
    string Username,
    string? FullName,
    string? Department,
    IReadOnlyList<string> Roles
);

public record CreateUserRequest(
    string Username,
    string Password,
    string FullName,
    string Department,
    IReadOnlyList<string> Roles
);

public record UserSummaryDto(
    int Id,
    string Username,
    string? FullName,
    string? Department,
    bool IsActive,
    IReadOnlyList<string> Roles
);

// ------------------------------------------------------------- operations

public record HealthDetailsResponse(
    bool Ready,
    string DatabaseStatus,
    string? DatabaseBanner,
    int TableCount,
    string? OperationsPackageStatus,
    string? AutomationPackageStatus,
    string? TraceabilityPackageStatus,
    string? TraceabilityPackageVersion,
    DateTime CheckedAtUtc,
    string? ErrorCode
);
