// Check the claims book behaves as expected, reading it from Dataverse.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab.
//
// The module rests on one property of the data: a single hop cannot see the fraud ring, and
// following the link the last hop suggested can. This confirms it through the same functions the
// agent's tools use. No model calls, so it is free to run.
//
//   dotnet run -- verify
using System.Text.Json;

namespace FraudDesk;

public static class Verify
{
    const string Seed = "BCL-2026-0201";
    const string Innocent = "Haldane Accident Repair";
    static readonly string[] Links = { "bank_account", "phone", "postcode" };
    static readonly Dictionary<string, string> Col = new()
    {
        ["bank_account"] = "cp_bankaccount",
        ["phone"] = "cp_phone",
        ["postcode"] = "cp_postcode",
    };

    /// <summary>One hop: everyone sharing any link attribute with anyone we already know.</summary>
    static async Task<Dictionary<string, JsonElement>> ExpandAsync(
        Dictionary<string, JsonElement> known)
    {
        var outp = new Dictionary<string, JsonElement>(known);
        foreach (var attr in Links)
        {
            var values = known.Values.Select(k => Dataverse.Str(k, Col[attr]))
                              .Where(v => v != "").Distinct();
            foreach (var row in await Dataverse.ClaimantsSharingAnyAsync(attr, values))
                outp[Dataverse.Str(row, "cp_claimantref")] = row;
        }
        return outp;
    }

    public static async Task RunAsync()
    {
        Console.WriteLine($"signed in as {await Dataverse.WhoAmIAsync()}");
        var seed = await Dataverse.GetClaimAsync(Seed)
            ?? throw new Exception(
                $"{Seed} not found. The claims book has not been loaded into Dataverse.");
        var start = await Dataverse.GetClaimantAsync(Dataverse.Str(seed, "cp_claimantref"))
            ?? throw new Exception("seed claimant missing");

        Console.WriteLine($"seed claim {Seed}: {Dataverse.Str(start, "cp_fullname")} at "
                        + $"{Dataverse.Str(seed, "cp_repairername")}, "
                        + $"GBP {Dataverse.Num(seed, "cp_amountclaimed"):N0}");

        Console.WriteLine("\n--- the obvious first hop: same repairer ---");
        var rep = await Dataverse.ClaimsByRepairerAsync(
            Dataverse.Str(seed, "cp_repairername"));
        var repPeople = rep.Select(c => Dataverse.Str(c, "cp_claimantref")).Distinct().ToList();
        Console.WriteLine($"  {rep.Count} claims, {repPeople.Count} claimants. Too many to accuse.");

        Console.WriteLine("\n--- following the links ---");
        var known = new Dictionary<string, JsonElement>
        {
            [Dataverse.Str(start, "cp_claimantref")] = start,
        };
        var hops = 0;
        while (hops < 5)
        {
            var next = await ExpandAsync(known);
            if (next.Count == known.Count) break;
            hops++;
            known = next;
            Console.WriteLine($"  hop {hops}: {known.Count} claimant(s)");
        }

        var claims = await Dataverse.ClaimsOfManyAsync(known.Keys);
        Console.WriteLine($"\n  reached {known.Count} claimants over {hops} hops, "
                        + $"{claims.Count} claims, "
                        + $"GBP {claims.Sum(c => Dataverse.Num(c, "cp_amountclaimed")):N0}");

        Console.WriteLine("\n--- the control: the big innocent repairer ---");
        var inn = await Dataverse.ClaimsByRepairerAsync(Innocent);
        var innPeople = inn.Select(c => Dataverse.Str(c, "cp_claimantref")).Distinct()
                           .OrderBy(x => x).ToList();
        var one = await Dataverse.GetClaimantAsync(innPeople[0]);
        var grew = await ExpandAsync(new Dictionary<string, JsonElement>
        {
            [innPeople[0]] = one!.Value,
        });
        Console.WriteLine($"  {inn.Count} claims, {innPeople.Count} claimants at {Innocent}");
        Console.WriteLine($"  traversing from {innPeople[0]} reaches {grew.Count} claimant(s)");

        Console.WriteLine();
        (string Label, bool Ok)[] checks =
        {
            ("the repairer hop returns a haystack", repPeople.Count > 20),
            ("following the links converges", hops > 1 && hops <= 4),
            ("it reaches more than one hop's worth", known.Count > 3),
            ("the innocent cluster has volume", innPeople.Count > 30),
            ("and no links at all", grew.Count == 1),
        };
        foreach (var (label, ok) in checks)
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}]  {label}");
        Console.WriteLine("\nRESULT: " + (checks.All(c => c.Ok)
            ? "the book behaves in Dataverse as it did on disk"
            : "FAILED"));
    }
}
