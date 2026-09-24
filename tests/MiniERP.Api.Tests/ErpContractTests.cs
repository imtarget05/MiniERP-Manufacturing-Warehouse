using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Unit tests for the Oracle error -> business code translation table that the
/// API contract relies on (docs/04-plsql-spec.md section 4).
/// These tests run without any database.
/// </summary>
public class ErpErrorMapperTests
{
    [Theory]
    [InlineData(20001, "ERR_INVALID_QTY")]
    [InlineData(20002, "ERR_NOT_FOUND")]
    [InlineData(20003, "ERR_INSUFFICIENT_STOCK")]
    [InlineData(20004, "ERR_PO_ALREADY_COMPLETED")]
    [InlineData(20005, "ERR_PO_CANCELLED")]
    [InlineData(20006, "ERR_NO_ACTIVE_BOM")]
    [InlineData(20007, "ERR_MATERIAL_SHORTAGE")]
    [InlineData(20008, "ERR_PO_STATE_INVALID")]
    [InlineData(20009, "ERR_PURCHASE_ORDER_STATE")]
    [InlineData(20010, "ERR_APPROVAL_REQUIRED")]
    [InlineData(20011, "ERR_AUTOMATION_STATE")]
    [InlineData(20012, "ERR_INVALID_INPUT")]
    [InlineData(20013, "ERR_IDEMPOTENCY_CONFLICT")]
    [InlineData(20014, "ERR_DUPLICATE_REPLAY")]
    public void BusinessCodeFor_KnownOracleNumbers_ReturnsDocumentedCode(int oraNumber, string expected)
    {
        Assert.Equal(expected, ErpErrorMapper.BusinessCodeFor(oraNumber));
    }

    [Fact]
    public void BusinessCodeFor_NegativeOracleNumber_StillMaps()
    {
        // PL/SQL sees RAISE_APPLICATION_ERROR as -20007; both signs must map.
        Assert.Equal("ERR_MATERIAL_SHORTAGE", ErpErrorMapper.BusinessCodeFor(-20007));
    }

    [Fact]
    public void BusinessCodeFor_UnknownNumber_FallsBackToDatabaseError()
    {
        Assert.Equal("ERR_DATABASE", ErpErrorMapper.BusinessCodeFor(99999));
        Assert.False(ErpErrorMapper.IsKnown(99999));
    }

    [Theory]
    [InlineData(20007, "ORA-20007")]
    [InlineData(-20007, "ORA-20007")]
    [InlineData(1017, "ORA-01017")]
    public void OracleCodeFor_IsNormalizedToFiveDigits(int oraNumber, string expected)
    {
        Assert.Equal(expected, ErpErrorMapper.OracleCodeFor(oraNumber));
    }

    [Fact]
    public void ErpBusinessException_CarriesFullErrorContext()
    {
        var ex = new ErpBusinessException("ORA-20007", "ERR_MATERIAL_SHORTAGE",
            "complete_production_order", "material MAT_RUBBER_01 requires 50, available 40", 20007);

        Assert.Equal(20007, ex.OracleErrorNumber);
        Assert.Equal("ERR_MATERIAL_SHORTAGE", ex.BusinessCode);
        Assert.Equal("complete_production_order", ex.ProcedureName);
        Assert.Contains("MAT_RUBBER_01", ex.Message);
    }
}

/// <summary>
/// Unit tests for the HTTP error contract produced by the API layer.
/// </summary>
public class ErpApiPresenterTests
{
    private static ErpBusinessException Shortage() => new(
        "ORA-20007", "ERR_MATERIAL_SHORTAGE", "complete_production_order",
        "Cannot complete PO PO001 due to material shortage");

    [Theory]
    [InlineData("ERR_NOT_FOUND", 404)]
    [InlineData("ERR_INVALID_QTY", 400)]
    [InlineData("ERR_INVALID_INPUT", 400)]
    [InlineData("ERR_APPROVAL_REQUIRED", 403)]
    [InlineData("ERR_INTERNAL", 500)]
    [InlineData("ERR_DATABASE", 500)]
    [InlineData("ERR_MATERIAL_SHORTAGE", 409)]
    [InlineData("ERR_INSUFFICIENT_STOCK", 409)]
    [InlineData("ERR_NO_ACTIVE_BOM", 409)]
    [InlineData("ERR_PO_ALREADY_COMPLETED", 409)]
    [InlineData("ERR_PO_CANCELLED", 409)]
    [InlineData("ERR_PO_STATE_INVALID", 409)]
    [InlineData("ERR_PURCHASE_ORDER_STATE", 409)]
    [InlineData("ERR_AUTOMATION_STATE", 409)]
    public void StatusFor_FollowsDocumentedContract(string businessCode, int expected)
    {
        Assert.Equal(expected, ErpApiPresenter.StatusFor(businessCode));
    }

    [Fact]
    public void BuildPayload_ExposesOracleCodeProcedureAndReference()
    {
        var payload = ErpApiPresenter.BuildPayload(Shortage(), "PO001");

        Assert.False(payload.Success);
        Assert.Equal("ORA-20007", payload.ErrorCode);
        Assert.Equal("ERR_MATERIAL_SHORTAGE", payload.BusinessCode);
        Assert.Equal("complete_production_order", payload.ProcedureName);
        Assert.Equal("PO001", payload.ReferenceNo);
    }

    [Fact]
    public void BuildPayload_MaterialShortage_PointsOperatorToErrorLog()
    {
        var payload = ErpApiPresenter.BuildPayload(Shortage(), "PO001");

        // The runbook hint must tell the support engineer where to look next.
        Assert.Contains("/api/support/errors", payload.Action);
        Assert.Contains("PO001", payload.Action);
    }

    [Fact]
    public void ActionFor_UnknownCode_FallsBackToRunbook()
    {
        Assert.Contains("runbook", ErpApiPresenter.ActionFor("ERR_DATABASE", "X"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActionFor_ApprovalRequired_PointsToTheApprovalEndpoint()
    {
        var action = ErpApiPresenter.ActionFor("ERR_APPROVAL_REQUIRED", "APV-1");
        Assert.Contains("/api/automation/approvals", action);
        Assert.Contains("APV-1", action);
    }

    [Fact]
    public void ActionFor_AutomationState_PointsToTheRunAuditTrail()
    {
        var action = ErpApiPresenter.ActionFor("ERR_AUTOMATION_STATE", "PO-9");
        Assert.Contains("/api/automation/runs", action);
        Assert.Contains("PO-9", action);
    }

    [Fact]
    public void GetIncidentsSql_AliasesAllDapperConstructorColumns()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Services", "ErpDbService.cs"));
        var helpdesk = File.ReadAllText(Path.Combine(root, "src", "Services", "ErpDbService.Helpdesk.cs"));
        Assert.Contains("ERROR_CODE AS ErrorCode", source, StringComparison.Ordinal);
        Assert.Contains("REF_NO AS RefNo", source, StringComparison.Ordinal);
        Assert.Contains("CONTEXT_JSON AS ContextJson", source, StringComparison.Ordinal);
        Assert.Contains("CREATED_BY AS CreatedBy", source, StringComparison.Ordinal);
        Assert.Contains("CREATED_AT AS CreatedAt", source, StringComparison.Ordinal);
        Assert.Contains("ATTEMPT_COUNT AS AttemptCount", helpdesk, StringComparison.Ordinal);
        Assert.Contains("HELPDESK_DELIVERY", helpdesk, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN :Status = 'SENT' THEN SYSDATE ELSE NULL END", helpdesk,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Program.cs")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
