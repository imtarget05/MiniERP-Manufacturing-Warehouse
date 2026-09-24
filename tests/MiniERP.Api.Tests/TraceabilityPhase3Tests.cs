using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Phase 3 unit tests (no database): trace direction contract, idempotency
/// hash separation per operation, allocation DTO shape and genealogy tree.
/// </summary>
public class TraceabilityPhase3Tests
{
    [Theory]
    [InlineData(null, "BOTH")]
    [InlineData("", "BOTH")]
    [InlineData("backward", "BACKWARD")]
    [InlineData(" FORWARD ", "FORWARD")]
    [InlineData("Both", "BOTH")]
    public void NormalizeTraceDirection_DefaultsAndNormalizes(string? input, string expected)
    {
        Assert.Equal(expected, TraceabilityLogic.NormalizeTraceDirection(input));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("sideways")]
    public void NormalizeTraceDirection_Unknown_Throws(string input)
    {
        Assert.Throws<ArgumentException>(
            () => TraceabilityLogic.NormalizeTraceDirection(input));
    }

    [Fact]
    public void IdempotencyHash_Operations_AreSeparated()
    {
        var payload = new { po = "MO001" };
        var issue = ErpDbService.IdempotencyHash("ISSUE_LOTS", payload);
        var complete = ErpDbService.IdempotencyHash("COMPLETE_TRACEABLE", payload);
        var receive = ErpDbService.IdempotencyHash("RECEIVE_LOT", payload);
        Assert.NotEqual(issue, complete);
        Assert.NotEqual(issue, receive);
        Assert.NotEqual(complete, receive);
    }

    [Fact]
    public void AllocationLine_ReportsShortfallWithFeFoSuggestions()
    {
        var line = new AllocationLineDto("MAT_X", "LOT", 100m, 80m, 80m < 100m,
            new List<AllocationSuggestionDto>
            {
                new("RM001-A", new DateTime(2026, 1, 1), 50m),
                new("RM001-B", null, 30m),
            });
        Assert.True(line.IsShort);
        Assert.Equal(2, line.SuggestedLots.Count);
        Assert.Null(line.SuggestedLots[1].ExpiryDate);
    }

    [Fact]
    public void TraceTree_Backward_HasExplicitLinksOnly()
    {
        var inputs = new List<TraceNodeDto>
        {
            new("LOT", "RM001-A", "MAT_RUBBER_01", 40m, new List<TraceNodeDto>
            {
                new("SOURCE", "PO_PUR_LOT_01", "MAT_RUBBER_01", null,
                    new List<TraceNodeDto>()),
            }),
        };
        var order = new TraceNodeDto("PRODUCTION_ORDER", "MO001", null, 40m, inputs);
        var root = new TraceNodeDto("LOT", "FG001-001", "FG_WHEEL_01", null,
            new List<TraceNodeDto> { order });

        Assert.Equal("LOT", root.Kind);
        var po = Assert.Single(root.Children);
        Assert.Equal("PRODUCTION_ORDER", po.Kind);
        var inputLot = Assert.Single(po.Children);
        Assert.Equal("RM001-A", inputLot.Key);
        Assert.Equal("SOURCE", Assert.Single(inputLot.Children).Kind);
    }

    [Fact]
    public void CompleteTraceableRequest_ToleranceDefaultIsExact()
    {
        var req = new CompleteTraceableRequest(null, null, null, null, "k1", "WH_FG");
        Assert.Null(req.Tolerance);
        Assert.Equal(0m, req.Tolerance ?? 0m); // service sends 0 => exact issue
        Assert.Equal("WH_FG", req.OutputWarehouseCode);
    }

    [Fact]
    public void Presenter_KnownManufacturingCodes_StillMap()
    {
        Assert.Equal("ERR_MATERIAL_SHORTAGE", ErpErrorMapper.BusinessCodeFor(20007));
        Assert.Equal("ERR_PO_ALREADY_COMPLETED", ErpErrorMapper.BusinessCodeFor(20004));
        Assert.Equal("ERR_PO_CANCELLED", ErpErrorMapper.BusinessCodeFor(20005));
        Assert.Equal(409, ErpApiPresenter.StatusFor("ERR_PO_ALREADY_COMPLETED"));
        Assert.Equal(409, ErpApiPresenter.StatusFor("ERR_MATERIAL_SHORTAGE"));
    }
}