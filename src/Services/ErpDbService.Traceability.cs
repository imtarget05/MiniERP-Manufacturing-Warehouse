using System.Data;
using Dapper;
using MiniERP.Api.Models;
using Oracle.ManagedDataAccess.Client;

namespace MiniERP.Api.Services;

/// <summary>
/// Traceability extension of <see cref="ErpDbService"/> (partial class, same
/// service — no duplicate DbService). Reads for locations/lots/barcodes plus
/// write-through helpers for the ERP_TRACEABILITY package.
/// </summary>
public partial class ErpDbService
{
    // ------------------------------------------------------------ reads

    private sealed record LocationRow(decimal Id, string WarehouseCode,
        string LocationCode, string? LocationName, string LocationType, decimal IsActive);

    private sealed record LotRow(string LotCode, string ItemCode, string? SupplierLotNo,
        string SourceType, string? SourceRefNo, DateTime? MfgDate,
        DateTime? ExpiryDate, string Status);

    private sealed record LotStockRow(string LotCode, string WarehouseCode,
        string LocationCode, decimal QtyOnHand, decimal QtyReserved);

    public async Task<IEnumerable<WarehouseLocationDto>> GetLocationsAsync(string warehouseCode)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT l.ID, w.CODE AS WarehouseCode, l.LOCATION_CODE AS LocationCode,
                   l.LOCATION_NAME AS LocationName, l.LOCATION_TYPE AS LocationType,
                   (CASE WHEN l.IS_ACTIVE = 1 THEN 1 ELSE 0 END) AS IsActive
              FROM WAREHOUSE_LOCATION l JOIN WAREHOUSE w ON l.WAREHOUSE_ID = w.ID
             WHERE w.CODE = :Code ORDER BY l.LOCATION_CODE";
        var rows = await conn.QueryAsync<LocationRow>(sql, new { Code = warehouseCode });
        return rows.Select(r => new WarehouseLocationDto((int)r.Id, r.WarehouseCode,
            r.LocationCode, r.LocationName, r.LocationType, r.IsActive == 1));
    }

    public async Task<InventoryLotDto?> GetLotAsync(string lotCode)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT l.LOT_CODE AS LotCode, i.CODE AS ItemCode,
                   l.SUPPLIER_LOT_NO AS SupplierLotNo, l.SOURCE_TYPE AS SourceType,
                   l.SOURCE_REF_NO AS SourceRefNo, l.MFG_DATE AS MfgDate,
                   l.EXPIRY_DATE AS ExpiryDate, l.STATUS AS Status
              FROM INVENTORY_LOT l JOIN ITEM i ON l.ITEM_ID = i.ID
             WHERE l.LOT_CODE = :Code";
        return await conn.QueryFirstOrDefaultAsync<InventoryLotDto>(
            sql, new { Code = lotCode.ToUpperInvariant() });
    }

    public async Task<IEnumerable<LotStockDto>> GetLotStockAsync(string lotCode)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT l.LOT_CODE AS LotCode, w.CODE AS WarehouseCode,
                   loc.LOCATION_CODE AS LocationCode,
                   s.QTY_ON_HAND AS QtyOnHand, s.QTY_RESERVED AS QtyReserved
              FROM LOT_STOCK s
              JOIN INVENTORY_LOT l ON s.LOT_ID = l.LOT_ID
              JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
              JOIN WAREHOUSE_LOCATION loc ON s.LOCATION_ID = loc.ID
             WHERE l.LOT_CODE = :Code ORDER BY w.CODE, loc.LOCATION_CODE";
        var rows = await conn.QueryAsync<LotStockRow>(
            sql, new { Code = lotCode.ToUpperInvariant() });
        return rows.Select(r => new LotStockDto(r.LotCode, r.WarehouseCode,
            r.LocationCode, r.QtyOnHand, r.QtyReserved, r.QtyOnHand - r.QtyReserved));
    }

    // ------------------------------------------------------------ writes

    /// <summary>Canonical idempotency hash over the stock-affecting payload.</summary>
    public static string IdempotencyHash(string operation, object payload) =>
        TraceabilityLogic.HashRequest(operation,
            System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)));

    /// <summary>
    /// Translate the PL/SQL replay sentinel (ORA-20014) into a replay result:
    /// the response body carries replayed=true and no extra stock movement
    /// happened (the procedure rolled back to the pre-call state).
    /// </summary>
    public static bool IsReplay(ErpBusinessException ex) =>
        ex.BusinessCode == "ERR_DUPLICATE_REPLAY";

    /// <summary>Idempotent location provisioning (validates type client-side too).</summary>
    public Task EnsureLocationAsync(string warehouseCode, CreateLocationRequest req, string actor)
    {
        if (!TraceabilityLogic.IsValidLocationType(req.LocationType))
        {
            throw new ErpBusinessException("ORA-20012", "ERR_INVALID_INPUT",
                "ensure_location", $"Invalid LOCATION_TYPE: {req.LocationType}.", 20012);
        }

        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.ensure_location",
            () => new DynamicParameters(new
            {
                p_wh_code = warehouseCode,
                p_loc_code = req.LocationCode,
                p_loc_name = req.LocationName,
                p_loc_type = req.LocationType.ToUpperInvariant(),
                p_user = ResolveActor(actor),
            }), "ensure_location");
    }

    public Task ReceiveLotAsync(string purchaseOrderNo, ReceiveLotRequest req, string actor)
    {
        var hash = IdempotencyHash("RECEIVE_LOT", new
        {
            po = purchaseOrderNo,
            item = req.ItemCode,
            qty = req.Qty,
            lot = req.LotCode,
            loc = req.ReceivingLocationCode,
        });
        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.receive_lot_stock",
            () => new DynamicParameters(new
            {
                p_po_no = purchaseOrderNo,
                p_item_code = req.ItemCode,
                p_qty = req.Qty,
                p_lot_code = req.LotCode,
                p_supplier_lot = req.SupplierLotNo,
                p_mfg = req.MfgDate,
                p_expiry = req.ExpiryDate,
                p_loc_code = req.ReceivingLocationCode,
                p_idem_key = req.IdempotencyKey,
                p_req_hash = hash,
                p_user = ResolveActor(actor),
            }), "receive_lot_stock");
    }

    public Task PutawayLotAsync(PutawayLotRequest req, string actor)
    {
        var hash = IdempotencyHash("PUTAWAY", new
        {
            lot = req.LotCode,
            fromLoc = req.FromLocationCode,
            toLoc = req.ToLocationCode,
            qty = req.Qty,
        });
        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.putaway_lot",
            () => new DynamicParameters(new
            {
                p_lot_code = req.LotCode,
                p_from_loc = req.FromLocationCode,
                p_to_loc = req.ToLocationCode,
                p_qty = req.Qty,
                p_idem_key = req.IdempotencyKey,
                p_req_hash = hash,
                p_user = ResolveActor(actor),
            }), "putaway_lot");
    }

    public Task MoveLotAsync(MoveLotRequest req, string actor)
    {
        var hash = IdempotencyHash("MOVE_LOT", new
        {
            lot = req.LotCode,
            fromWh = req.FromWarehouseCode,
            fromLoc = req.FromLocationCode,
            toWh = req.ToWarehouseCode,
            toLoc = req.ToLocationCode,
            qty = req.Qty,
        });
        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.move_lot_stock",
            () => new DynamicParameters(new
            {
                p_lot_code = req.LotCode,
                p_from_wh = req.FromWarehouseCode,
                p_from_loc = req.FromLocationCode,
                p_to_wh = req.ToWarehouseCode,
                p_to_loc = req.ToLocationCode,
                p_qty = req.Qty,
                p_idem_key = req.IdempotencyKey,
                p_req_hash = hash,
                p_user = ResolveActor(actor),
            }), "move_lot_stock");
    }

    public Task SetLotHoldAsync(string lotCode, string status, string actor) =>
        ExecuteProcedureAsync(
            "ERP_TRACEABILITY.set_lot_hold",
            () => new DynamicParameters(new
            {
                p_lot_code = lotCode,
                p_status = status.ToUpperInvariant(),
                p_user = ResolveActor(actor),
            }), "set_lot_hold");

    // ---------------------------------------- Phase 3: genealogy

    private sealed record BomLineRow(decimal ItemId, string ItemCode,
        string TraceMode, decimal QtyRequired);

    private sealed record AllocationLotRow(string LotCode, DateTime? ExpiryDate,
        DateTime CreatedAt, decimal QtyAvailable);

    private sealed record AllocStockRow(decimal ItemId, decimal Qty);

    /// <summary>
    /// FEFO/FIFO allocation preview: package availability (OUT ref cursor)
    /// merged with the BOM requirement, aggregate STOCK for non-lot lines and
    /// per-lot FEFO suggestions for LOT-tracked lines. Read-only.
    /// </summary>
    public async Task<AllocationPreviewResponse> GetAllocationAsync(string poNo)
    {
        using var conn = CreateConnection();
        var oracle = conn as OracleConnection
            ?? throw new InvalidOperationException("Oracle connection required.");
        oracle.Open();

        // Package validates the order (ORA-20002 when missing) and returns
        // issuable lot availability per BOM material.
        var availability = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = oracle.CreateCommand();
            cmd.CommandText = "ERP_TRACEABILITY.allocate_lots_preview";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.Add("p_po_no", OracleDbType.Varchar2).Value = poNo;
            var cursor = cmd.Parameters.Add("p_cur", OracleDbType.RefCursor);
            cursor.Direction = ParameterDirection.Output;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                availability[reader.GetString(0)] = Convert.ToDecimal(reader.GetValue(2));
            }
        }
        catch (OracleException ex)
        {
            _logger.LogWarning("Oracle {OraCode} in allocate_lots_preview: {Message}",
                ex.Number, ex.Message);
            throw new ErpBusinessException(
                ErpErrorMapper.OracleCodeFor(ex.Number),
                ErpErrorMapper.BusinessCodeFor(ex.Number),
                "allocate_lots_preview", ex.Message,
                ErpErrorMapper.Normalize(ex.Number));
        }

        const string bomSql = @"
            SELECT i.ID AS ItemId, i.CODE AS ItemCode, i.TRACE_MODE AS TraceMode,
                   (d.QTY_REQUIRED * o.QTY_PLANNED) AS QtyRequired
              FROM PRODUCTION_ORDER o
              JOIN BOM b ON b.FG_ITEM_ID = o.FG_ITEM_ID AND b.STATUS = 'ACTIVE'
              JOIN BOM_DETAIL d ON d.BOM_ID = b.ID
              JOIN ITEM i ON i.ID = d.MAT_ITEM_ID
             WHERE o.PO_NO = :Po
             ORDER BY i.CODE";
        var whId = await conn.QuerySingleAsync<decimal>(
            "SELECT WAREHOUSE_ID FROM PRODUCTION_ORDER WHERE PO_NO = :Po",
            new { Po = poNo });
        var stock = (await conn.QueryAsync<AllocStockRow>(
                "SELECT ITEM_ID AS ItemId, QTY AS Qty FROM STOCK WHERE WAREHOUSE_ID = :Wh",
                new { Wh = whId }))
            .ToDictionary(r => r.ItemId, r => r.Qty);
        var lines = (await conn.QueryAsync<BomLineRow>(bomSql, new { Po = poNo })).ToList();

        const string lotSql = @"
            SELECT l.LOT_CODE AS LotCode, l.EXPIRY_DATE AS ExpiryDate,
                   l.CREATED_AT AS CreatedAt,
                   (s.QTY_ON_HAND - s.QTY_RESERVED) AS QtyAvailable
              FROM LOT_STOCK s
              JOIN INVENTORY_LOT l ON s.LOT_ID = l.LOT_ID
             WHERE l.ITEM_ID = :ItemId
               AND s.WAREHOUSE_ID = :WarehouseId
               AND l.STATUS = 'ACTIVE'
               AND (l.EXPIRY_DATE IS NULL OR l.EXPIRY_DATE >= TRUNC(SYSDATE))
               AND (s.QTY_ON_HAND - s.QTY_RESERVED) > 0";

        var result = new List<AllocationLineDto>(lines.Count);
        foreach (var line in lines)
        {
            var isLot = string.Equals(line.TraceMode, "LOT",
                StringComparison.OrdinalIgnoreCase);
            decimal avail;
            var suggestions = new List<AllocationSuggestionDto>();
            if (isLot)
            {
                availability.TryGetValue(line.ItemCode, out avail);
                var rows = await conn.QueryAsync<AllocationLotRow>(
                    lotSql, new { ItemId = line.ItemId, WarehouseId = whId });
                suggestions.AddRange(TraceabilityLogic
                    .OrderForIssue(rows.Select(r => new TraceabilityLogic.LotCandidate(
                        r.LotCode, r.ExpiryDate, r.CreatedAt, r.QtyAvailable)))
                    .Select(c => new AllocationSuggestionDto(
                        c.LotCode, c.ExpiryDate, c.QtyAvailable)));
            }
            else
            {
                stock.TryGetValue(line.ItemId, out avail);
            }

            result.Add(new AllocationLineDto(line.ItemCode, line.TraceMode,
                line.QtyRequired, avail, avail < line.QtyRequired, suggestions));
        }

        return new AllocationPreviewResponse(poNo, result);
    }

    /// <summary>Issue every LOT-tracked BOM line (idempotent, single tx).</summary>
    public Task IssueLotsAsync(string poNo, IssueLotsRequest req, string actor)
    {
        var hash = IdempotencyHash("ISSUE_LOTS", new { po = poNo });
        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.issue_lot_to_production",
            () => new DynamicParameters(new
            {
                p_po_no = poNo,
                p_idem_key = req.IdempotencyKey,
                p_req_hash = hash,
                p_user = ResolveActor(actor),
            }), "issue_lot_to_production");
    }

    /// <summary>Traceable completion: FG lot + genealogy + PO close (one tx).</summary>
    public Task CompleteTraceableAsync(string poNo, CompleteTraceableRequest req, string actor)
    {
        var hash = IdempotencyHash("COMPLETE_TRACEABLE", new
        {
            po = poNo,
            fgLot = req.FgLot,
            qty = req.Qty,
            loc = req.LocationCode,
            outputWarehouse = req.OutputWarehouseCode,
            tol = req.Tolerance,
        });
        return ExecuteProcedureAsync(
            "ERP_TRACEABILITY.complete_production_traceable",
            () => new DynamicParameters(new
            {
                p_po_no = poNo,
                p_fg_lot = req.FgLot,
                p_qty = req.Qty,
                p_loc_code = req.LocationCode,
                p_output_wh = req.OutputWarehouseCode,
                p_tolerance = req.Tolerance ?? 0m,
                p_idem_key = req.IdempotencyKey,
                p_req_hash = hash,
                p_user = ResolveActor(actor),
            }), "complete_production_traceable");
    }

    // ---------------------------------------- backward/forward trace

    private sealed record LotInfoRow(string LotCode, string ItemCode,
        string? SourceType, string? SourceRefNo);

    private sealed record OrderLinkRow(string PoNo, decimal Qty);

    private sealed record TraceLotRow(string LotCode, string ItemCode,
        decimal Qty, string? SourceType, string? SourceRefNo);

    private sealed record TraceOutputLotRow(string LotCode, string ItemCode, decimal Qty);

    private const int MaxTraceDepth = 4;

    /// <summary>
    /// Build the genealogy tree for a lot from the explicit link tables only
    /// (PRODUCTION_LOT_CONSUMPTION / PRODUCTION_LOT_OUTPUT). Returns null when
    /// the lot does not exist so the route can answer 404.
    /// </summary>
    public async Task<TraceNodeDto?> GetTraceAsync(string lotCode, string direction)
    {
        var dir = TraceabilityLogic.NormalizeTraceDirection(direction);
        using var conn = CreateConnection();

        const string lotSql = @"
            SELECT l.LOT_CODE AS LotCode, i.CODE AS ItemCode,
                   l.SOURCE_TYPE AS SourceType, l.SOURCE_REF_NO AS SourceRefNo
              FROM INVENTORY_LOT l JOIN ITEM i ON l.ITEM_ID = i.ID
             WHERE l.LOT_CODE = :Code";
        var info = await conn.QueryFirstOrDefaultAsync<LotInfoRow>(
            lotSql, new { Code = lotCode.ToUpperInvariant() });
        if (info is null) return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LOT:" + info.LotCode,
        };
        var children = new List<TraceNodeDto>();
        if (dir is "BACKWARD" or "BOTH")
        {
            children.AddRange(await ExpandBackwardAsync(
                conn, info.LotCode, info.ItemCode, info.SourceType,
                info.SourceRefNo, 0, visited));
        }
        if (dir is "FORWARD" or "BOTH")
        {
            children.AddRange(await ExpandForwardAsync(
                conn, info.LotCode, 0, visited));
        }

        return new TraceNodeDto("LOT", info.LotCode, info.ItemCode, null, children);
    }

    /// <summary>Upstream: producing orders, their input lots, then recurse.</summary>
    private async Task<List<TraceNodeDto>> ExpandBackwardAsync(
        IDbConnection conn, string lotCode, string itemCode,
        string? sourceType, string? sourceRefNo, int depth,
        HashSet<string> visited)
    {
        var children = new List<TraceNodeDto>();
        if (depth >= MaxTraceDepth) return children;

        const string producingSql = @"
            SELECT o.PO_NO AS PoNo, t.QTY_PRODUCED AS Qty
              FROM PRODUCTION_LOT_OUTPUT t
              JOIN PRODUCTION_ORDER o ON t.PRODUCTION_ORDER_ID = o.ID
              JOIN INVENTORY_LOT l ON t.OUTPUT_LOT_ID = l.LOT_ID
             WHERE l.LOT_CODE = :Code";
        var orders = (await conn.QueryAsync<OrderLinkRow>(
            producingSql, new { Code = lotCode })).ToList();

        if (orders.Count == 0)
        {
            // Terminal upstream node: where the lot entered the plant.
            if (!string.IsNullOrWhiteSpace(sourceRefNo) &&
                visited.Add($"SOURCE:{sourceType}:{sourceRefNo}"))
            {
                children.Add(new TraceNodeDto("SOURCE", sourceRefNo,
                    itemCode, null, new List<TraceNodeDto>()));
            }
            return children;
        }

        const string inputsSql = @"
            SELECT l.LOT_CODE AS LotCode, i.CODE AS ItemCode,
                   c.QTY_CONSUMED AS Qty, l.SOURCE_TYPE AS SourceType,
                   l.SOURCE_REF_NO AS SourceRefNo
              FROM PRODUCTION_LOT_CONSUMPTION c
              JOIN INVENTORY_LOT l ON c.INPUT_LOT_ID = l.LOT_ID
              JOIN ITEM i ON c.ITEM_ID = i.ID
              JOIN PRODUCTION_ORDER o ON c.PRODUCTION_ORDER_ID = o.ID
             WHERE o.PO_NO = :Po";

        foreach (var order in orders)
        {
            if (!visited.Add("PO:" + order.PoNo)) continue;
            var inputNodes = new List<TraceNodeDto>();
            foreach (var input in await conn.QueryAsync<TraceLotRow>(
                inputsSql, new { Po = order.PoNo }))
            {
                if (!visited.Add("LOT:" + input.LotCode)) continue;
                var upstream = await ExpandBackwardAsync(conn, input.LotCode,
                    input.ItemCode, input.SourceType, input.SourceRefNo,
                    depth + 1, visited);
                inputNodes.Add(new TraceNodeDto("LOT", input.LotCode,
                    input.ItemCode, input.Qty, upstream));
            }
            children.Add(new TraceNodeDto("PRODUCTION_ORDER", order.PoNo,
                null, order.Qty, inputNodes));
        }
        return children;
    }

    /// <summary>Downstream: consuming orders, their output lots, then recurse.</summary>
    private async Task<List<TraceNodeDto>> ExpandForwardAsync(
        IDbConnection conn, string lotCode, int depth, HashSet<string> visited)
    {
        var children = new List<TraceNodeDto>();
        if (depth >= MaxTraceDepth) return children;

        const string consumingSql = @"
            SELECT o.PO_NO AS PoNo, c.QTY_CONSUMED AS Qty
              FROM PRODUCTION_LOT_CONSUMPTION c
              JOIN PRODUCTION_ORDER o ON c.PRODUCTION_ORDER_ID = o.ID
              JOIN INVENTORY_LOT l ON c.INPUT_LOT_ID = l.LOT_ID
             WHERE l.LOT_CODE = :Code";
        var orders = (await conn.QueryAsync<OrderLinkRow>(
            consumingSql, new { Code = lotCode })).ToList();
        if (orders.Count == 0) return children;

        const string outputsSql = @"
            SELECT l.LOT_CODE AS LotCode, i.CODE AS ItemCode,
                   t.QTY_PRODUCED AS Qty
              FROM PRODUCTION_LOT_OUTPUT t
              JOIN INVENTORY_LOT l ON t.OUTPUT_LOT_ID = l.LOT_ID
              JOIN ITEM i ON t.FG_ITEM_ID = i.ID
              JOIN PRODUCTION_ORDER o ON t.PRODUCTION_ORDER_ID = o.ID
             WHERE o.PO_NO = :Po";

        foreach (var order in orders)
        {
            if (!visited.Add("POF:" + order.PoNo)) continue;
            var outputNodes = new List<TraceNodeDto>();
            foreach (var output in await conn.QueryAsync<TraceOutputLotRow>(
                outputsSql, new { Po = order.PoNo }))
            {
                if (!visited.Add("LOT:" + output.LotCode)) continue;
                var downstream = await ExpandForwardAsync(conn,
                    output.LotCode, depth + 1, visited);
                outputNodes.Add(new TraceNodeDto("LOT", output.LotCode,
                    output.ItemCode, output.Qty, downstream));
            }
            children.Add(new TraceNodeDto("PRODUCTION_ORDER", order.PoNo,
                null, order.Qty, outputNodes));
        }
        return children;
    }

    // ---------------------------------------- Phase 4: label workflow

    private sealed record LabelData(string? ItemCode, string? ItemName, string? Uom,
        decimal Qty, string? Location, DateTime? Mfg, DateTime? Expiry);

    // Oracle returns unquoted aliases in uppercase. Keep the wire row explicit,
    // then map wire types to the public DTO rather than relying on constructor
    // name casing across Oracle.ManagedDataAccess + Dapper.
    private sealed record LabelJobRow(
        decimal PRINTJOBID, string LABELTYPE, string ENTITYTYPE, string ENTITYKEY,
        string TEMPLATECODE, decimal COPIES, string FORMAT, string STATUS);

    private static LabelJobDto MapLabel(LabelJobRow row) => new(
        (int)row.PRINTJOBID, row.LABELTYPE, row.ENTITYTYPE, row.ENTITYKEY,
        row.TEMPLATECODE, (int)row.COPIES, row.FORMAT, row.STATUS);

    /// <summary>
    /// Create a label print job via ERP_TRACEABILITY.create_label_job, then
    /// render the payload. Reprint = another POST: new audit row, no lot or
    /// stock movement (the procedure never touches INVENTORY_LOT/STOCK).
    /// </summary>
    public async Task<LabelPrintResponse> CreateLabelJobAsync(
        CreateLabelRequest req, string actor)
    {
        if (!TraceabilityLogic.IsValidLabelEntityType(req.EntityType))
        {
            throw new ErpBusinessException("ORA-20012", "ERR_INVALID_INPUT",
                "create_label_job",
                $"Invalid label entity type: {req.EntityType}.", 20012);
        }
        if (!TraceabilityLogic.IsValidLabelType(req.LabelType))
        {
            throw new ErpBusinessException("ORA-20012", "ERR_INVALID_INPUT",
                "create_label_job", $"Invalid label type: {req.LabelType}.", 20012);
        }
        string format;
        try
        {
            format = TraceabilityLogic.NormalizeLabelFormat(req.Format);
        }
        catch (ArgumentException ex)
        {
            throw new ErpBusinessException("ORA-20012", "ERR_INVALID_INPUT",
                "create_label_job", ex.Message, 20012);
        }
        if (req.Copies < 1)
        {
            throw new ErpBusinessException("ORA-20001", "ERR_INVALID_QTY",
                "create_label_job", "Copies must be >= 1.", 20001);
        }

        var jobId = await ExecuteProcedureWithOutAsync<decimal>(
            "ERP_TRACEABILITY.create_label_job",
            () =>
            {
                var p = new DynamicParameters(new
                {
                    p_entity_type = req.EntityType.Trim().ToUpperInvariant(),
                    p_entity_key = req.EntityKey.Trim().ToUpperInvariant(),
                    p_label_type = req.LabelType.Trim().ToUpperInvariant(),
                    p_copies = req.Copies,
                    p_format = format,
                    p_user = ResolveActor(actor),
                });
                p.Add("p_job_id", dbType: DbType.Decimal,
                    direction: ParameterDirection.Output);
                return p;
            }, "p_job_id", "create_label_job");

        var job = await GetLabelJobAsync((long)jobId)
            ?? throw new ErpBusinessException("ORA-20002", "ERR_NOT_FOUND",
                "create_label_job", $"Label job {jobId} not found.", 20002);
        return new LabelPrintResponse(job, await RenderLabelAsync(job));
    }

    /// <summary>Reprint audit trail: newest print jobs for an entity (or all).</summary>
    public async Task<IEnumerable<LabelJobDto>> GetLabelJobsAsync(
        string? entityType, string? entityKey)
    {
        using var conn = CreateConnection();
        var clauses = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            clauses.Add("ENTITY_TYPE = :Et");
            p.Add("Et", entityType.Trim().ToUpperInvariant());
        }
        if (!string.IsNullOrWhiteSpace(entityKey))
        {
            clauses.Add("ENTITY_KEY = :Ek");
            p.Add("Ek", entityKey.Trim().ToUpperInvariant());
        }
        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
        var sql = @"
            SELECT PRINT_JOB_ID AS PrintJobId, LABEL_TYPE AS LabelType,
                   ENTITY_TYPE AS EntityType, ENTITY_KEY AS EntityKey,
                   TEMPLATE_CODE AS TemplateCode, COPIES AS Copies,
                   FORMAT AS Format, STATUS AS Status
              FROM LABEL_PRINT_JOB" + where + @"
             ORDER BY CREATED_AT DESC, PRINT_JOB_ID DESC
             FETCH FIRST 200 ROWS ONLY";
        var rows = await conn.QueryAsync<LabelJobRow>(sql, p);
        return rows.Select(MapLabel);
    }

    public async Task<LabelJobDto?> GetLabelJobAsync(long printJobId)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT PRINT_JOB_ID AS PrintJobId, LABEL_TYPE AS LabelType,
                   ENTITY_TYPE AS EntityType, ENTITY_KEY AS EntityKey,
                   TEMPLATE_CODE AS TemplateCode, COPIES AS Copies,
                   FORMAT AS Format, STATUS AS Status
              FROM LABEL_PRINT_JOB WHERE PRINT_JOB_ID = :Id";
        var row = await conn.QueryFirstOrDefaultAsync<LabelJobRow>(
            sql, new { Id = printJobId });
        return row is null ? null : MapLabel(row);
    }

    /// <summary>Print view: re-render an existing job without creating a new one.</summary>
    public async Task<LabelPrintResponse?> RenderLabelJobAsync(long printJobId)
    {
        var job = await GetLabelJobAsync(printJobId);
        return job is null
            ? null
            : new LabelPrintResponse(job, await RenderLabelAsync(job));
    }

    private async Task<string> RenderLabelAsync(LabelJobDto job)
    {
        using var conn = CreateConnection();
        var sql = job.EntityType switch
        {
            "LOT" => @"
                SELECT i.CODE ItemCode, i.NAME ItemName, i.UOM Uom,
                       NVL((SELECT SUM(s.QTY_ON_HAND) FROM LOT_STOCK s
                             WHERE s.LOT_ID = l.LOT_ID), 0) Qty,
                       (SELECT w.CODE || '/' || loc.LOCATION_CODE
                          FROM LOT_STOCK s
                          JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
                          JOIN WAREHOUSE_LOCATION loc ON s.LOCATION_ID = loc.ID
                         WHERE s.LOT_ID = l.LOT_ID AND ROWNUM = 1) Location,
                       l.MFG_DATE Mfg, l.EXPIRY_DATE Expiry
                  FROM INVENTORY_LOT l JOIN ITEM i ON l.ITEM_ID = i.ID
                 WHERE l.LOT_CODE = :Key",
            "ITEM" => @"
                SELECT CODE ItemCode, NAME ItemName, UOM Uom, 0 Qty,
                       CAST(NULL AS VARCHAR2(60)) Location,
                       CAST(NULL AS DATE) Mfg, CAST(NULL AS DATE) Expiry
                  FROM ITEM WHERE CODE = :Key",
            "LOCATION" => @"
                SELECT CAST(NULL AS VARCHAR2(30)) ItemCode, LOCATION_NAME ItemName,
                       CAST(NULL AS VARCHAR2(10)) Uom, 0 Qty, LOCATION_CODE Location,
                       CAST(NULL AS DATE) Mfg, CAST(NULL AS DATE) Expiry
                  FROM WAREHOUSE_LOCATION
                 WHERE LOCATION_CODE = :Key AND ROWNUM = 1",
            _ => @"
                SELECT fg.CODE ItemCode, fg.NAME ItemName, fg.UOM Uom,
                       o.QTY_PLANNED Qty,
                       CAST(NULL AS VARCHAR2(60)) Location,
                       CAST(NULL AS DATE) Mfg, CAST(NULL AS DATE) Expiry
                  FROM PRODUCTION_ORDER o JOIN ITEM fg ON o.FG_ITEM_ID = fg.ID
                 WHERE o.PO_NO = :Key",
        };
        var d = await conn.QueryFirstOrDefaultAsync<LabelData>(
            sql, new { Key = job.EntityKey });
        d ??= new LabelData(null, null, null, 0m, null, null, null);
        return string.Equals(job.Format, "ZPL", StringComparison.OrdinalIgnoreCase)
            ? LabelRenderService.RenderZpl(d.ItemCode ?? job.EntityKey,
                job.EntityKey, d.Qty, d.Uom ?? "PCS")
            : LabelRenderService.RenderHtml("MiniERP Demo",
                d.ItemCode ?? job.EntityKey, d.ItemName ?? job.EntityType,
                job.EntityKey, d.Qty, d.Uom ?? "PCS",
                d.Location, d.Mfg, d.Expiry);
    }
}
