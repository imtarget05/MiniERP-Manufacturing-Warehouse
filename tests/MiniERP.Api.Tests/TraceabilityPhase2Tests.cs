using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Phase 2 unit tests (no database): idempotency mapping, error-code
/// contract for the new traceability codes, and the location/lot DTO flow.
/// </summary>
public class TraceabilityPhase2Tests
{
    [Theory]
    [InlineData(20013, "ERR_IDEMPOTENCY_CONFLICT")]
    [InlineData(-20013, "ERR_IDEMPOTENCY_CONFLICT")]
    [InlineData(20014, "ERR_DUPLICATE_REPLAY")]
    [InlineData(-20014, "ERR_DUPLICATE_REPLAY")]
    public void BusinessCodeFor_TraceabilityCodes_Map(int ora, string expected)
    {
        Assert.Equal(expected, ErpErrorMapper.BusinessCodeFor(ora));
    }

    [Fact]
    public void StatusFor_IdempotencyConflict_Is409()
    {
        Assert.Equal(409, ErpApiPresenter.StatusFor("ERR_IDEMPOTENCY_CONFLICT"));
    }

    [Fact]
    public void StatusFor_DuplicateReplay_Is200()
    {
        Assert.Equal(200, ErpApiPresenter.StatusFor("ERR_DUPLICATE_REPLAY"));
    }

    [Fact]
    public void IsReplay_OnlyMatchesReplaySentinel()
    {
        var replay = new ErpBusinessException(
            "ORA-20014", "ERR_DUPLICATE_REPLAY", "receive_lot_stock", "DUPLICATE_REPLAY", 20014);
        var conflict = new ErpBusinessException(
            "ORA-20013", "ERR_IDEMPOTENCY_CONFLICT", "receive_lot_stock", "conflict", 20013);
        Assert.True(ErpDbService.IsReplay(replay));
        Assert.False(ErpDbService.IsReplay(conflict));
    }

    [Fact]
    public void IdempotencyHash_SamePayload_SameHash()
    {
        var a = ErpDbService.IdempotencyHash("RECEIVE_LOT", new { lot = "L1", qty = 10 });
        var b = ErpDbService.IdempotencyHash("RECEIVE_LOT", new { lot = "L1", qty = 10 });
        var c = ErpDbService.IdempotencyHash("RECEIVE_LOT", new { lot = "L1", qty = 11 });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Presenter_ConflictPointsToRunbook()
    {
        var ex = new ErpBusinessException("ORA-20013", "ERR_IDEMPOTENCY_CONFLICT",
            "receive_lot_stock", "key reused", 20013);
        var payload = ErpApiPresenter.BuildPayload(ex, "PO1");
        Assert.Equal("ERR_IDEMPOTENCY_CONFLICT", payload.BusinessCode);
        Assert.Contains("runbook", payload.Action, StringComparison.OrdinalIgnoreCase);
    }
}
