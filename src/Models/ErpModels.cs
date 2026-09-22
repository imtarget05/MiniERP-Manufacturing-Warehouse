namespace MiniERP.Api.Models;

public record WarehouseDto(
    int Id,
    string Code,
    string Name,
    string? Location,
    bool IsActive
);

public record StockItemDto(
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string ItemType,
    string Uom,
    decimal Quantity,
    decimal MinStock,
    bool IsBelowMinStock
);

public record StockInRequest(
    string WarehouseCode,
    string ItemCode,
    decimal Quantity,
    string ReferenceNo,
    string User = "system"
);

public record StockOutRequest(
    string WarehouseCode,
    string ItemCode,
    decimal Quantity,
    string ReferenceNo,
    string User = "system"
);

public record BomLineRequest(
    string FinishedGoodCode,
    string Version,
    string MaterialCode,
    decimal QuantityRequired,
    string User = "system"
);

public record CreateProductionOrderRequest(
    string ProductionOrderNo,
    string FinishedGoodCode,
    decimal PlannedQuantity,
    string WarehouseCode,
    string User = "system"
);

public record CompleteProductionOrderResponse(
    bool Success,
    string ProductionOrderNo,
    string Status,
    string Message
);

public record ErrorLogDto(
    int Id,
    string ErrorCode,
    string Message,
    string? ProcedureName,
    string? ReferenceNo,
    string CreatedBy,
    DateTime CreatedAt
);

public record CreateChangeRequest(
    string ChangeRequestNo,
    string Title,
    string RequestType,
    string? ReferenceNo,
    string RootCause,
    string FixAction,
    string Requester
);
