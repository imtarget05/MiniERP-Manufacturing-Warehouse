namespace MiniERP.Api.Services;

/// <summary>
/// Business exception translated from an Oracle RAISE_APPLICATION_ERROR raised
/// inside package ERP_OPERATIONS. It gives the API a stable error contract
/// (see docs/03-api-spec.md) instead of leaking raw ORA- text to clients.
/// </summary>
public sealed class ErpBusinessException : Exception
{
    public ErpBusinessException(string oracleErrorCode, string businessCode, string procedureName, string message,
        int oracleErrorNumber = 0)
        : base(message)
    {
        OracleErrorCode = oracleErrorCode;
        BusinessCode = businessCode;
        ProcedureName = procedureName;
        OracleErrorNumber = oracleErrorNumber;
    }

    /// <summary>Normalized Oracle error number, e.g. 20007.</summary>
    public int OracleErrorNumber { get; }

    /// <summary>e.g. "ORA-20007".</summary>
    public string OracleErrorCode { get; }

    /// <summary>e.g. "ERR_MATERIAL_SHORTAGE" (docs/04-plsql-spec.md section 4).</summary>
    public string BusinessCode { get; }

    /// <summary>PL/SQL procedure that raised the error, when known.</summary>
    public string ProcedureName { get; }
}

/// <summary>
/// Maps the RAISE_APPLICATION_ERROR numbers used by package ERP_OPERATIONS
/// (-20001 .. -20009) to the business error codes documented in
/// docs/04-plsql-spec.md section 4. ODP.NET exposes OracleException.Number as a
/// positive value (ORA-20007 -> 20007), so both signs are accepted here.
/// </summary>
public static class ErpErrorMapper
{
    private static readonly IReadOnlyDictionary<int, string> BusinessCodes = new Dictionary<int, string>
    {
        [20001] = "ERR_INVALID_QTY",
        [20002] = "ERR_NOT_FOUND",
        [20003] = "ERR_INSUFFICIENT_STOCK",
        [20004] = "ERR_PO_ALREADY_COMPLETED",
        [20005] = "ERR_PO_CANCELLED",
        [20006] = "ERR_NO_ACTIVE_BOM",
        [20007] = "ERR_MATERIAL_SHORTAGE",
        [20008] = "ERR_PO_STATE_INVALID",
        [20009] = "ERR_PURCHASE_ORDER_STATE",
        // --- automation layer (package ERP_AUTOMATION, docs/04-plsql-spec.md) ---
        [20010] = "ERR_APPROVAL_REQUIRED",
        [20011] = "ERR_AUTOMATION_STATE",
        [20012] = "ERR_INVALID_INPUT",
    };

    public static int Normalize(int oraNumber) => Math.Abs(oraNumber);

    public static bool IsKnown(int oraNumber) => BusinessCodes.ContainsKey(Normalize(oraNumber));

    public static string BusinessCodeFor(int oraNumber) =>
        BusinessCodes.TryGetValue(Normalize(oraNumber), out var code) ? code : "ERR_DATABASE";

    public static string OracleCodeFor(int oraNumber) => $"ORA-{Normalize(oraNumber):D5}";
}
