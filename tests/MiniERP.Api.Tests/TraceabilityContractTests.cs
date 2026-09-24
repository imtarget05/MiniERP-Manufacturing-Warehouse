using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Phase 1 unit tests (no database): scan resolution, idempotency hashing,
/// FEFO/FIFO ordering, lot-issue gating, label rendering, PBKDF2 hashing.
/// </summary>
public class TraceabilityLogicTests
{
    [Theory]
    [InlineData("LOT:RM001-001", "LOT", "RM001-001")]
    [InlineData("ITEM:MAT_RUBBER_01", "ITEM", "MAT_RUBBER_01")]
    [InlineData("LOC:RCV-01", "LOCATION", "RCV-01")]
    [InlineData("LOCATION:A-01-02", "LOCATION", "A-01-02")]
    [InlineData("PO:PO001", "PRODUCTION_ORDER", "PO001")]
    [InlineData("RM001-20260924-001", "LOT", "RM001-20260924-001")]
    [InlineData("WH_RAW", "LOCATION", "WH_RAW")]
    [InlineData("PO001", "PRODUCTION_ORDER", "PO001")]
    [InlineData("MAT_RUBBER_01", "ITEM", "MAT_RUBBER_01")]
    public void ResolveScan_MapsToEntity(string raw, string type, string key)
    {
        var (entityType, entityKey) = TraceabilityLogic.ResolveScan(raw);
        Assert.Equal(type, entityType);
        Assert.Equal(key, entityKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LOT:")]
    [InlineData("BADPREFIX:X")]
    public void ResolveScan_Invalid_Throws(string raw)
    {
        Assert.Throws<ArgumentException>(() => TraceabilityLogic.ResolveScan(raw));
    }

    [Fact]
    public void HashRequest_IsDeterministicAndSensitive()
    {
        var a = TraceabilityLogic.HashRequest("RECEIVE", "{\"qty\":100}");
        var b = TraceabilityLogic.HashRequest("receive", "{\"qty\":100}");
        var c = TraceabilityLogic.HashRequest("RECEIVE", "{\"qty\":101}");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void OrderForIssue_PrefersEarliestExpiryThenFifo()
    {
        var now = new DateTime(2026, 9, 24);
        var list = new[]
        {
            new TraceabilityLogic.LotCandidate("L-NOEXP", null, now.AddDays(-9), 10m),
            new TraceabilityLogic.LotCandidate("L-LATE", now.AddMonths(6), now.AddDays(-5), 10m),
            new TraceabilityLogic.LotCandidate("L-EARLY", now.AddMonths(1), now.AddDays(-1), 10m),
        };
        var ordered = TraceabilityLogic.OrderForIssue(list).Select(c => c.LotCode).ToList();
        Assert.Equal(new[] { "L-EARLY", "L-LATE", "L-NOEXP" }, ordered);
    }

    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("active", true)]
    [InlineData("HOLD", false)]
    [InlineData("BLOCKED", false)]
    [InlineData("EXPIRED", false)]
    [InlineData("CONSUMED", false)]
    [InlineData(null, false)]
    public void IsIssuableLotStatus_GatesIssue(string? status, bool expected)
    {
        Assert.Equal(expected, TraceabilityLogic.IsIssuableLotStatus(status));
    }

    [Theory]
    [InlineData("BIN", true)]
    [InlineData("receiving", true)]
    [InlineData("SHELF", false)]
    [InlineData(null, false)]
    public void IsValidLocationType_Validates(string? type, bool expected)
    {
        Assert.Equal(expected, TraceabilityLogic.IsValidLocationType(type));
    }
}

/// <summary>Label rendering contract (HTML + ZPL preview).</summary>
public class LabelRenderServiceTests
{
    [Fact]
    public void RenderHtml_ContainsLotQtyAndHumanReadableCode()
    {
        var html = LabelRenderService.RenderHtml("MiniERP Demo",
            "RM001", "Rubber sole", "RM001-001", 100m, "PAIR", "RCV-01",
            new DateTime(2026, 9, 20), new DateTime(2027, 9, 20));
        Assert.Contains("RM001-001", html);
        Assert.Contains("100", html);
        Assert.Contains("*RM001-001*", html);
    }

    [Fact]
    public void RenderHtml_EscapesMarkup()
    {
        var html = LabelRenderService.RenderHtml("<b>co</b>",
            "I", "<script>", "L1", 1m, "PCS", null, null, null);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void RenderZpl_ContainsBarcodeBlock()
    {
        var zpl = LabelRenderService.RenderZpl("RM001", "RM001-001", 100m, "PAIR");
        Assert.StartsWith("^XA", zpl);
        Assert.Contains("^BCN", zpl);
        Assert.Contains("RM001-001", zpl);
        Assert.EndsWith("^XZ", zpl.TrimEnd());
    }
}

/// <summary>PBKDF2 credential handling (never plaintext).</summary>
public class PasswordHashServiceTests
{
    [Fact]
    public void Hash_ThenVerify_RoundTrips()
    {
        var stored = PasswordHashService.Hash("correct-horse-01", 1000);
        Assert.StartsWith("pbkdf2$", stored);
        Assert.DoesNotContain("correct-horse-01", stored);
        Assert.True(PasswordHashService.Verify("correct-horse-01", stored));
        Assert.False(PasswordHashService.Verify("wrong", stored));
    }

    [Fact]
    public void Verify_Malformed_ReturnsFalse()
    {
        Assert.False(PasswordHashService.Verify("x", null));
        Assert.False(PasswordHashService.Verify("x", "not-a-hash"));
        Assert.False(PasswordHashService.Verify("", PasswordHashService.Hash("y", 1000)));
    }
}
