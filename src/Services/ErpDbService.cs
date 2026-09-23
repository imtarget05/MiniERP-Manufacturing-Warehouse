using System.Data;
using Dapper;
using MiniERP.Api.Models;
using Oracle.ManagedDataAccess.Client;

namespace MiniERP.Api.Services;

/// <summary>
/// Data access layer: every business write goes through package ERP_OPERATIONS
/// so that ACID rules live inside Oracle, exactly like a real ERP deployment.
/// Oracle business errors are translated into <see cref="ErpBusinessException"/>
/// with a stable error code (see docs/04-plsql-spec.md section 4).
/// </summary>
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

    /// <summary>
    /// Execute a parameterless-ish PL/SQL procedure of package ERP_OPERATIONS.
    /// </summary>
    private async Task ExecuteProcedureAsync(string call, Func<DynamicParameters> bind, string procName)
    {
        using var conn = CreateConnection();
        var p = bind();
        try
        {
            await conn.ExecuteAsync(call, p, commandType: CommandType.StoredProcedure);
        }
        catch (OracleException ex)
        {
            var oraCode = ErpErrorMapper.OracleCodeFor(ex.Number);
            var businessCode = ErpErrorMapper.BusinessCodeFor(ex.Number);
            _logger.LogWarning("Oracle business error {OraCode}/{BusinessCode} in {Proc}: {Message}",
                oraCode, businessCode, procName, ex.Message);
            throw new ErpBusinessException(oraCode, businessCode, procName, Clean(ex.Message),
                ErpErrorMapper.Normalize(ex.Number));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling {Proc}", procName);
            throw new ErpBusinessException("ORA-00000", "ERR_INTERNAL", procName, Clean(ex.Message));
        }
    }

    private static string Clean(string message) =>
        message.Replace("\r", " ").Replace("\n", " ").Trim();

    // ================================================================== READS

    // Dapper materializes positional records by matching constructor parameters
    // to column names AND provider types. Oracle's provider reports NUMBER as
    // Decimal (even for CASE/1-0 flags), so the rows below mirror the wire types
    // and are converted into the public DTOs explicitly.
    private sealed record WarehouseRow(decimal Id, string Code, string Name, string? Location, decimal IsActive);

    private sealed record StockRow(string WarehouseCode, string ItemCode, string ItemName, string ItemType,
        string Uom, decimal Quantity, decimal MinStock, decimal IsBelowMinStock);

    private sealed record ErrorLogRow(decimal Id, string ErrorCode, string Message, string? ProcedureName,
        string? ReferenceNo, string CreatedBy, DateTime CreatedAt);

    public async Task<IEnumerable<WarehouseDto>> GetWarehousesAsync()
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT ID, CODE, NAME, LOCATION, (CASE WHEN IS_ACTIVE = 1 THEN 1 ELSE 0 END) AS IsActive
            FROM WAREHOUSE
            ORDER BY CODE";
        var rows = await conn.QueryAsync<WarehouseRow>(sql);
        return rows.Select(r => new WarehouseDto((int)r.Id, r.Code, r.Name, r.Location, r.IsActive == 1));
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
        var rows = await conn.QueryAsync<StockRow>(sql, new { WarehouseCode = warehouseCode });
        return rows.Select(r => new StockItemDto(r.WarehouseCode, r.ItemCode, r.ItemName, r.ItemType,
            r.Uom, r.Quantity, r.MinStock, r.IsBelowMinStock == 1));
    }

    public async Task<IEnumerable<ErrorLogDto>> GetErrorLogsAsync(string? refNo, int take = 50)
    {
        using var conn = CreateConnection();
        var filter = string.IsNullOrWhiteSpace(refNo) ? string.Empty : "WHERE REF_NO = :RefNo ";
        var sql = $@"
            SELECT * FROM (
                SELECT ID, ERR_CODE AS ErrorCode, MESSAGE AS Message,
                       PROC_NAME AS ProcedureName, REF_NO AS ReferenceNo,
                       CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt
                FROM ERROR_LOG
                {filter}
                ORDER BY ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<ErrorLogRow>(sql, new { RefNo = refNo, Take = take });
        return rows.Select(r => new ErrorLogDto((int)r.Id, r.ErrorCode, r.Message, r.ProcedureName,
            r.ReferenceNo, r.CreatedBy, r.CreatedAt));
    }

    // =============================================================== BUSINESS

    public Task ExecuteStockInAsync(StockInRequest req) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.create_stock_in",
        () => new DynamicParameters(new
        {
            p_wh_code = req.WarehouseCode,
            p_item_code = req.ItemCode,
            p_qty = req.Quantity,
            p_ref_no = req.ReferenceNo,
            p_user = req.User,
        }), "create_stock_in");

    public Task ExecuteStockOutAsync(StockOutRequest req) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.create_stock_out",
        () => new DynamicParameters(new
        {
            p_wh_code = req.WarehouseCode,
            p_item_code = req.ItemCode,
            p_qty = req.Quantity,
            p_ref_no = req.ReferenceNo,
            p_user = req.User,
        }), "create_stock_out");

    public Task SaveBomLineAsync(BomLineRequest req) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.save_bom_line",
        () => new DynamicParameters(new
        {
            p_fg_code = req.FinishedGoodCode,
            p_version = req.Version,
            p_mat_code = req.MaterialCode,
            p_qty_req = req.QuantityRequired,
            p_user = req.User,
        }), "save_bom_line");

    public Task CreateProductionOrderAsync(CreateProductionOrderRequest req) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.create_production_order",
        () => new DynamicParameters(new
        {
            p_po_no = req.ProductionOrderNo,
            p_fg_code = req.FinishedGoodCode,
            p_qty = req.PlannedQuantity,
            p_wh_code = req.WarehouseCode,
            p_user = req.User,
        }), "create_production_order");

    public Task CompleteProductionOrderAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.complete_production_order",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "complete_production_order");

    public Task CancelProductionOrderAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.cancel_production_order",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "cancel_production_order");

    public Task ReceivePurchaseOrderAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_OPERATIONS.receive_purchase_order",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "receive_purchase_order");

    // ============================================================= AUTOMATION
    // Every call below goes through package ERP_AUTOMATION (sql/06). OUT
    // parameters are read back while the connection is still open.

    private async Task<T> ExecuteProcedureWithOutAsync<T>(
        string call, Func<DynamicParameters> bind, string outName, string procName)
    {
        using var conn = CreateConnection();
        var p = bind();
        try
        {
            await conn.ExecuteAsync(call, p, commandType: CommandType.StoredProcedure);
            return p.Get<T>(outName);
        }
        catch (OracleException ex)
        {
            var oraCode = ErpErrorMapper.OracleCodeFor(ex.Number);
            var businessCode = ErpErrorMapper.BusinessCodeFor(ex.Number);
            _logger.LogWarning("Oracle business error {OraCode}/{BusinessCode} in {Proc}: {Message}",
                oraCode, businessCode, procName, ex.Message);
            throw new ErpBusinessException(oraCode, businessCode, procName, Clean(ex.Message),
                ErpErrorMapper.Normalize(ex.Number));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling {Proc}", procName);
            throw new ErpBusinessException("ORA-00000", "ERR_INTERNAL", procName, Clean(ex.Message));
        }
    }

    /// <summary>W1: material availability check; returns the resulting PO status.</summary>
    public async Task<string> CheckMaterialAvailabilityAsync(string poNo, string user)
    {
        var status = await ExecuteProcedureWithOutAsync<string>(
            "ERP_AUTOMATION.check_material_availability",
            () =>
            {
                var p = new DynamicParameters(new { p_po_no = poNo, p_user = user });
                p.Add("p_status", dbType: DbType.String,
                    direction: ParameterDirection.Output, size: 30);
                return p;
            }, "p_status", "check_material_availability");
        return status ?? string.Empty;
    }

    public Task ReserveMaterialsAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_AUTOMATION.reserve_materials",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "reserve_materials");

    public Task ReleaseReservationsAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_AUTOMATION.release_reservations",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "release_reservations");

    /// <summary>W4: transactional completion wrapper (reservations + stock in one tx).</summary>
    public Task CompleteReservedOrderAsync(string poNo, string user) => ExecuteProcedureAsync(
        "ERP_AUTOMATION.complete_reserved_order",
        () => new DynamicParameters(new { p_po_no = poNo, p_user = user }),
        "complete_reserved_order");

    /// <summary>W3: replenishment rule for one line; returns suggested qty.</summary>
    public Task<decimal> EvaluateReplenishmentAsync(
        string itemCode, string warehouseCode, string trigger, string user)
        => ExecuteProcedureWithOutAsync<decimal>(
            "ERP_AUTOMATION.evaluate_replenishment",
            () =>
            {
                var p = new DynamicParameters(new
                {
                    p_item_code = itemCode,
                    p_wh_code = warehouseCode,
                    p_trigger = trigger,
                    p_user = user,
                });
                p.Add("p_suggested", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_suggested", "evaluate_replenishment");

    /// <summary>W3 batch sweep; returns the number of OPEN alerts afterwards.</summary>
    public async Task<int> SweepReplenishmentAsync(string user)
    {
        var alerts = await ExecuteProcedureWithOutAsync<decimal>(
            "ERP_AUTOMATION.sweep_replenishment",
            () =>
            {
                var p = new DynamicParameters(new { p_user = user });
                p.Add("p_alerts", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_alerts", "sweep_replenishment");
        return (int)alerts;
    }

    /// <summary>W5: stale PO detection run; returns the stale order count.</summary>
    public async Task<int> DetectStaleOrdersAsync(int days, string user)
    {
        var count = await ExecuteProcedureWithOutAsync<decimal>(
            "ERP_AUTOMATION.detect_stale_orders",
            () =>
            {
                var p = new DynamicParameters(new { p_days = days, p_user = user });
                p.Add("p_stale_count", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_stale_count", "detect_stale_orders");
        return (int)count;
    }

    /// <summary>W6: generate a scheduled report; returns the new report id.</summary>
    public async Task<long> GenerateReportAsync(
        string reportType, DateTime? periodFrom, DateTime? periodTo, string user)
    {
        var id = await ExecuteProcedureWithOutAsync<decimal>(
            "ERP_AUTOMATION.generate_report",
            () =>
            {
                var p = new DynamicParameters(new
                {
                    p_report_type = reportType,
                    p_period_from = periodFrom,
                    p_period_to = periodTo,
                    p_user = user,
                });
                p.Add("p_report_id", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_report_id", "generate_report");
        return (long)id;
    }

    /// <summary>W7: collect incident context; returns the new incident id.</summary>
    public async Task<long> CollectIncidentContextAsync(
        string refNo, string errorCode, string title, string user)
    {
        var id = await ExecuteProcedureWithOutAsync<decimal>(
            "ERP_AUTOMATION.collect_incident_context",
            () =>
            {
                var p = new DynamicParameters(new
                {
                    p_ref_no = refNo,
                    p_error_code = errorCode,
                    p_title = string.IsNullOrEmpty(title) ? null : title,
                    p_user = user,
                });
                p.Add("p_incident_id", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_incident_id", "collect_incident_context");
        return (long)id;
    }

    public Task RequestApprovalAsync(string approvalNo, string action,
        string refNo, string payload, string requester) => ExecuteProcedureAsync(
        "ERP_AUTOMATION.request_approval",
        () => new DynamicParameters(new
        {
            p_approval_no = approvalNo,
            p_action = action,
            p_ref_no = string.IsNullOrEmpty(refNo) ? null : refNo,
            p_payload = string.IsNullOrEmpty(payload) ? null : payload,
            p_requester = requester,
        }), "request_approval");

    public Task DecideApprovalAsync(string approvalNo, string decision, string approver)
        => ExecuteProcedureAsync(
            "ERP_AUTOMATION.decide_approval",
            () => new DynamicParameters(new
            {
                p_approval_no = approvalNo,
                p_decision = decision,
                p_approver = approver,
            }), "decide_approval");

    /// <summary>Approval-gated stock override (ORA-20010 without an APPROVED token).</summary>
    public Task AdjustStockAsync(string warehouseCode, string itemCode,
        decimal quantityDelta, string approvalNo, string user) => ExecuteProcedureAsync(
        "ERP_AUTOMATION.adjust_stock",
        () => new DynamicParameters(new
        {
            p_wh_code = warehouseCode,
            p_item_code = itemCode,
            p_qty_delta = quantityDelta,
            p_approval_no = approvalNo,
            p_user = user,
        }), "adjust_stock");

    // ---------------------------------------------------------- automation reads
    // Row records mirror provider wire types (Oracle NUMBER -> decimal), then
    // map to the public DTOs explicitly - same pattern as the reads above.

    private sealed record AlertRow(decimal Id, string ItemCode, string WarehouseCode,
        decimal QuantityAvailable, decimal QuantitySuggested, string TriggerType,
        string? RefNo, string Status, string TriggeredBy, DateTime CreatedAt, DateTime? ClosedAt);

    private sealed record StaleOrderRow(string ProductionOrderNo, string FinishedGoodCode,
        decimal PlannedQuantity, string Status, DateTime CreatedAt, DateTime LastChangeAt,
        decimal DaysIdle);

    private sealed record AutomationRunRow(decimal Id, string WorkflowName, string TriggerType,
        string? TriggerId, string Status, DateTime StartedAt, DateTime? FinishedAt,
        decimal RetryCount, string? ErrorCode, string? ErrorMessage, string? ResultSummary,
        string RunBy);

    private sealed record ReportRow(decimal Id, string ReportType, DateTime? PeriodFrom,
        DateTime? PeriodTo, decimal LineCount, string? PayloadJson, string GeneratedBy,
        DateTime GeneratedAt);

    private sealed record IncidentRow(decimal Id, string ErrorCode, string? RefNo,
        string Title, string? ContextJson, string? Diagnosis, string Status,
        string CreatedBy, DateTime CreatedAt);

    private sealed record ApprovalRow(decimal Id, string ApprovalNo, string Action,
        string? RefNo, string? Payload, string Status, string? Requester,
        string? Approver, DateTime? DecidedAt, DateTime CreatedAt);

    /// <summary>Replenishment alerts raised by W1/W3 (filter by status).</summary>
    public async Task<IEnumerable<ReplenishAlertDto>> GetReplenishAlertsAsync(
        string? status, int take = 50)
    {
        using var conn = CreateConnection();
        var filter = string.IsNullOrWhiteSpace(status)
            ? string.Empty : "WHERE a.STATUS = :Status ";
        var sql = $@"
            SELECT * FROM (
                SELECT a.ID, i.CODE AS ItemCode, w.CODE AS WarehouseCode,
                       a.QTY_AVAILABLE AS QuantityAvailable,
                       a.QTY_SUGGESTED AS QuantitySuggested,
                       a.TRIGGER_TYPE AS TriggerType, a.REF_NO AS RefNo,
                       a.STATUS, a.TRIGGERED_BY AS TriggeredBy,
                       a.CREATED_AT, a.CLOSED_AT
                  FROM REPLENISH_ALERT a
                  JOIN ITEM i ON a.ITEM_ID = i.ID
                  JOIN WAREHOUSE w ON a.WAREHOUSE_ID = w.ID
                  {filter}
                 ORDER BY a.ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<AlertRow>(sql, new { Status = status, Take = take });
        return rows.Select(r => new ReplenishAlertDto((int)r.Id, r.ItemCode, r.WarehouseCode,
            r.QuantityAvailable, r.QuantitySuggested, r.TriggerType, r.RefNo, r.Status,
            r.TriggeredBy, r.CreatedAt, r.ClosedAt));
    }

    /// <summary>Non-terminal POs idle for more than `days` since the last change.</summary>
    public async Task<IEnumerable<StaleOrderDto>> GetStaleOrdersAsync(int days)
    {
        using var conn = CreateConnection();
        // POs that never changed status yet (no history row) fall back to
        // CREATED_AT - mirrors detect_stale_orders in the package.
        const string sql = @"
            SELECT p.PO_NO AS ProductionOrderNo, i.CODE AS FinishedGoodCode,
                   p.QTY_PLANNED AS PlannedQuantity, p.STATUS,
                   p.CREATED_AT AS CreatedAt,
                   NVL(x.LastChangeAt, p.CREATED_AT) AS LastChangeAt,
                   FLOOR(SYSDATE - NVL(x.LastChangeAt, p.CREATED_AT)) AS DaysIdle
              FROM PRODUCTION_ORDER p
              JOIN ITEM i ON p.FG_ITEM_ID = i.ID
              LEFT JOIN (
                    SELECT h.PO_NO, MAX(h.CHANGED_AT) AS LastChangeAt
                      FROM PO_STATE_HISTORY h
                     GROUP BY h.PO_NO
                   ) x ON x.PO_NO = p.PO_NO
             WHERE p.STATUS NOT IN ('COMPLETED', 'CANCELLED')
               AND NVL(x.LastChangeAt, p.CREATED_AT) < SYSDATE - :Days
             ORDER BY NVL(x.LastChangeAt, p.CREATED_AT)";
        var rows = await conn.QueryAsync<StaleOrderRow>(sql, new { Days = days });
        return rows.Select(r => new StaleOrderDto(r.ProductionOrderNo, r.FinishedGoodCode,
            r.PlannedQuantity, r.Status, r.CreatedAt, r.LastChangeAt, (int)r.DaysIdle));
    }

    /// <summary>ERP_AUTOMATION_RUN audit rows (newest first).</summary>
    public async Task<IEnumerable<AutomationRunDto>> GetAutomationRunsAsync(
        string? workflow, string? status, int take = 50)
    {
        using var conn = CreateConnection();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(workflow)) filters.Add("WORKFLOW_NAME = :Workflow");
        if (!string.IsNullOrWhiteSpace(status)) filters.Add("STATUS = :Status");
        var where = filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) + " " : string.Empty;
        var sql = $@"
            SELECT * FROM (
                SELECT ID, WORKFLOW_NAME, TRIGGER_TYPE, TRIGGER_ID, STATUS,
                       STARTED_AT, FINISHED_AT, RETRY_COUNT, ERROR_CODE,
                       ERROR_MESSAGE, RESULT_SUMMARY, RUN_BY
                  FROM ERP_AUTOMATION_RUN
                  {where}
                 ORDER BY ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<AutomationRunRow>(sql,
            new { Workflow = workflow, Status = status, Take = take });
        return rows.Select(r => new AutomationRunDto((int)r.Id, r.WorkflowName, r.TriggerType,
            r.TriggerId, r.Status, r.StartedAt, r.FinishedAt, (int)r.RetryCount,
            r.ErrorCode, r.ErrorMessage, r.ResultSummary, r.RunBy));
    }

    /// <summary>Persisted report snapshots; `withPayload` adds the JSON body.</summary>
    public async Task<IEnumerable<AutomationReportDto>> GetReportsAsync(
        string? reportType, bool withPayload, int take = 20)
    {
        using var conn = CreateConnection();
        var filter = string.IsNullOrWhiteSpace(reportType)
            ? string.Empty : "WHERE REPORT_TYPE = :ReportType ";
        var payloadCol = withPayload ? "PAYLOAD_JSON" : "TO_CLOB(NULL)";
        var sql = $@"
            SELECT * FROM (
                SELECT ID, REPORT_TYPE, PERIOD_FROM, PERIOD_TO, LINE_COUNT,
                       {payloadCol} AS PayloadJson,
                       GENERATED_BY, GENERATED_AT
                  FROM AUTOMATION_REPORT
                  {filter}
                 ORDER BY ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<ReportRow>(sql,
            new { ReportType = reportType, Take = take });
        return rows.Select(r => new AutomationReportDto((int)r.Id, r.ReportType,
            r.PeriodFrom, r.PeriodTo, (int)r.LineCount, r.PayloadJson,
            r.GeneratedBy, r.GeneratedAt));
    }

    /// <summary>Support incidents collected by W7 (newest first).</summary>
    public async Task<IEnumerable<SupportIncidentDto>> GetIncidentsAsync(
        string? refNo, int take = 20)
    {
        using var conn = CreateConnection();
        var filter = string.IsNullOrWhiteSpace(refNo)
            ? string.Empty : "WHERE REF_NO = :RefNo ";
        var sql = $@"
            SELECT * FROM (
                SELECT ID, ERROR_CODE, REF_NO, TITLE, CONTEXT_JSON, DIAGNOSIS,
                       STATUS, CREATED_BY, CREATED_AT
                  FROM SUPPORT_INCIDENT
                  {filter}
                 ORDER BY ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<IncidentRow>(sql, new { RefNo = refNo, Take = take });
        return rows.Select(r => new SupportIncidentDto((int)r.Id, r.ErrorCode, r.RefNo,
            r.Title, r.ContextJson, r.Diagnosis, r.Status, r.CreatedBy, r.CreatedAt));
    }

    /// <summary>Approval tokens (filter by status: PENDING/APPROVED/...).</summary>
    public async Task<IEnumerable<ApprovalDto>> GetApprovalsAsync(
        string? status, int take = 20)
    {
        using var conn = CreateConnection();
        var filter = string.IsNullOrWhiteSpace(status)
            ? string.Empty : "WHERE STATUS = :Status ";
        var sql = $@"
            SELECT * FROM (
                SELECT ID, APPROVAL_NO, ACTION, REF_NO, PAYLOAD, STATUS,
                       REQUESTER, APPROVER, DECIDED_AT, CREATED_AT
                  FROM APPROVAL_REQUEST
                  {filter}
                 ORDER BY ID DESC
            ) WHERE ROWNUM <= :Take";
        var rows = await conn.QueryAsync<ApprovalRow>(sql, new { Status = status, Take = take });
        return rows.Select(r => new ApprovalDto((int)r.Id, r.ApprovalNo, r.Action,
            r.RefNo, r.Payload, r.Status, r.Requester, r.Approver,
            r.DecidedAt, r.CreatedAt));
    }

    // ================================================================== WRITES

    public async Task<CreateChangeRequestResponse> CreateChangeRequestAsync(CreateChangeRequest req)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO CHANGE_REQUEST (CR_NO, TITLE, REQ_TYPE, REF_NO, ROOT_CAUSE, FIX_ACTION, STATUS, REQUESTER)
            VALUES (:ChangeRequestNo, :Title, :RequestType, :ReferenceNo, :RootCause, :FixAction, 'OPEN', :Requester)";
        var affected = await conn.ExecuteAsync(sql, req);
        if (affected == 0)
        {
            throw new InvalidOperationException($"Change request {req.ChangeRequestNo} was not created.");
        }

        return new CreateChangeRequestResponse(req.ChangeRequestNo, "OPEN");
    }

    public async Task<ChangeRequestDto?> GetChangeRequestAsync(string crNo)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT CR_NO AS ChangeRequestNo, TITLE AS Title, REQ_TYPE AS RequestType,
                   REF_NO AS ReferenceNo, ROOT_CAUSE AS RootCause, FIX_ACTION AS FixAction,
                   STATUS AS Status, REQUESTER AS Requester,
                   CREATED_AT AS CreatedAt, CLOSED_AT AS ClosedAt
            FROM CHANGE_REQUEST WHERE CR_NO = :CrNo";
        return await conn.QueryFirstOrDefaultAsync<ChangeRequestDto>(sql, new { CrNo = crNo });
    }

    public async Task<ProductionOrderDto?> GetProductionOrderAsync(string poNo)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT po.PO_NO AS ProductionOrderNo, i.CODE AS FinishedGoodCode,
                   po.QTY_PLANNED AS PlannedQuantity, po.QTY_DONE AS DoneQuantity,
                   po.STATUS AS Status, w.CODE AS WarehouseCode,
                   po.CREATED_AT AS CreatedAt, po.COMPLETED_AT AS CompletedAt
            FROM PRODUCTION_ORDER po
            JOIN ITEM i ON po.FG_ITEM_ID = i.ID
            JOIN WAREHOUSE w ON po.WAREHOUSE_ID = w.ID
            WHERE po.PO_NO = :PoNo";
        return await conn.QueryFirstOrDefaultAsync<ProductionOrderDto>(sql, new { PoNo = poNo });
    }

    /// <summary>
    /// Register a supplier delivery note (PURCHASE_ORDER in CREATED state) so it
    /// can later be received through ERP_OPERATIONS.receive_purchase_order.
    /// Master data only - no stock movement happens here.
    /// </summary>
    public async Task CreatePurchaseOrderAsync(CreatePurchaseOrderRequest req)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO PURCHASE_ORDER (PO_NO, ITEM_ID, QTY, WAREHOUSE_ID, STATUS)
            VALUES (:PurchaseOrderNo,
                    (SELECT ID FROM ITEM WHERE CODE = :ItemCode),
                    :Quantity,
                    (SELECT ID FROM WAREHOUSE WHERE CODE = :WarehouseCode),
                    'CREATED')";
        var affected = await conn.ExecuteAsync(sql, req);
        if (affected == 0)
        {
            throw new InvalidOperationException($"Purchase order {req.PurchaseOrderNo} was not created.");
        }
    }

    public async Task<DbHealthDto> ProbeAsync()
    {
        using var conn = CreateConnection();
        var banner = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT banner FROM v$version WHERE ROWNUM = 1");
        // Oracle NUMBER surfaces as Decimal through the provider, hence the cast.
        var tables = await conn.ExecuteScalarAsync<decimal>("SELECT COUNT(*) FROM user_tables");
        var pkg = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT status FROM user_objects WHERE object_name = 'ERP_OPERATIONS' AND object_type = 'PACKAGE BODY'");
        return new DbHealthDto(banner, (int)tables, pkg);
    }
}

