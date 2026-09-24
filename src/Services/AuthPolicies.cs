namespace MiniERP.Api.Services;

/// <summary>Role policy names shared by Program.cs and tests.</summary>
public static class AuthPolicies
{
    public const string WarehouseMutation = "warehouse-mutation";
    public const string ProductionMutation = "production-mutation";
    public const string MaterialIssue = "material-issue";
    public const string LotHold = "lot-hold";
    public const string LabelMutation = "label-mutation";
    public const string SupportMutation = "support-mutation";
    public const string OperationsMutation = "operations-mutation";
    public const string ProcurementMutation = "procurement-mutation";
    public const string AdminOnly = "admin-only";
    public const string SupportDetails = "support-details";
}

/// <summary>Authorization roles from the upgrade specification.</summary>
public static class ErpRoles
{
    // Canonical Phase 2 roles
    public const string CanonicalAdmin = "ADMIN";
    public const string Planner = "PLANNER";
    public const string CanonicalWarehouse = "WAREHOUSE";
    public const string Procurement = "PROCUREMENT";
    public const string CanonicalSupport = "SUPPORT";
    public const string Auditor = "AUDITOR";

    // Legacy names (used by tests and existing database seeds)
    public const string Admin = "ERP_ADMIN";
    public const string Warehouse = "WAREHOUSE_OPERATOR";
    public const string Production = "PRODUCTION_OPERATOR";
    public const string Support = "ERP_SUPPORT";
    public const string Viewer = "VIEWER";
}
