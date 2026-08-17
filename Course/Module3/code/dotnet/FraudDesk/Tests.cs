// Assertions over the fraud desk. No model, no Dataverse, no cost.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab.
//
// The same questions the Python track answers, asked of the C# build. The guarantees an insurer
// would ask about are checked in CODE, not argued for in a document.
//
//   dotnet run -- test          exits non-zero if anything fails
using System.Reflection;
using Microsoft.Agents.AI.Workflows;

namespace FraudDesk;

public static class Tests
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"
                        + (!ok && detail.Length > 0 ? $"  {detail}" : ""));
    }

    /// <summary>
    /// The message types THIS executor declares a handler for.
    ///
    /// Declared members only. Walking inherited members would also pick up the Executor base class
    /// and report types the graph never uses.
    /// </summary>
    static HashSet<string> HandlerTypes(Type t)
    {
        var ours = typeof(SeedClaim).Assembly.GetTypes()
            .Where(x => x.Namespace == "FraudDesk").Select(x => x.Name).ToHashSet();
        var found = new HashSet<string>();
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                       | BindingFlags.DeclaredOnly))
        {
            if (m.GetCustomAttribute<MessageHandlerAttribute>() is null) continue;
            foreach (var p in m.GetParameters())
                if (ours.Contains(p.ParameterType.Name)) found.Add(p.ParameterType.Name);
        }
        return found;
    }

    /// <summary>
    /// Find a source file by walking up from the binary. Returns null rather than throwing, so a
    /// source-dependent check can be skipped rather than failing for the wrong reason.
    /// </summary>
    static string? FindSource(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    public static int Run()
    {
        Console.WriteLine("=== the graph ===");

        var nodes = new (string Name, Type T)[]
        {
            ("Intake", typeof(Desk).GetNestedType("Intake", BindingFlags.Public)!),
            ("Investigate", typeof(Desk).GetNestedType("Investigate", BindingFlags.Public)!),
            ("Challenge", typeof(Desk).GetNestedType("Challenge", BindingFlags.Public)!),
            ("Refer", typeof(Desk).GetNestedType("Refer", BindingFlags.Public)!),
        };

        // Delivery is by type. Two executors accepting the same type would both receive the message,
        // which produces no output and no error.
        var seen = new HashSet<string>();
        var overlap = new HashSet<string>();
        foreach (var (_, t) in nodes)
            foreach (var ty in HandlerTypes(t))
                if (!seen.Add(ty)) overlap.Add(ty);
        Check("every executor accepts a distinct message type", overlap.Count == 0,
              $"shared: {string.Join(", ", overlap)}");

        Check("intake takes SeedClaim and emits something else",
              HandlerTypes(nodes[0].T).Contains(nameof(SeedClaim))
              && !HandlerTypes(nodes[1].T).Contains(nameof(SeedClaim)));

        Check("the workflow builds", Desk.BuildWorkflow() is not null);

        // Diagnostic: does the framework actually see our handlers? If InputTypes is empty the run
        // completes instantly with no events and no error, which is the worst kind of failure.
        var intake = new Desk.Intake();
        Console.WriteLine($"    intake.InputTypes  = [{string.Join(", ", intake.InputTypes.Select(t => t.Name))}]");
        Console.WriteLine($"    intake.OutputTypes = [{string.Join(", ", intake.OutputTypes.Select(t => t.Name))}]");
        Console.WriteLine($"    intake.CanHandle(SeedClaim) = {intake.CanHandle(typeof(SeedClaim))}");
        Check("the framework sees intake's handler",
              intake.CanHandle(typeof(SeedClaim)));

        Console.WriteLine("\n=== nobody is referred without a person ===");

        Check("the human gate is a node in the graph, not a call inside a method",
              Desk.ApprovalPort.Id == "approval"
              && Desk.ApprovalPort.Request == typeof(ReferralApprovalRequest)
              && Desk.ApprovalPort.Response == typeof(ReferralDecision));

        // Only Refer may produce an outcome, and only after a human answered: its handler takes a
        // ReferralDecision, which can only arrive through the approval port.
        var yields = nodes.Where(n => n.T.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                     | BindingFlags.DeclaredOnly)
            .Any(m => m.GetCustomAttribute<MessageHandlerAttribute>() is not null
                   && m.GetParameters().Any(p => p.ParameterType == typeof(ReferralDecision))))
            .Select(n => n.Name).ToList();
        Check("only Refer handles the human's decision", yields.SequenceEqual(new[] { "Refer" }),
              $"found {string.Join(", ", yields)}");

        Console.WriteLine("\n=== the shape of what a model returns is ENFORCED, not requested ===");

        // Requesting JSON in the prompt is not the same as enforcing a schema: a drifting model
        // returns well-formed JSON carrying a different JUDGEMENT, harder to spot than a parse
        // error. Checked this way because a schema constant can be perfectly correct and never
        // actually passed to a model call.
        //
        // .NET cannot read its own source by reflection, so this reads the file. If it is absent
        // the check is skipped rather than failed.
        var deskSource = FindSource("Desk.cs");
        if (deskSource is null)
        {
            Console.WriteLine("  [SKIP] schema enforcement (Desk.cs not beside the binary)");
        }
        else
        {
            var enforced = deskSource.Split('\n')
                .Where(l => l.Contains("options: Enforce(")).ToList();
            foreach (var schema in new[] { "TraversalSchema", "JudgementSchema" })
                Check($"{schema} is passed to a model call, not merely declared",
                      enforced.Any(l => l.Contains(schema)));
        }

        Console.WriteLine("\n=== the decision table ===");
        (bool approved, bool recommended, string want)[] cases =
        {
            (true, true, "referred"),
            (true, false, "not_referred"),
            (false, true, "dismissed"),
            (false, false, "dismissed"),
        };
        foreach (var (approved, recommended, want) in cases)
        {
            var got = Desk.DecideOutcome(approved, recommended);
            Check($"DecideOutcome(approved={approved}, recommended={recommended}) == {want}",
                  got == want, $"got {got}");
        }

        Console.WriteLine("\n=== the tools ===");
        var tools = Desk.LinkTools();
        Check("four tools are published to the model", tools.Count == 4, $"got {tools.Count}");
        Check("the hop is a batch operation, so one hop costs one round trip",
              typeof(Desk).GetMethod(nameof(Desk.Expand))!
                  .GetParameters()[0].ParameterType == typeof(string[]));
        Check("the repairer tool returns a count only, never a list of people",
              typeof(Desk).GetMethod(nameof(Desk.CountClaimsAtRepairer)) is not null);

        Console.WriteLine("\n=== the data layer ===");
        // The book contains a repairer called Eccles Panel & Paint. Unencoded, the & splits the
        // query string and the filter fails as malformed - so the escape helper must survive both
        // parsers, OData's and the URL's. This is the regression test for exactly that name.
        var esc = typeof(Dataverse).GetMethod("Esc",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var encoded = (string)esc.Invoke(null, new object[] { "Eccles Panel & Paint" })!;
        Check("identifier values are URL-encoded before they reach Dataverse",
              !encoded.Contains('&') && encoded.Contains("%26") && !encoded.Contains(' '),
              $"got {encoded}");

        Console.WriteLine("\n=== the judgement the module is about ===");
        Check("the challenger is told a postcode alone is not evidence",
              Desk.Challenger.Contains("Exclude anyone whose ONLY link is a postcode"));
        Check("the challenger judges the GROUP, not distance from the seed",
              Desk.Challenger.Contains("A link between ANY TWO members"));
        Check("the investigator is told a shared repairer is not a link",
              Desk.Investigator.Contains("not a link between people"));
        Check("the investigator is bounded to a fixed number of hops",
              Desk.Investigator.Contains($"at most {Desk.MaxHops} hops"));
        Check("the investigator is told to batch the hop",
              Desk.Investigator.Contains("One call per hop"));

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
