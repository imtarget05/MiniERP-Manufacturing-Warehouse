using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MiniERP.Api.Services;

public sealed record RefreshTokenData(
    string Token,
    int UserId,
    string Username,
    string? FullName,
    string? Department,
    IReadOnlyList<string> Roles,
    DateTimeOffset ExpiresAtUtc,
    bool IsRevoked = false
);

public sealed class RefreshTokenStore
{
    private readonly ConcurrentDictionary<string, RefreshTokenData> _tokens = new(StringComparer.Ordinal);

    public string CreateToken(AuthenticatedUser user, int expiryDays = 7)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(48);
        var token = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var data = new RefreshTokenData(
            token,
            user.Id,
            user.Username,
            user.FullName,
            user.Department,
            user.Roles,
            DateTimeOffset.UtcNow.AddDays(expiryDays),
            false
        );
        _tokens[token] = data;
        return token;
    }

    public RefreshTokenData? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!_tokens.TryGetValue(token, out var data)) return null;
        if (data.IsRevoked) return null;
        if (data.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;
        return data;
    }

    public bool Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_tokens.TryGetValue(token, out var data)) return false;
        _tokens[token] = data with { IsRevoked = true };
        return true;
    }

    public int RevokeAllForUser(string username)
    {
        var count = 0;
        foreach (var kvp in _tokens)
        {
            if (string.Equals(kvp.Value.Username, username, StringComparison.OrdinalIgnoreCase) && !kvp.Value.IsRevoked)
            {
                _tokens[kvp.Key] = kvp.Value with { IsRevoked = true };
                count++;
            }
        }
        return count;
    }
}
