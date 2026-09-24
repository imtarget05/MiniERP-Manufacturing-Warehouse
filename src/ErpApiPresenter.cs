using MiniERP.Api.Models;
using MiniERP.Api.Services;

namespace MiniERP.Api;

/// <summary>
/// Translates an <see cref="ErpBusinessException"/> raised by package
/// ERP_OPERATIONS into the public API error contract (HTTP status + JSON body)
/// documented in docs/03-api-spec.md. Kept as pure functions so it can be unit
/// tested without a database.
/// </summary>
public static class ErpApiPresenter
{
    /// <summary>HTTP status a business error must be reported with.</summary>
    public static int StatusFor(string businessCode) => businessCode switch
    {
        "ERR_NOT_FOUND" => StatusCodes.Status404NotFound,
        "ERR_INVALID_QTY" or "ERR_INVALID_INPUT" => StatusCodes.Status400BadRequest,
        "ERR_APPROVAL_REQUIRED" => StatusCodes.Status403Forbidden,
        "ERR_IDEMPOTENCY_CONFLICT" => StatusCodes.Status409Conflict,
        "ERR_DUPLICATE_REPLAY" => StatusCodes.Status200OK,
        "ERR_INTERNAL" or "ERR_DATABASE" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status409Conflict,
    };

    /// <summary>Runbook hint an operator / integrator should follow next.</summary>
    public static string ActionFor(string businessCode, string? referenceNo) => businessCode switch
    {
        "ERR_MATERIAL_SHORTAGE" => $"Consult GET /api/support/errors?refNo={referenceNo} then post a stock-in or receive the purchase order.",
        "ERR_INSUFFICIENT_STOCK" => "Post a stock-in document or run a physical count before retrying.",
        "ERR_NO_ACTIVE_BOM" => "Ask Engineering to release an ACTIVE BOM for this finished good.",
        "ERR_PO_ALREADY_COMPLETED" => "Order is closed; create a new production order instead.",
        "ERR_PO_CANCELLED" => "Order is cancelled; raise a new production order.",
        "ERR_PO_STATE_INVALID" => "Verify PO_NO and its current status before cancelling.",
        "ERR_PURCHASE_ORDER_STATE" => "Purchase order is not in CREATED state; nothing to receive.",
        "ERR_NOT_FOUND" => "Check the WAREHOUSE / ITEM master data catalogs.",
        "ERR_INVALID_QTY" => "Quantity must be greater than zero.",
        "ERR_INVALID_INPUT" => "Validate the automation parameter (report type, trigger, decision) against docs/03-api-spec.md.",
        "ERR_APPROVAL_REQUIRED" => $"Request an approval token (POST /api/automation/approvals), get it APPROVED, then retry {referenceNo}.",
        "ERR_AUTOMATION_STATE" => $"Check GET /api/automation/runs?triggerId={referenceNo} for the last workflow state before retrying.",
        _ => "Consult docs/05-erp-support-runbook.md and open a Change Request.",
    };

    public static ErpErrorResponse BuildPayload(ErpBusinessException ex, string? referenceNo) => new(
        Success: false,
        ErrorCode: ex.OracleErrorCode,
        BusinessCode: ex.BusinessCode,
        ProcedureName: ex.ProcedureName,
        Message: ex.Message,
        ReferenceNo: referenceNo,
        Action: ActionFor(ex.BusinessCode, referenceNo));

    public static IResult Problem(ErpBusinessException ex, string? referenceNo = null)
    {
        var payload = BuildPayload(ex, referenceNo);
        return Results.Json(payload, statusCode: StatusFor(ex.BusinessCode));
    }
}
