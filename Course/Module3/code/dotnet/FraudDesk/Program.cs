// Drive the fraud desk, one process at a time.
//
// Every command below is a SEPARATE OS PROCESS holding nothing in memory from the last one. If
// `approve` works, the durability is real. If it did not, we would have to say so.
//
//   dotnet run -- login                          sign in as yourself, once
//   dotnet run -- start                          investigate the seeded claim, stop at the gate
//   dotnet run -- start --claim BCL-2026-0007    investigate a different claim
//   dotnet run -- status                         what is waiting, read from disk only
//   dotnet run -- approve --by "K Bell"          approve, and write the referral to Dataverse
//   dotnet run -- decline --by "K Bell"          dismiss it, and record that too
//   dotnet run -- mine                           every referral YOU have written
//   dotnet run -- test                           offline assertions, no model, no cost
//   dotnet run -- verify                         re-measure the book through Dataverse
using System.Globalization;
using FraudDesk;

// Pin the culture for output. Without it, "GBP 3,100" prints as "GBP 3.100" wherever the locale
// uses a dot for thousands, so identical code would report different figures on different machines.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

const string DefaultSeed = "BCL-2026-0201";
var checkpointDir = Path.Combine(AppContext.BaseDirectory, "_checkpoints");
var sessionFile = Path.Combine(checkpointDir, "session.txt");

/// <summary>Read `--name value` off the command line, or return the fallback.</summary>
string Arg(string name, string fallback = "")
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

if (args.Length == 0) { Usage(); return; }

// A missing sign-in is an ordinary thing, not a crash. Without this the message arrives wrapped in
// a stack trace, which reads like a broken program rather than a step you have not done yet.
try
{
    await Dispatch();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {ex.Message}");
    Console.Error.WriteLine();
    if (Environment.GetEnvironmentVariable("M3_TRACE") == "1") Console.Error.WriteLine(ex);
    else Console.Error.WriteLine("  (set M3_TRACE=1 for the full stack trace)");
    Environment.Exit(1);
}
return;

/// <summary>Route the first argument to a command. Each one is a whole process, start to finish.</summary>
async Task Dispatch()
{
switch (args[0])
{
    case "test": Environment.Exit(Tests.Run()); return;
    case "login": await Dataverse.DeviceLoginAsync(); await AfterLogin(); return;
    case "verify": await Verify.RunAsync(); return;
    case "start": await Start(Arg("--claim", DefaultSeed)); return;
    case "status": await Status(); return;
    case "approve": await Decide(true); return;
    case "decline": await Decide(false); return;
    case "mine": await Mine(); return;
    default: Usage(); return;
}
}

/// <summary>What to type. Printed for no arguments and for anything unrecognised.</summary>
void Usage()
{
    Console.WriteLine("usage: dotnet run -- <login|start|status|approve|decline|mine|test|verify>");
    Console.WriteLine("  approve and decline require --by \"Your Name\"");
}

/// <summary>
/// Prove the sign-in reached Dataverse, not just Entra. A token can be issued perfectly and still
/// read nothing if the role grant is missing - which would otherwise surface much later as a
/// traversal that finds nobody.
/// </summary>
async Task AfterLogin()
{
    Console.WriteLine($"\nyou are {await Dataverse.WhoAmIAsync()}");
    var n = await Dataverse.ClaimantsSharingAnyAsync("postcode", new[] { "M20 4WX" });
    Console.WriteLine($"claims book reachable: {n.Count} claimant(s) at the test postcode");
    Console.WriteLine("\nnext:  dotnet run -- verify");
}


/// <summary>
/// Investigate a claim from scratch, and stop at the human gate. Old checkpoints are cleared first,
/// which discards nothing real - referrals already written are rows, not checkpoints. The session
/// id is written to disk immediately, because a LATER process needs it to find this checkpoint.
/// </summary>
async Task Start(string claimRef)
{
    Directory.CreateDirectory(checkpointDir);
    var stale = Directory.GetFiles(checkpointDir);
    if (stale.Length > 0)
        Console.WriteLine($"(clearing {stale.Length} checkpoint file(s) from a previous "
                        + "investigation. Referrals already written to Dataverse are not affected.)");
    foreach (var f in stale) File.Delete(f);

    Console.WriteLine($"=== FRAUD DESK  {claimRef} ===");
    Console.WriteLine($"    signed in as {await Dataverse.WhoAmIAsync()}\n");

    var sessionId = Guid.NewGuid().ToString();
    File.WriteAllText(sessionFile, sessionId);

    await Resume.StartAsync(checkpointDir, sessionId, new SeedClaim(claimRef));
    Report();
    // A step that threw is reported as an event rather than an exception, so exiting 0 here would
    // tell a script the investigation succeeded after printing why it did not.
    if (Resume.Failed) Environment.Exit(1);
    await Status();
}

/// <summary>
/// What is waiting, read from disk in a process that never ran the investigation. The
/// demonstration, not a convenience: it costs nothing, because a suspended workflow is just a file.
/// </summary>
async Task Status()
{
    Console.WriteLine("\n=== STATUS (read from disk, nothing in memory) ===");
    if (!File.Exists(sessionFile))
    {
        Console.WriteLine("  no checkpoint. run 'start' first.");
        return;
    }
    var sessionId = File.ReadAllText(sessionFile).Trim();
    var files = Directory.Exists(checkpointDir) ? Directory.GetFiles(checkpointDir) : [];
    Console.WriteLine($"  checkpoint files : {files.Length}");
    Console.WriteLine($"  session          : {sessionId}");
    var pending = Pending.Read(checkpointDir);
    if (pending is null)
    {
        Console.WriteLine("  pending          : none. The investigation is finished.");
        return;
    }
    Console.WriteLine($"  pending          : a REFERRAL DECISION on {pending.SeedClaimRef}");
    Console.WriteLine($"    claimants      : {pending.ClaimantCount}");
    Console.WriteLine($"    claims         : {pending.ClaimCount}");
    Console.WriteLine($"    exposure       : GBP {pending.ExposureGbp:N0}");
    Console.WriteLine($"    would refer    : {string.Join(", ", pending.ConfirmedRefs)}");
    Console.WriteLine($"    ruled out      : {string.Join(", ", pending.ExcludedRefs)}");
    Console.WriteLine("    -> dotnet run -- approve --by \"Your Name\"");
    Console.WriteLine("    -> dotnet run -- decline --by \"Your Name\"");
    await Task.CompletedTask;
}

/// <summary>
/// Approve or decline whatever is pending, from a process that never started the investigation.
/// The durability claim: rebuild the graph, hand it the checkpoint, answer the re-emitted request.
/// </summary>
async Task Decide(bool approved)
{
    var by = Arg("--by");
    // The module claims nothing is referred without a NAMED human. Enforce it here rather than
    // defaulting to a placeholder: a referral raised by "unnamed" would make that claim false.
    if (string.IsNullOrWhiteSpace(by))
    {
        Console.Error.WriteLine(
            $"--by is required for '{args[0]}'. Nobody is accused without a named human.\n"
            + $"  e.g.  dotnet run -- {args[0]} --by \"K. Bell (fraud desk)\"");
        Environment.Exit(2);
    }
    if (!File.Exists(sessionFile))
    {
        Console.WriteLine("nothing is waiting for a decision. run 'status'.");
        return;
    }
    var sessionId = File.ReadAllText(sessionFile).Trim();
    Console.WriteLine($"=== RESUMING session {sessionId} in a NEW process ===\n");

    await Resume.AnswerAsync(checkpointDir, sessionId,
        new ReferralDecision(approved, by, Arg("--note")));
    Report();
    if (Resume.Failed) Environment.Exit(1);
}

/// <summary>
/// Print the workflow's final output, if it produced one. Silent when there is none, which is the
/// NORMAL case for `start`: the desk suspends at the gate and has deliberately not finished.
/// </summary>
void Report()
{
    var outcome = Outcomes.Last;
    if (outcome is null) return;
    Console.WriteLine("\n=== OUTCOME ===");
    Console.WriteLine($"  seed claim   : {outcome.SeedClaimRef}");
    Console.WriteLine($"  decision     : {outcome.Decision}");
    Console.WriteLine($"  claimants    : {outcome.ClaimantCount}");
    Console.WriteLine($"  claims       : {outcome.ClaimCount}");
    Console.WriteLine($"  exposure     : GBP {outcome.ExposureGbp:N0}");
    Console.WriteLine($"  hops         : {outcome.Hops}");
    Console.WriteLine($"  approver     : {outcome.Approver}");
    Console.WriteLine($"  referral     : {outcome.ReferralRef}");
    Console.WriteLine("  audit trail  :");
    foreach (var line in outcome.AuditTrail) Console.WriteLine($"    - {line}");
    Console.WriteLine("\n  Open it in Dataverse:");
    Console.WriteLine($"  {Dataverse.Org}/main.aspx?pagetype=entitylist&etn=cp_fraudreferral");
}

/// <summary>
/// Every referral you have written, filtered by your own UPN. Nobody's row is hidden and nobody can
/// edit anybody else's, so "mine" is a filter rather than a race.
/// </summary>
async Task Mine()
{
    var rows = await Dataverse.MyReferralsAsync();
    Console.WriteLine($"\n=== REFERRALS WRITTEN BY {await Dataverse.WhoAmIAsync()} ===");
    if (rows.Count == 0) { Console.WriteLine("  none yet."); return; }
    Console.WriteLine($"  {"referral",-40} {"decision",-14} {"people",6} {"claims",6} "
                    + $"{"exposure",12}  built with");
    foreach (var r in rows)
        Console.WriteLine($"  {Dataverse.Str(r, "cp_referralref"),-40} "
                        + $"{Dataverse.Str(r, "cp_decision"),-14} "
                        + $"{Dataverse.Num(r, "cp_ringsize"),6:N0} "
                        + $"{Dataverse.Num(r, "cp_claimcount"),6:N0} "
                        + $"{Dataverse.Num(r, "cp_exposure"),12:N0}  "
                        + Dataverse.Str(r, "cp_language"));
    Console.WriteLine($"\n  {rows.Count} referral(s). Everyone else's are separate rows; yours are");
    Console.WriteLine("  the ones stamped with your name.");
}
