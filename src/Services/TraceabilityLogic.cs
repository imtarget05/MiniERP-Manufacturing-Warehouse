using System.Security.Cryptography;
using System.Text;

namespace MiniERP.Api.Services;

/// <summary>
/// Pure (database-free) helpers for the traceability upgrade: barcode/scan
/// resolution, deterministic idempotency hashing, FEFO/FIFO lot ordering and
/// label rendering. Side-effect free so unit tests cover every branch.
/// </summary>
public static class TraceabilityLogic
{
    private static readonly HashSet<string> LocationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIN", "STAGING", "RECEIVING", "SHIPPING", "LINE"
    };

    private static readonly HashSet<string> LabelEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOT", "ITEM", "LOCATION", "PRODUCTION_ORDER"
    };

    private static readonly HashSet<string> LabelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RAW_MATERIAL", "FINISHED_GOOD", "LOCATION"
    };

    /// <summary>Only ACTIVE lots may be issued to production.</summary>
    public static bool IsIssuableLotStatus(string? status) =>
        string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidLocationType(string? locationType) =>
        !string.IsNullOrWhiteSpace(locationType) && LocationTypes.Contains(locationType.Trim());

    /// <summary>
    /// Resolve a raw scan string into (entityType, entityKey). Accepts the
    /// canonical "PREFIX:key" form (LOT:/ITEM:/LOC:/PO:) and bare codes.
    /// </summary>
    public static (string EntityType, string EntityKey) ResolveScan(string? raw)
    {
        var code = (raw ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            throw new ArgumentException("Scan code must not be empty.", nameof(raw));
        }

        var colon = code.IndexOf(':');
        if (colon > 0)
        {
            var prefix = code[..colon].Trim().ToUpperInvariant();
            var key = code[(colon + 1)..].Trim();
            if (key.Length == 0)
            {
                throw new ArgumentException("Scan code has an empty key.", nameof(raw));
            }

            return prefix switch
            {
                "LOT" => ("LOT", key),
                "ITEM" => ("ITEM", key),
                "LOC" or "LOCATION" => ("LOCATION", key),
                "PO" or "ORDER" => ("PRODUCTION_ORDER", key),
                _ => throw new ArgumentException($"Unknown scan prefix '{prefix}'.", nameof(raw)),
            };
        }

        if (code.StartsWith("WH_", StringComparison.OrdinalIgnoreCase))
        {
            return ("LOCATION", code);
        }

        if (code.StartsWith("PO", StringComparison.OrdinalIgnoreCase))
        {
            return ("PRODUCTION_ORDER", code);
        }

        if (code.Contains('-') || code.StartsWith("LOT", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("RM", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("FG", StringComparison.OrdinalIgnoreCase))
        {
            return ("LOT", code);
        }

        return ("ITEM", code);
    }

    /// <summary>
    /// Deterministic SHA-256 hash of a request payload for idempotency
    /// comparison: same key + same hash = replay; same key + other hash = 409.
    /// </summary>
    public static string HashRequest(string operation, string canonicalPayload)
    {
        var input = (operation ?? string.Empty).Trim().ToUpperInvariant()
            + "|" + (canonicalPayload ?? string.Empty);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public sealed record LotCandidate(
        string LotCode,
        DateTime? ExpiryDate,
        DateTime CreatedAt,
        decimal QtyAvailable);

    /// <summary>
    /// FEFO when expiry exists, otherwise FIFO by creation time.
    /// </summary>
    public static IReadOnlyList<LotCandidate> OrderForIssue(
        IEnumerable<LotCandidate> candidates)
    {
        return candidates
            .OrderBy(c => c.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(c => c.ExpiryDate ?? DateTime.MaxValue)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.LotCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Normalize a trace direction query value (case-insensitive, default
    /// BOTH) or reject it with ArgumentException so the route can return 400.
    /// </summary>
    public static string NormalizeTraceDirection(string? direction)
    {
        var d = (direction ?? string.Empty).Trim().ToUpperInvariant();
        return d switch
        {
            "" => "BOTH",
            "BACKWARD" or "FORWARD" or "BOTH" => d,
            _ => throw new ArgumentException(
                $"Unknown trace direction '{direction}'. Use backward, forward or both.",
                nameof(direction)),
        };
    }

    // ---- label workflow (Phase 4)

    public static bool IsValidLabelEntityType(string? entityType) =>
        !string.IsNullOrWhiteSpace(entityType) &&
        LabelEntityTypes.Contains(entityType.Trim());

    public static bool IsValidLabelType(string? labelType) =>
        !string.IsNullOrWhiteSpace(labelType) &&
        LabelTypes.Contains(labelType.Trim());

    /// <summary>Default HTML; only HTML/ZPL accepted, else ArgumentException.</summary>
    public static string NormalizeLabelFormat(string? format)
    {
        var f = (format ?? string.Empty).Trim().ToUpperInvariant();
        return f switch
        {
            "" => "HTML",
            "HTML" or "ZPL" => f,
            _ => throw new ArgumentException(
                $"Unknown label format '{format}'. Use HTML or ZPL.",
                nameof(format)),
        };
    }
}
