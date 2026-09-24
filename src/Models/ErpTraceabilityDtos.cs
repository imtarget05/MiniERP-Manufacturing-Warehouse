namespace MiniERP.Api.Models;

// ------------------------------------------------------- traceability (Phase 1)

/// <summary>Warehouse bin/staging/receiving/shipping/line position.</summary>
public record WarehouseLocationDto(
    int Id,
    string WarehouseCode,
    string LocationCode,
    string? LocationName,
    string LocationType,
    bool IsActive
);

public record CreateLocationRequest(
    string LocationCode,
    string? LocationName,
    string LocationType
);

/// <summary>Master record for one batch/lot (exactly one item).</summary>
public record InventoryLotDto(
    string LotCode,
    string ItemCode,
    string? SupplierLotNo,
    string SourceType,
    string? SourceRefNo,
    DateTime? MfgDate,
    DateTime? ExpiryDate,
    string Status
);

public record LotStockDto(
    string LotCode,
    string WarehouseCode,
    string LocationCode,
    decimal QtyOnHand,
    decimal QtyReserved,
    decimal QtyAvailable
);

public record ReceiveLotRequest(
    string ItemCode,
    decimal Qty,
    string LotCode,
    string? SupplierLotNo,
    DateTime? MfgDate,
    DateTime? ExpiryDate,
    string ReceivingLocationCode,
    string IdempotencyKey
);

public record MoveLotRequest(
    string LotCode,
    string FromWarehouseCode,
    string FromLocationCode,
    string ToWarehouseCode,
    string ToLocationCode,
    decimal Qty,
    string IdempotencyKey
);

public record PutawayLotRequest(
    string LotCode,
    string FromLocationCode,
    string ToLocationCode,
    decimal Qty,
    string IdempotencyKey
);

public record BarcodeResolveResponse(
    string Code,
    string EntityType,
    string EntityKey,
    string? ItemCode,
    string Status
);

public record LabelJobDto(
    long PrintJobId,
    string LabelType,
    string EntityType,
    string EntityKey,
    string TemplateCode,
    int Copies,
    string Format,
    string Status
);

/// <summary>Backward/forward genealogy tree (explicit links only).</summary>
public record TraceNodeDto(
    string Kind,
    string Key,
    string? ItemCode,
    decimal? Qty,
    IReadOnlyList<TraceNodeDto> Children
);

// ------------------------------------------- genealogy (Phase 3)

/// <summary>One FEFO/FIFO candidate lot offered for an issue line.</summary>
public record AllocationSuggestionDto(
    string LotCode,
    DateTime? ExpiryDate,
    decimal QtyAvailable
);

/// <summary>FEFO allocation line: what the order needs vs what is issuable.</summary>
public record AllocationLineDto(
    string ItemCode,
    string TraceMode,
    decimal QtyRequired,
    decimal QtyAvailableIssuable,
    bool IsShort,
    IReadOnlyList<AllocationSuggestionDto> SuggestedLots
);

public record AllocationPreviewResponse(
    string ProductionOrderNo,
    IReadOnlyList<AllocationLineDto> Lines
);

/// <summary>Issue all LOT-tracked BOM lines of a production order.</summary>
public record IssueLotsRequest(string IdempotencyKey);

/// <summary>Traceable completion: creates/validates the FG lot, closes the PO.</summary>
public record CompleteTraceableRequest(
    string? FgLot,
    decimal? Qty,
    string? LocationCode,
    decimal? Tolerance,
    string IdempotencyKey,
    string? OutputWarehouseCode = null
);

// ----------------------------------------------- label workflow (Phase 4)

/// <summary>
/// Create a label print job. A reprint posts the same payload again: it
/// appends one LABEL_PRINT_JOB audit row and never creates lot/stock rows.
/// </summary>
public record CreateLabelRequest(
    string EntityType,
    string EntityKey,
    string LabelType,
    int Copies,
    string Format
);

/// <summary>Print job + deterministic rendered payload (HTML or ZPL text).</summary>
public record LabelPrintResponse(
    LabelJobDto Job,
    string Rendered
);
