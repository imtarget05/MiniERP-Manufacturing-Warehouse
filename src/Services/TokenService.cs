using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MiniERP.Api.Services;

public sealed class JwtOptions
{
    public string Issuer { get; init; } = "MiniERP";
    public string Audience { get; init; } = "MiniERP.Api";
    public string SigningKey { get; init; } = string.Empty;
    public int ExpiryMinutes { get; init; } = 30;

    public static JwtOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var issuer = configuration["Jwt:Issuer"] ?? configuration["JWT_ISSUER"] ?? "MiniERP";
        var audience = configuration["Jwt:Audience"] ?? configuration["JWT_AUDIENCE"] ?? "MiniERP.Api";
        var key = configuration["Jwt:SigningKey"] ?? configuration["JWT_SIGNING_KEY"] ?? string.Empty;
        var expiry = int.TryParse(configuration["Jwt:ExpiryMinutes"] ?? configuration["JWT_EXPIRY_MINUTES"], out var value) ? value : 30;
        if (expiry is < 1 or > 1440) throw new InvalidOperationException("JWT_EXPIRY_MINUTES must be between 1 and 1440.");
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("JWT_SIGNING_KEY is required outside Development.");
            key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("JWT_SIGNING_KEY must be at least 32 bytes.");
        return new JwtOptions { Issuer = issuer, Audience = audience, SigningKey = key, ExpiryMinutes = expiry };
    }
}

public sealed record AuthenticatedUser(int Id, string Username, string? FullName, string? Department, IReadOnlyList<string> Roles);
public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Dependency-free compact bearer token service (HS256).</summary>
public sealed class TokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JwtOptions _options;
    private readonly byte[] _key;

    public TokenService(JwtOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32) throw new ArgumentException("JWT signing key must be at least 32 bytes.", nameof(options));
        _key = Encoding.UTF8.GetBytes(options.SigningKey);
    }

    public IssuedToken Issue(AuthenticatedUser user)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.ExpiryMinutes);
        var header = new Dictionary<string, object?> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = _options.Issuer,
            ["aud"] = _options.Audience,
            ["sub"] = user.Id.ToString(),
            ["name"] = user.Username,
            ["full_name"] = user.FullName,
            ["department"] = user.Department,
            ["roles"] = user.Roles,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N")
        };
        var encodedHeader = Encode(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
        var encodedPayload = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var input = $"{encodedHeader}.{encodedPayload}";
        return new IssuedToken($"{input}.{Encode(Sign(input))}", expires);
    }

    public bool TryValidate(string? token, out ClaimsPrincipal? principal, out string? error)
    {
        principal = null;
        error = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192)
        {
            error = "Bearer token is missing or too large.";
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            error = "Bearer token has an invalid compact format.";
            return false;
        }

        byte[] signature;
        byte[] headerBytes;
        byte[] payloadBytes;
        try
        {
            signature = Decode(parts[2]);
            headerBytes = Decode(parts[0]);
            payloadBytes = Decode(parts[1]);
        }
        catch (FormatException)
        {
            error = "Bearer token contains invalid base64url data.";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign($"{parts[0]}.{parts[1]}")))
        {
            error = "Bearer token signature is invalid.";
            return false;
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            using var payload = JsonDocument.Parse(payloadBytes);
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256")
            {
                error = "Bearer token algorithm is not accepted.";
                return false;
            }

            if (!payload.RootElement.TryGetProperty("iss", out var issuer) ||
                issuer.GetString() != _options.Issuer ||
                !payload.RootElement.TryGetProperty("aud", out var audience) ||
                audience.GetString() != _options.Audience ||
                !payload.RootElement.TryGetProperty("sub", out var subject) ||
                !int.TryParse(subject.GetString(), out var userId) ||
                !payload.RootElement.TryGetProperty("name", out var name) ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                !payload.RootElement.TryGetProperty("exp", out var exp) ||
                !exp.TryGetInt64(out var expSeconds) ||
                expSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                error = "Bearer token claims are invalid or expired.";
                return false;
            }

            var roles = new List<string>();
            if (payload.RootElement.TryGetProperty("roles", out var roleArray) &&
                roleArray.ValueKind == JsonValueKind.Array)
            {
                roles.AddRange(roleArray.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal));
            }

            var fullName = payload.RootElement.TryGetProperty("full_name", out var fullNameElement)
                ? fullNameElement.GetString() : null;
            var department = payload.RootElement.TryGetProperty("department", out var departmentElement)
                ? departmentElement.GetString() : null;
            var identity = new ClaimsIdentity("Bearer", ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Name, name.GetString()!));
            if (!string.IsNullOrWhiteSpace(fullName))
                identity.AddClaim(new Claim("full_name", fullName));
            if (!string.IsNullOrWhiteSpace(department))
                identity.AddClaim(new Claim("department", department));
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            principal = new ClaimsPrincipal(identity);
            return true;
        }
        catch (JsonException)
        {
            error = "Bearer token JSON is invalid.";
            return false;
        }
    }

    private byte[] Sign(string input) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(input));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}

public sealed class MiniErpBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly TokenService _tokens;

    public MiniErpBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        TokenService tokens) : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) ||
            !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_tokens.TryValidate(header.ToString()[7..].Trim(), out var principal, out var error))
        {
            return Task.FromResult(AuthenticateResult.Fail(error ?? "Invalid bearer token."));
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal!, Scheme.Name)));
    }
}
