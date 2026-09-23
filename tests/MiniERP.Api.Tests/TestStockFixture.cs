using System.Net.Http.Json;
using System.Text.Json;
using MiniERP.Api.Models;

namespace MiniERP.Api.Tests;

/// <summary>
/// Resets the shared demo stock to the sql/03_seed.sql baseline before every
/// integration test. All tests run against ONE Oracle schema: completing
/// orders consumes RAW material and reservations can be left ACTIVE, so
/// without this reset later tests observe negative availability and fail
/// with WAITING_MATERIAL / ORA-20007 (409). Test-only hygiene - it does not
/// change any product behaviour.
/// </summary>
internal static class TestStockFixture
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Opening balances of sql/03_seed.sql for warehouse WH_RAW.</summary>
    private static readonly (string Code, decimal Target)[] RawBaseline =
    {
        ("MAT_RUBBER_01", 40m),
        ("MAT_MESH_01", 500m),
        ("MAT_THREAD_01", 200m),
        ("MAT_GLUE_01", 100m),
        ("MAT_BOX_01", 600m),
    };

    // The ACID test asserts the FG row exists but stays below 1000 pairs.
    private const decimal FgSeedIfMissing = 100m;
    private const decimal FgCap = 500m;

    public static void Reset(HttpClient client) =>
        ResetAsync(client).GetAwaiter().GetResult();

    private static async Task ResetAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/stock/WH_RAW");
        res.EnsureSuccessStatusCode();
        var rows = await res.Content.ReadFromJsonAsync<List<StockItemDto>>(Web);
        if (rows is null)
        {
            return;
        }

        foreach (var (code, target) in RawBaseline)
        {
            var qty = rows.FirstOrDefault(r => r.ItemCode == code)?.Quantity ?? 0m;
            if (qty < target)
            {
                await StockInAsync(client, code, target - qty);
            }
        }

        var fg = rows.FirstOrDefault(r => r.ItemCode == "FG_RUNNER_PRO_42");
        if (fg is null)
        {
            await StockInAsync(client, "FG_RUNNER_PRO_42", FgSeedIfMissing);
        }
        else if (fg.Quantity > FgCap)
        {
            await StockOutAsync(client, "FG_RUNNER_PRO_42", fg.Quantity - FgSeedIfMissing);
        }
    }

    private static async Task StockInAsync(HttpClient client, string itemCode, decimal qty)
    {
        var request = new StockInRequest("WH_RAW", itemCode, qty,
            "FIXTURE_RESET_" + Guid.NewGuid().ToString("N")[..12]);
        var res = await client.PostAsJsonAsync("/api/stock/in", request, Web);
        res.EnsureSuccessStatusCode();
    }

    private static async Task StockOutAsync(HttpClient client, string itemCode, decimal qty)
    {
        var request = new StockOutRequest("WH_RAW", itemCode, qty,
            "FIXTURE_TRIM_" + Guid.NewGuid().ToString("N")[..12]);
        var res = await client.PostAsJsonAsync("/api/stock/out", request, Web);
        res.EnsureSuccessStatusCode();
    }
}