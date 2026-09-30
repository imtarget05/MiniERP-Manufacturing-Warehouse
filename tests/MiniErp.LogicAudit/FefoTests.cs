using MiniERP.Api.Services;
using Xunit;

namespace MiniErpAudit;

public class FefoTests
{
    private static TraceabilityLogic.LotCandidate Lot(
        string code, DateTime? expiry, DateTime created, decimal qty = 10)
        => new(code, expiry, created, qty);

    private static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void Fefo_PicksEarliestExpiryFirst()
    {
        var lots = new[]
        {
            Lot("C", new DateTime(2027, 1, 1), T0),
            Lot("A", new DateTime(2026, 3, 1), T0),
            Lot("B", new DateTime(2026, 6, 1), T0),
        };
        var ordered = TraceabilityLogic.OrderForIssue(lots);
        Assert.Equal(new[] { "A", "B", "C" }, ordered.Select(l => l.LotCode));
    }

    [Fact]
    public void LotsWithoutExpiry_AreIssuedAfterEveryExpiringLot()
    {
        // The rule is FEFO-when-expiry-exists, FIFO otherwise. A non-expiring
        // lot with an old creation date must still come LAST, otherwise an
        // unlimited-shelf-life batch would be issued ahead of stock that is
        // about to expire, which is the whole point of FEFO.
        var lots = new[]
        {
            Lot("NOEXP-OLD", null, T0.AddYears(-5)),
            Lot("EXPIRING-SOON", T0.AddDays(1), T0),
        };
        var ordered = TraceabilityLogic.OrderForIssue(lots);
        Assert.Equal("EXPIRING-SOON", ordered[0].LotCode);
        Assert.Equal("NOEXP-OLD", ordered[1].LotCode);
    }

    [Fact]
    public void NoneExpiring_AreOrderedByCreationTimeThenLotCode()
    {
        var lots = new[]
        {
            Lot("B", null, T0.AddDays(2)),
            Lot("A", null, T0.AddDays(1)),
            Lot("C", null, T0.AddDays(1)),
        };
        var ordered = TraceabilityLogic.OrderForIssue(lots);
        // Same creation time for A and C: the lot code breaks the tie, and it
        // must break it deterministically or two hosts issue different lots.
        Assert.Equal(new[] { "A", "C", "B" }, ordered.Select(l => l.LotCode));
    }

    [Fact]
    public void OrderingIsStable_RegardlessOfInputOrder()
    {
        // Same lots, shuffled input, identical output. Without the full
        // tie-break chain, LINQ's OrderBy is stable but the RESULT still
        // depends on input order when keys tie.
        var lots = new[]
        {
            Lot("L3", new DateTime(2026, 5, 1), T0.AddDays(3)),
            Lot("L1", new DateTime(2026, 5, 1), T0.AddDays(1)),
            Lot("L2", new DateTime(2026, 5, 1), T0.AddDays(2)),
        };
        var forward = TraceabilityLogic.OrderForIssue(lots).Select(l => l.LotCode);
        var reversed = TraceabilityLogic.OrderForIssue(lots.Reverse()).Select(l => l.LotCode);
        Assert.Equal(forward, reversed);
        Assert.Equal(new[] { "L1", "L2", "L3" }, forward);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty_NotNull()
    {
        var ordered = TraceabilityLogic.OrderForIssue(
            Array.Empty<TraceabilityLogic.LotCandidate>());
        Assert.NotNull(ordered);
        Assert.Empty(ordered);
    }

    [Fact]
    public void QuantityIsNotAnOrderingCriterion()
    {
        // FEFO must not be influenced by how much stock a lot holds. If a
        // caller sorted by qty the "earliest expiry first" rule would silently
        // become "smallest lot first", and a partial issue would take the
        // wrong batch.
        var lots = new[]
        {
            Lot("BIG-SOON", T0.AddDays(2), T0, qty: 9999),
            Lot("SMALL-LATER", T0.AddDays(90), T0, qty: 1),
        };
        var ordered = TraceabilityLogic.OrderForIssue(lots);
        Assert.Equal("BIG-SOON", ordered[0].LotCode);
    }
}

public class IdempotencyHashTests
{
    // Idempotency contract: same key + same payload = replay (return the first
    // result); same key + different payload = 409 conflict. HashRequest is the
    // only thing standing between those two outcomes, so the properties that
    // matter are determinism, case-insensitivity of the operation, and the
    // fact that a changed payload must actually change the hash.

    [Fact]
    public void HashIsDeterministic()
    {
        var a = TraceabilityLogic.HashRequest("ISSUE", "{\"lot\":\"A\"}");
        var b = TraceabilityLogic.HashRequest("ISSUE", "{\"lot\":\"A\"}");
        Assert.Equal(a, b);
    }

    [Fact]
    public void OperationIsCaseAndWhitespaceInsensitive()
    {
        var a = TraceabilityLogic.HashRequest("issue", "payload");
        var b = TraceabilityLogic.HashRequest("  ISSUE  ", "payload");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPayload_ProducesDifferentHash()
    {
        var a = TraceabilityLogic.HashRequest("ISSUE", "{\"lot\":\"A\"}");
        var b = TraceabilityLogic.HashRequest("ISSUE", "{\"lot\":\"B\"}");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentOperation_ProducesDifferentHash()
    {
        var a = TraceabilityLogic.HashRequest("ISSUE", "{\"lot\":\"A\"}");
        var b = TraceabilityLogic.HashRequest("RECEIPT", "{\"lot\":\"A\"}");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NullInputs_DoNotThrow()
    {
        // A null operation or payload must not become a 500 on the write path.
        var h = TraceabilityLogic.HashRequest(null!, null!);
        Assert.False(string.IsNullOrWhiteSpace(h));
    }

    [Fact]
    public void FieldBoundary_IsNotCollapsible()
    {
        // The hash input is operation + "|" + payload. If the separator were
        // absent, ("AB","C") and ("A","BC") would hash identically, and two
        // different requests would be treated as the same replay.
        var a = TraceabilityLogic.HashRequest("AB", "C");
        var b = TraceabilityLogic.HashRequest("A", "BC");
        Assert.NotEqual(a, b);
    }
}

public class TraceDirectionTests
{
    [Theory]
    [InlineData(null, "BOTH")]
    [InlineData("", "BOTH")]
    [InlineData("  ", "BOTH")]
    [InlineData("backward", "BACKWARD")]
    [InlineData("FORWARD", "FORWARD")]
    [InlineData("Both", "BOTH")]
    public void ValidDirections_Normalize(string? input, string expected)
        => Assert.Equal(expected, TraceabilityLogic.NormalizeTraceDirection(input));

    [Theory]
    [InlineData("sideways")]
    [InlineData("BACK")]
    [InlineData("BOTHISH")]
    public void UnknownDirection_Throws_SoTheRouteCanReturn400(string input)
    {
        // Must throw, not silently default. Defaulting an unknown direction to
        // BOTH would quietly widen the trace and mask a client bug.
        Assert.Throws<ArgumentException>(
            () => TraceabilityLogic.NormalizeTraceDirection(input));
    }
}
