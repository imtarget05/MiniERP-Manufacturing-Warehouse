namespace MiniERP.Api.Services;

/// <summary>Deterministic label rendering (no printer integration, no stock effects).</summary>
public static class LabelRenderService
{
    public static string RenderHtml(
        string company, string itemCode, string itemName,
        string lotCode, decimal qty, string uom,
        string? location, DateTime? mfg, DateTime? expiry)
    {
        static string E(string? v) => System.Net.WebUtility.HtmlEncode(v ?? string.Empty);
        return "<div class=\"erp-label\">"
            + $"<div class=\"erp-label-company\">{E(company)}</div>"
            + $"<div class=\"erp-label-item\">{E(itemCode)} — {E(itemName)}</div>"
            + $"<div class=\"erp-label-lot\">LOT: {E(lotCode)}</div>"
            + $"<div class=\"erp-label-qty\">QTY: {qty} {E(uom)}</div>"
            + $"<div class=\"erp-label-loc\">LOC: {E(location ?? string.Empty)}</div>"
            + $"<div class=\"erp-label-dates\">MFG: {mfg:yyyy-MM-dd} EXP: {expiry:yyyy-MM-dd}</div>"
            + $"<div class=\"erp-label-code\">*{E(lotCode)}*</div>"
            + "</div>";
    }

    public static string RenderZpl(string itemCode, string lotCode, decimal qty, string uom)
    {
        return "^XA\n"
            + $"^FO50,50^ADN,36,20^FD{itemCode}^FS\n"
            + $"^FO50,100^ADN,36,20^FDLOT:{lotCode}^FS\n"
            + $"^FO50,150^ADN,36,20^FDQTY:{qty} {uom}^FS\n"
            + $"^FO50,200^BCN,100,Y,N,N^FD{lotCode}^FS\n"
            + "^XZ";
    }
}
