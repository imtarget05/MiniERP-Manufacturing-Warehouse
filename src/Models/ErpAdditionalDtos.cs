namespace MiniERP.Api.Models;

// ------------------------------------------------------------------ responses

public record ProductionOrderDto(
    string ProductionOrderNo,
    string FinishedGoodCode,
    decimal PlannedQuantity,
    decimal DoneQuantity,
    string Status,
    string WarehouseCode,
    DateTime CreatedAt,
    DateTime? CompletedAt
);

public record ChangeRequestDto(
    string ChangeRequestNo,
    string Title,
    string RequestType,
    string? ReferenceNo,
    string RootCause,
    string FixAction,
    string Status,
    string? Requester,
    DateTime CreatedAt,
    DateTime? ClosedAt
);

public record CreateChangeRequestResponse(
    string ChangeRequestNo,
    string Status
);

public record DbHealthDto(
    string? Banner,
    int TableCount,
    string? PackageStatus
);

/// <summary>
/// Uniform error payload for every failed business operation. It mirrors the
/// response contract documented in docs/03-api-spec.md (success=false +
/// errorCode + actionable hint) and is produced from ErpBusinessException.
/// </summary>
public record ErpErrorResponse(
    bool Success,
    string ErrorCode,
    string BusinessCode,
    string ProcedureName,
    string Message,
    string? ReferenceNo,
    string Action
);

// ------------------------------------------------------------------- requests

public record CreatePurchaseOrderRequest(
    string PurchaseOrderNo,
    string ItemCode,
    decimal Quantity,
    string WarehouseCode
);

public record ReceivePurchaseOrderRequest(
    string User = "system"
);

// =============================================================== automation

/// <summary>One open/closed replenishment alert (W1/W3 output).</summary>
public record ReplenishAlertDto(
    int Id,
    string ItemCode,
    string WarehouseCode,
    decimal QuantityAvailable,
    decimal QuantitySuggested,
    string TriggerType,
    string? RefNo,
    string Status,
    string TriggeredBy,
    DateTime CreatedAt,
    DateTime? ClosedAt
);

/// <summary>A production order with no state change for more than N days (W5).</summary>
public record StaleOrderDto(
    string ProductionOrderNo,
    string FinishedGoodCode,
    decimal PlannedQuantity,
    string Status,
    DateTime CreatedAt,
    DateTime LastChangeAt,
    int DaysIdle
);

/// <summary>One ERP_AUTOMATION_RUN audit row (guarantee: every run is traced).</summary>
public record AutomationRunDto(
    int Id,
    string WorkflowName,
    string TriggerType,
    string? TriggerId,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int RetryCount,
    string? ErrorCode,
    string? ErrorMessage,
    string? ResultSummary,
    string RunBy
);

/// <summary>A persisted scheduled report snapshot (W6).</summary>
public record AutomationReportDto(
    int Id,
    string ReportType,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    int LineCount,
    string? PayloadJson,
    string GeneratedBy,
    DateTime GeneratedAt
);

/// <summary>A support incident with collected context + rule diagnosis (W7).</summary>
public record SupportIncidentDto(
    int Id,
    string ErrorCode,
    string? RefNo,
    string Title,
    string? ContextJson,
    string? Diagnosis,
    string Status,
    string CreatedBy,
    DateTime CreatedAt
);

/// <summary>An approval token guarding risky automation overrides.</summary>
public record ApprovalDto(
    int Id,
    string ApprovalNo,
    string Action,
    string? RefNo,
    string? Payload,
    string Status,
    string? Requester,
    string? Approver,
    DateTime? DecidedAt,
    DateTime CreatedAt
);

/// <summary>Result of the W1 material availability check (ERP_AUTOMATION.check_material_availability).</summary>
public record MaterialCheckResponse(
    bool Success,
    string ProductionOrderNo,
    string Status,
    string Message
);

/// <summary>W3 replenishment evaluation of one (item, warehouse) line.</summary>
public record ReplenishmentEvalResponse(
    bool Success,
    string ItemCode,
    string WarehouseCode,
    decimal SuggestedQty,
    string Message
);

public record ReplenishmentSweepResponse(
    bool Success,
    int OpenAlerts,
    string Message
);

/// <summary>W7 incident context snapshot collected by ERP_AUTOMATION.collect_incident_context.</summary>
public record IncidentContextResponse(
    bool Success,
    long IncidentId,
    string ErrorCode,
    string ReferenceNo,
    string Status,
    string Diagnosis,
    string? ContextJson
);

public record ApprovalResponse(
    bool Success,
    string ApprovalNo,
    string Action,
    string Status,
    string Message
);

// ---------------------------------------------------------------- requests

public record ReplenishmentEvalRequest(
    string ItemCode,
    string WarehouseCode,
    string Trigger = "LOW_STOCK",
    string User = "system"
);

public record CollectIncidentRequest(
    string ReferenceNo,
    string? ErrorCode = "ERR_MATERIAL_SHORTAGE",
    string? Title = null,
    string User = "system"
);

public record CreateApprovalRequest(
    string ApprovalNo,
    string Action,
    string? ReferenceNo = null,
    string? Payload = null,
    string Requester = "system"
);

public record DecideApprovalRequest(
    string Decision,
    string Approver = "system"
);

public record AdjustStockRequest(
    string WarehouseCode,
    string ItemCode,
    decimal QuantityDelta,
    string ApprovalNo,
    string User = "system"
);
