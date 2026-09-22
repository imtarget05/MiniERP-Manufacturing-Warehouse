using System.Data;
using Dapper;
using MiniERP.Api.Models;
using Oracle.ManagedDataAccess.Client;

namespace MiniERP.Api.Services;

public class ErpDbService
{
    private readonly string _connectionString;
    private readonly ILogger<ErpDbService> _logger;

    public ErpDbService(IConfiguration configuration, ILogger<ErpDbService> logger)
    {
        _connectionString = configuration.GetConnectionString("OracleDb")
            ?? throw new InvalidOperationException("Connection string 'OracleDb' not configured.");
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new OracleConnection(_connectionString);

    public async Task<IEnumerable<WarehouseDto>> GetWarehousesAsync()
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT ID, CODE, NAME, LOCATION, (CASE WHEN IS_ACTIVE = 1 THEN 1 ELSE 0 END) AS IsActive
            FROM WAREHOUSE
            ORDER BY CODE";
        return await conn.QueryAsync<WarehouseDto>(sql);
    }

    public async Task<IEnumerable<StockItemDto>> GetStockAsync(string warehouseCode)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT 
                w.CODE AS WarehouseCode,
                i.CODE AS ItemCode,
                i.NAME AS ItemName,
                i.ITEM_TYPE AS ItemType,
                i.UOM AS Uom,
                s.QTY AS Quantity,
                i.MIN_STOCK AS MinStock,
                (CASE WHEN s.QTY < i.MIN_STOCK THEN 1 ELSE 0 END) AS IsBelowMinStock
            FROM STOCK s
            JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
            JOIN ITEM i ON s.ITEM_ID = i.ID
            WHERE w.CODE = :WarehouseCode
            ORDER BY i.ITEM_TYPE, i.CODE";
        return await conn.QueryAsync<StockItemDto>(sql, new { WarehouseCode = warehouseCode });
    }

    public async Task ExecuteStockInAsync(StockInRequest req)
    {
        using var conn = CreateConnection();
        var p = new DynamicParameters();
        p.Add("p_wh_code", req.WarehouseCode);
        p.Add("p_item_code", req.ItemCode);
        p.Add("p_qty", req.Quantity);
        p.Add("p_ref_no", req.ReferenceNo);
        p.Add("p_user", req.User);

        await conn.ExecuteAsync("ERP_OPERATIONS.create_stock_in", p, commandType: CommandType.StoredProcedure);
    }

    public async Task ExecuteStockOutAsync(StockOutRequest req)
    {
        using var conn = CreateConnection();
        var p = new DynamicParameters();
        p.Add("p_wh_code", req.WarehouseCode);
        p.Add("p_item_code", req.ItemCode);
        p.Add("p_qty", req.Quantity);
        p.Add("p_ref_no", req.ReferenceNo);
        p.Add("p_user", req.User);

        await conn.ExecuteAsync("ERP_OPERATIONS.create_stock_out", p, commandType: CommandType.StoredProcedure);
    }

    public async Task CreateProductionOrderAsync(CreateProductionOrderRequest req)
    {
        using var conn = CreateConnection();
        var p = new DynamicParameters();
        p.Add("p_po_no", req.ProductionOrderNo);
        p.Add("p_fg_code", req.FinishedGoodCode);
        p.Add("p_qty", req.PlannedQuantity);
        p.Add("p_wh_code", req.WarehouseCode);
        p.Add("p_user", req.User);

        await conn.ExecuteAsync("ERP_OPERATIONS.create_production_order", p, commandType: CommandType.StoredProcedure);
    }

    public async Task CompleteProductionOrderAsync(string poNo, string user)
    {
        using var conn = CreateConnection();
        var p = new DynamicParameters();
        p.Add("p_po_no", poNo);
        p.Add("p_user", user);

        await conn.ExecuteAsync("ERP_OPERATIONS.complete_production_order", p, commandType: CommandType.StoredProcedure);
    }

    public async Task<IEnumerable<ErrorLogDto>> GetErrorLogsAsync(string? refNo)
    {
        using var conn = CreateConnection();
        string sql = @"
            SELECT ID, ERR_CODE AS ErrorCode, MESSAGE, PROC_NAME AS ProcedureName, 
                   REF_NO AS ReferenceNo, CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt
            FROM ERROR_LOG
            " + (string.IsNullOrWhiteSpace(refNo) ? "" : "WHERE REF_NO = :RefNo ") + @"
            ORDER BY ID DESC";
        return await conn.QueryAsync<ErrorLogDto>(sql, new { RefNo = refNo });
    }

    public async Task CreateChangeRequestAsync(CreateChangeRequest req)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO CHANGE_REQUEST (CR_NO, TITLE, REQ_TYPE, REF_NO, ROOT_CAUSE, FIX_ACTION, STATUS, REQUESTER)
            VALUES (:ChangeRequestNo, :Title, :RequestType, :ReferenceNo, :RootCause, :FixAction, 'OPEN', :Requester)";
        await conn.ExecuteAsync(sql, req);
    }
}
