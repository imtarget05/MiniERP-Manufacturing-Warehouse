using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

/// <summary>
/// Phase-2 property tests for traceability lot logic (pure logic only — no
/// Oracle, no web host). Seeded <see cref="System.Random"/> generates
/// ≥200 random cases per invariant so runs are reproducible.
/// Runs under <c>Category!=Integration</c> (no Integration trait).
/// </summary>
public class TraceabilityPropertyTests
{
    private const int CaseCount = 250;
    private const int Seed = 20260927;

    private static List<TraceabilityLogic.LotCandidate> RandomLots(Random rng, int caseIndex)
    {
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int count = rng.Next(0, 12); // include empty + single-lot edge cases
        var lots = new List<TraceabilityLogic.LotCandidate>(count);
        for (int i = 0; i < count; i++)
        {
            DateTime? expiry = rng.Next(0, 5) == 0
                ? null // ~20% null-expiry (FIFO fallback branch)
                : baseDate.AddDays(rng.Next(0, 730)).AddHours(rng.Next(0, 24));
            var created = baseDate.AddDays(-rng.Next(0, 365)).AddMinutes(rng.Next(0, 1440));
            decimal qty = rng.Next(0, 101); // includes 0 available
            lots.Add(new TraceabilityLogic.LotCandidate($"L-{caseIndex:000}-{i:00}", expiry, created, qty));
        }
        return lots;
    }

    [Fact]
    public void OrderForIssue_FEFO_Invariant_Holds_ForRandomLotLists()
    {
        var rng = new Random(Seed);
        for (int c = 0; c < CaseCount; c++)
        {
            var lots = RandomLots(rng, c);

            var actual = TraceabilityLogic.OrderForIssue(lots).Select(l => l.LotCode).ToList();
            var expected = lots
                .OrderBy(l => l.ExpiryDate.HasValue ? 0 : 1) // null-expiry last
                .ThenBy(l => l.ExpiryDate ?? DateTime.MaxValue) // expiry ascending
                .ThenBy(l => l.CreatedAt)
                .ThenBy(l => l.LotCode, StringComparer.Ordinal)
                .Select(l => l.LotCode)
                .ToList();

            Assert.Equal(expected, actual);

            // Pairwise FEFO check: no later lot may "jump the queue" ahead of an
            // earlier-expiry lot.
            var ordered = TraceabilityLogic.OrderForIssue(lots);
            for (int i = 1; i < ordered.Count; i++)
            {
                int prevRank = ordered[i - 1].ExpiryDate.HasValue ? 0 : 1;
                int curRank = ordered[i].ExpiryDate.HasValue ? 0 : 1;
                Assert.True(prevRank <= curRank);
                if (prevRank == 0 && curRank == 0)
                {
                    Assert.True(ordered[i - 1].ExpiryDate!.Value <= ordered[i].ExpiryDate!.Value);
                }
            }
        }
    }

    [Fact]
    public void OnlyActiveLots_AreIssuable_ForRandomStatuses()
    {
        string?[] pool = ["ACTIVE", "active", "Active", "aCtIvE", "HOLD", "BLOCKED",
            "EXPIRED", "CONSUMED", "QUARANTINE", "REJECTED", "", "   ", null, "ACTIV", "INACTIVE"];
        var rng = new Random(Seed + 1);
        for (int c = 0; c < CaseCount; c++)
        {
            var status = pool[rng.Next(pool.Length)];
            bool expected = string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expected, TraceabilityLogic.IsIssuableLotStatus(status));
        }
    }

    [Fact]
    public void Allocation_NeverExceedsAvailableQty_ForRandomDemands()
    {
        var rng = new Random(Seed + 2);
        for (int c = 0; c < CaseCount; c++)
        {
            var lots = RandomLots(rng, c);
            decimal demand = rng.Next(0, 401);
            decimal available = lots.Sum(l => l.QtyAvailable);

            // FEFO allocation walk: take min(remaining, lotQty) in issue order.
            decimal remaining = demand;
            decimal allocated = 0;
            foreach (var lot in TraceabilityLogic.OrderForIssue(lots))
            {
                if (remaining <= 0) break;
                decimal take = Math.Min(remaining, Math.Max(0, lot.QtyAvailable));
                allocated += take;
                remaining -= take;
            }
            decimal shortfall = demand - allocated;

            Assert.True(shortfall >= 0); // allocation never exceeds available qty
            Assert.True(allocated <= available);
            Assert.Equal(demand, allocated + shortfall);
            Assert.Equal(Math.Max(0, demand - available), shortfall);
        }
    }
}
