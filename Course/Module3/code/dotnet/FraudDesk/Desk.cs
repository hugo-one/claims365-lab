// Claims 365 Fraud Desk - Module 3 (Microsoft Agent Framework, .NET).
//
// =======================================================================================
// DEMONSTRATION CODE for a fictional insurer, written to be READ: it shows how a workflow,
// agents, tools and a human approval gate fit together, in as few lines as the idea allows.
//
// It is NOT production code. A real fraud desk would add secret management, idempotent
// writes, structured logging and telemetry, data protection controls, and ongoing
// evaluation of the model's judgements. Take the shape away with you, not the file.
// =======================================================================================
//
// The same single artefact as the Python track: a WORKFLOW whose nodes are AGENTS WITH TOOLS, which
// is Microsoft's own recommended production shape.
//
//     "A workflow defines the high-level process, and individual executors within that workflow use
//      agents for the steps that benefit from LLM reasoning."
//     https://learn.microsoft.com/agent-framework/journey/workflows
//
//     intake -> investigate -> challenge -> [approval port] -> refer
//     (code)   (agent+tools)  (agent)      (a human)          (code + Dataverse)
//
// AN EXECUTOR MUST BE A `partial` CLASS DERIVING FROM `Executor`. `ReflectingExecutor<T>` is
// obsolete: it compiles and builds a workflow but discovers NO handlers, so the run completes
// instantly with no events and no error. Dispatch is generated at compile time, hence `partial`.
//
// ONE REAL DIFFERENCE FROM PYTHON: .NET makes the human gate a NODE, `RequestPort.Create<TReq,
// TResp>(id)`, with edges in and out. So this graph has five nodes to Python's four, and the gate
// is visible in the topology rather than buried inside a method.
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace FraudDesk;

public static partial class Desk
{
    public const int MaxHops = 3;
    public const string Language = "dotnet";
    public const string WorkflowName = "claims365-fraud-desk";

    // Python's `ctx.set_state` is shared WORKFLOW state. .NET's `QueueStateUpdateAsync` writes to
    // the CALLING EXECUTOR'S scope by default, so anything handed between executors must name an
    // explicit scope - otherwise the reader finds nothing, and only after a resume.
    public const string Shared = "desk";

    // Every tool call the MODEL chose to make, recorded so the run can print it. "The agent decided
    // to look something up" is a claim you should be able to check rather than take on trust.
    public static readonly List<string> ToolLog = new();

    // ----------------------------------------------------------------- the tools
    //
    // The [Description] on each tool is PROMPT, not documentation: it is what the model reads when
    // deciding whether to call one, so rewording it changes the agent's behaviour.
    //
    // WHY `Expand` TAKES AN ARRAY. An agent's cost is dominated by ROUND TRIPS, not tool calls,
    // because every trip re-sends the whole conversation. A hop is a set operation, so batching it
    // into one call is several times cheaper for the same answer - and the model still decides
    // whether to hop, who to expand and when to stop.
    /// <summary>
    /// ONE HOP. Given everyone found so far, return everyone newly connected and say how.
    ///
    /// Three queries whatever the size of the set. `shared_with` records WHICH known person each
    /// new one connects to, because the challenge step judges links between members.
    ///
    /// Returns an error object rather than throwing: an exception inside a tool kills the run,
    /// whereas a returned error lets the model notice its own bad argument and correct itself.
    /// </summary>
    [Description("Follow every shared identifier out from a set of claimants, in one step. Give it "
               + "every claimant you know about; it returns who else shares a bank account, phone "
               + "or postcode with any of them, and says which. This is one hop.")]
    public static async Task<string> Expand(
        [Description("every claimant reference found so far")] string[] claimantRefs)
    {
        ToolLog.Add($"expand({claimantRefs.Length} claimant(s))");
        var known = await Dataverse.ClaimantsByRefsAsync(claimantRefs);
        if (known.Count == 0) return JsonSerializer.Serialize(new { error = "no such claimants" });

        var byAttr = new Dictionary<string, string>
        {
            ["bank_account"] = "cp_bankaccount",
            ["phone"] = "cp_phone",
            ["postcode"] = "cp_postcode",
        };
        var seen = known.Select(k => Dataverse.Str(k, "cp_claimantref")).ToHashSet();
        var found = new Dictionary<string, Dictionary<string, object>>();

        foreach (var (attr, col) in byAttr)
        {
            var values = known.Select(k => Dataverse.Str(k, col)).Where(v => v != "").Distinct();
            foreach (var row in await Dataverse.ClaimantsSharingAnyAsync(attr, values))
            {
                var reference = Dataverse.Str(row, "cp_claimantref");
                if (seen.Contains(reference)) continue;
                var value = Dataverse.Str(row, col);
                var sharedWith = known.Where(k => Dataverse.Str(k, col) == value)
                                      .Select(k => Dataverse.Str(k, "cp_claimantref")).ToList();
                if (!found.TryGetValue(reference, out var entry))
                {
                    entry = new Dictionary<string, object>
                    {
                        ["claimant_ref"] = reference,
                        ["full_name"] = Dataverse.Str(row, "cp_fullname"),
                        ["links"] = new List<object>(),
                    };
                    found[reference] = entry;
                }
                ((List<object>)entry["links"]).Add(
                    new { attribute = attr, value, shared_with = sharedWith });
            }
        }
        return JsonSerializer.Serialize(new
        {
            queried = known.Count,
            new_claimants = found.Values.ToList(),
            new_count = found.Count,
        });
    }

    /// <summary>Identifiers for people the model already knows about, without hopping.</summary>
    [Description("Identifiers held by a set of claimants: bank account, phone, postcode.")]
    public static async Task<string> GetClaimants(string[] claimantRefs)
    {
        ToolLog.Add($"get_claimants({claimantRefs.Length})");
        var rows = await Dataverse.ClaimantsByRefsAsync(claimantRefs);
        return JsonSerializer.Serialize(new
        {
            claimants = rows.Select(c => new
            {
                claimant_ref = Dataverse.Str(c, "cp_claimantref"),
                full_name = Dataverse.Str(c, "cp_fullname"),
                bank_account = Dataverse.Str(c, "cp_bankaccount"),
                phone = Dataverse.Str(c, "cp_phone"),
                postcode = Dataverse.Str(c, "cp_postcode"),
            }),
        });
    }

    /// <summary>
    /// The claims behind a set of claimants, so the model can see scale. It may quote these
    /// figures, which is harmless: the number a human sees is summed from the same rows in C#.
    /// </summary>
    [Description("Claims belonging to a set of claimants.")]
    public static async Task<string> ListClaimsFor(string[] claimantRefs)
    {
        ToolLog.Add($"list_claims_for({claimantRefs.Length})");
        var rows = await Dataverse.ClaimsOfManyAsync(claimantRefs);
        return JsonSerializer.Serialize(new
        {
            count = rows.Count,
            claims = rows.Select(r => new
            {
                claim_ref = Dataverse.Str(r, "cp_bookclaimref"),
                claimant_ref = Dataverse.Str(r, "cp_claimantref"),
                repairer = Dataverse.Str(r, "cp_repairername"),
                amount_gbp = Dataverse.Num(r, "cp_amountclaimed"),
            }),
        });
    }

    /// <summary>
    /// A COUNT, deliberately. The seed claim's repairer has 42 claims across 36 people whose only
    /// connection is a garage; returning that list would drown the traversal. What a tool REFUSES
    /// to return shapes behaviour as much as what it returns.
    /// </summary>
    [Description("How many claims a repairer handled. A COUNT only, because a shared repairer is "
               + "not a link between people.")]
    public static async Task<string> CountClaimsAtRepairer(string repairerName)
    {
        ToolLog.Add($"count_claims_at_repairer('{repairerName}')");
        var rows = await Dataverse.ClaimsByRepairerAsync(repairerName);
        return JsonSerializer.Serialize(new
        {
            repairer = repairerName,
            claim_count = rows.Count,
            distinct_claimants = rows.Select(r => Dataverse.Str(r, "cp_claimantref"))
                                     .Distinct().Count(),
        });
    }

    /// <summary>
    /// The four tools published to the investigator, with the names the model sees. The explicit
    /// second argument matters: without it each tool is named after its C# method.
    ///
    /// UNLIKE PYTHON, THERE IS NO PER-TOOL CALL CEILING HERE - `AIFunctionFactory` has no
    /// equivalent of `max_invocations`, so the bound is the instruction alone.
    /// </summary>
    public static IList<AITool> LinkTools() => new List<AITool>
    {
        AIFunctionFactory.Create(Expand, "expand"),
        AIFunctionFactory.Create(GetClaimants, "get_claimants"),
        AIFunctionFactory.Create(ListClaimsFor, "list_claims_for"),
        AIFunctionFactory.Create(CountClaimsAtRepairer, "count_claims_at_repairer"),
    };

    // ----------------------------------------------------------------- the agents
    public const string Investigator = """
You are a claims investigator at Contoso Insurance, a fictional insurer used for training. You have
been handed one claim that triage flagged.

Your job is to find out whether that claim is part of an ORGANISED GROUP, by following identifiers
that separate households do not normally share.

How to work:
- Call `expand` with EVERY claimant you know about so far. That is one hop, and it returns everyone
  newly connected plus the identifier that connects them.
- If a hop finds someone new, hop again with the enlarged set. Stop after at most 3 hops, or as soon
  as a hop returns nobody new.
- Do not call `expand` one person at a time. Pass the whole set. One call per hop.
- A shared REPAIRER is not a link between people. A busy garage serves hundreds of unconnected
  customers. Check its volume if you like, but never treat it as evidence of a connection.

Report every claimant you reached, including the one you started from, and say which identifier
brought each of them in, and who they share it with. Report candidates, not conclusions. Somebody
else decides what they mean.

Reply with JSON only, in this shape:
{"hops": <int>, "narrative": "<3-4 sentences>", "claimants": [
  {"claimant_ref": "...", "full_name": "...", "why_linked": "...",
   "link_type": "seed|bank_account|phone|postcode", "shared_with": ["CLT-...."]}]}
""";

    public const string Challenger = """
You are the fraud desk's challenge officer at Contoso Insurance, a fictional insurer used for
training. An investigator has handed you a list of claimants reached by following shared
identifiers. Your job is to ARGUE AGAINST referring them, and to concede only what survives.

You are judging a GROUP, not a list of people each measured against the seed. These claimants form a
chain: A shares an account with B, B shares a phone with C, and so on. A link between ANY TWO members
counts, and it does not matter whether the seed claimant is one of them. Strength is a property of
the identifier, never of how close someone sits to the starting claim.

Weigh each identifier honestly:
- A shared BANK ACCOUNT between any two claimants is close to conclusive. Separate households do not
  receive settlements into one account. A second, different shared account inside the same group is
  more evidence, not less.
- A shared PHONE number between any two claimants is strong. It is occasionally innocent, so say
  when you are unsure, but it is not grounds to exclude on its own.
- A shared POSTCODE is close to worthless on its own. A UK postcode covers a terrace or a block of
  flats, so neighbours share one constantly. Exclude anyone whose ONLY link is a postcode.
- A shared repairer is not a link between people at all.

So: CONFIRM anyone holding at least one strong link to any other member of the group. EXCLUDE anyone
whose only connection to the group is a shared postcode.

Refer only if at least three claimants survive that test. Excluding an innocent person matters as
much as catching a guilty one: a fraud referral follows someone for years.

Reply with JSON only, in this shape:
{"recommend_refer": <bool>, "rationale": "...",
 "confirmed": [{"claimant_ref": "...", "reason": "...", "strength": "strong|weak"}],
 "excluded":  [{"claimant_ref": "...", "reason": "..."}]}
""";

    /// <summary>
    /// A chat client for the Foundry deployment, configured from `lab/.env`. The plain OpenAI
    /// client, NOT the Azure one: gpt-5-mini rejects dated `api-version` values, so the
    /// OpenAI-compatible `/openai/v1/` route is the one that works.
    /// </summary>
    public static IChatClient ChatClient()
    {
        var (endpoint, key, model) = Env.Foundry();
        var client = new OpenAIClient(new ApiKeyCredential(key),
                                      new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        return client.GetChatClient(model).AsIChatClient();
    }

    /// <summary>
    /// The traversal agent: instructions plus the four link tools. Built fresh per call, so each
    /// investigation starts with an empty conversation rather than the last run's history.
    /// </summary>
    public static AIAgent InvestigatorAgent() =>
        ChatClient().AsAIAgent(Investigator, "investigator", tools: LinkTools());

    /// <summary>
    /// The adversarial agent. NO TOOLS, on purpose: it judges evidence it has been handed, and
    /// letting it find its own would remove the separation the step exists to create.
    /// </summary>
    public static AIAgent ChallengerAgent() =>
        ChatClient().AsAIAgent(Challenger, "challenge-officer");

    // ENFORCE THE SCHEMA RATHER THAN ASKING FOR IT IN THE PROMPT. A model that drifts from a
    // merely REQUESTED shape returns well-formed JSON carrying a different JUDGEMENT - dropping a
    // real member, or confirming everyone. A parse error is obvious; a plausible wrong answer is
    // not.
    /// <summary>Wrap a JSON schema as run options the deployment will enforce server-side.</summary>
    static ChatClientAgentRunOptions Enforce(string schemaName, string schema) =>
        new(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(schema),
                schemaName),
        });

    public const string TraversalSchema = """
    {"type":"object","additionalProperties":false,
     "required":["claimants","hops","narrative"],
     "properties":{
       "hops":{"type":"integer"},
       "narrative":{"type":"string"},
       "claimants":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","full_name","why_linked","link_type","shared_with"],
         "properties":{
           "claimant_ref":{"type":"string"},
           "full_name":{"type":"string"},
           "why_linked":{"type":"string"},
           "link_type":{"type":"string","enum":["seed","bank_account","phone","postcode"]},
           "shared_with":{"type":"array","items":{"type":"string"}}}}}}}
    """;

    public const string JudgementSchema = """
    {"type":"object","additionalProperties":false,
     "required":["confirmed","excluded","recommend_refer","rationale"],
     "properties":{
       "recommend_refer":{"type":"boolean"},
       "rationale":{"type":"string"},
       "confirmed":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","reason","strength"],
         "properties":{"claimant_ref":{"type":"string"},"reason":{"type":"string"},
                       "strength":{"type":"string","enum":["strong","weak"]}}}},
       "excluded":{"type":"array","items":{
         "type":"object","additionalProperties":false,
         "required":["claimant_ref","reason"],
         "properties":{"claimant_ref":{"type":"string"},"reason":{"type":"string"}}}}}}
    """;

    // ----------------------------------------------------------------- 1. intake
    public sealed partial class Intake() : Executor("intake")
    {
        // The 1.17.0 way: declare the protocol explicitly. The source generator that would
        // write this for you does not ship in this package, and `ReflectingExecutor` discovers
        // nothing while looking like it works.
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
            builder.SendsMessage<VerifiedClaim>()
                   .ConfigureRoutes(r => r.AddHandler<SeedClaim>(HandleAsync));

        /// <summary>
        /// Turn a reference into a verified claim, or refuse. A rule, not a judgement, so it is
        /// code: a model asked "does this claim exist?" can be argued with, a lookup cannot.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(SeedClaim seed, IWorkflowContext ctx)
        {
            var row = await Dataverse.GetClaimAsync(seed.ClaimRef)
                ?? throw new InvalidOperationException(
                    $"{seed.ClaimRef} is not in the claims book. The fraud desk only investigates "
                    + "claims that exist; it does not take a reference on trust.");
            var claim = row;
            var v = new VerifiedClaim(
                Dataverse.Str(claim, "cp_bookclaimref"),
                Dataverse.Str(claim, "cp_claimantref"),
                Dataverse.Str(claim, "cp_repairername"),
                Dataverse.Num(claim, "cp_amountclaimed"));
            Console.WriteLine($"[intake]      {v.ClaimRef}  claimant={v.ClaimantRef}  "
                            + $"repairer={v.Repairer}  GBP {v.AmountClaimed:N0}");
            await ctx.QueueStateUpdateAsync("seed", v, Shared);
            await ctx.QueueStateUpdateAsync("trail",
                new List<string> { $"intake: accepted {v.ClaimRef} from triage" }, Shared);
            await ctx.SendMessageAsync(v);
        }
    }

    // ----------------------------------------------------------------- 2. investigate
    public sealed partial class Investigate() : Executor("investigate")
    {
        // The 1.17.0 way: declare the protocol explicitly. The source generator that would
        // write this for you does not ship in this package, and `ReflectingExecutor` discovers
        // nothing while looking like it works.
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
            builder.SendsMessage<TraversalComplete>()
                   .ConfigureRoutes(r => r.AddHandler<VerifiedClaim>(HandleAsync));

        /// <summary>
        /// Let the model plan and run the traversal, then hand on CANDIDATES, not conclusions.
        ///
        /// The node that justifies the whole tier: the second hop's arguments are the first hop's
        /// results, which no fixed query can express.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(VerifiedClaim seed, IWorkflowContext ctx)
        {
            ToolLog.Clear();
            // Hand the agent what the graph already knows. Making the model fetch the seed
            // claimant's identifiers costs two round trips, and a round trip is the unit this bill
            // is denominated in.
            var start = await Dataverse.GetClaimantAsync(seed.ClaimantRef);
            // Mirrors the Python track's soft path: a claim whose claimant row is missing still
            // produces an investigable opening, with placeholder identifiers, not a crash.
            string Ident(string col, string fallback) =>
                start is { } s ? Dataverse.Str(s, col) : fallback;
            var opening =
                $"Seed claim {seed.ClaimRef}, claimant {seed.ClaimantRef} "
                + $"({Ident("cp_fullname", "unknown")}), "
                + $"repairer {seed.Repairer}, GBP {seed.AmountClaimed:N0}.\n"
                + $"Their identifiers: bank {Ident("cp_bankaccount", "?")}, "
                + $"phone {Ident("cp_phone", "?")}, "
                + $"postcode {Ident("cp_postcode", "?")}.\n\n"
                + $"Begin by calling expand with [\"{seed.ClaimantRef}\"]. "
                + "Find out who else is connected.";

            var result = await InvestigatorAgent().RunAsync(
                opening, options: Enforce("traversal", TraversalSchema));
            var outJson = Json.Object(result.Text);

            var cands = outJson.GetProperty("claimants").EnumerateArray().Select(c =>
                new LinkedClaimant(
                    c.GetProperty("claimant_ref").GetString()!,
                    c.GetProperty("full_name").GetString()!,
                    c.GetProperty("why_linked").GetString()!,
                    c.GetProperty("link_type").GetString()!,
                    c.TryGetProperty("shared_with", out var sw)
                        ? sw.EnumerateArray().Select(x => x.GetString()!).ToList()
                        : new List<string>())).ToList();
            var hops = outJson.GetProperty("hops").GetInt32();

            Console.WriteLine($"[investigate] the model made {ToolLog.Count} tool call(s):");
            foreach (var t in ToolLog) Console.WriteLine($"                -> {t}");
            Console.WriteLine($"[investigate] reached {cands.Count} claimant(s) over {hops} hop(s)");

            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            trail.Add($"investigate: {cands.Count} candidates over {hops} hops, "
                    + $"{ToolLog.Count} tool calls");
            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.QueueStateUpdateAsync("tool_log", ToolLog.ToList(), Shared);
            await ctx.SendMessageAsync(new TraversalComplete(
                seed.ClaimRef, cands, hops,
                outJson.GetProperty("narrative").GetString() ?? ""));
        }
    }

    // ----------------------------------------------------------------- 3. challenge
    public sealed partial class Challenge() : Executor("challenge")
    {
        // The 1.17.0 way: declare the protocol explicitly. The source generator that would
        // write this for you does not ship in this package, and `ReflectingExecutor` discovers
        // nothing while looking like it works.
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
            builder.SendsMessage<ReferralApprovalRequest>()
                   .ConfigureRoutes(r => r.AddHandler<TraversalComplete>(HandleAsync));

        /// <summary>
        /// Argue against the traversal, keep what survives, price it in C#, and raise the gate.
        ///
        /// The traversal reaches nine; six are the ring, three share only a postcode. Unlike
        /// Python, this node also emits the approval REQUEST, because the gate is a separate node -
        /// so the proposal is parked in shared state for a Refer that runs in another process.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(TraversalComplete t, IWorkflowContext ctx)
        {
            var listing = string.Join("\n", t.Candidates.Select(c =>
                $"  {c.ClaimantRef}  {c.FullName}  shares {c.LinkType} with "
                + $"{(c.SharedWith.Count > 0 ? string.Join(", ", c.SharedWith) : "(the seed claim)")}"
                + $"  -- {c.WhyLinked}"));

            // Schema enforced, not requested - see the note on Enforce above. This step decides
            // who gets accused, so it is the last place to take a shape on trust.
            var result = await ChallengerAgent().RunAsync(
                $"Seed claim {t.SeedClaimRef}.\nThe investigator reached these claimants over "
                + $"{t.Hops} hops:\n{listing}\n\nWhich of them would you actually refer?",
                options: Enforce("judgement", JudgementSchema));
            var outJson = Json.Object(result.Text);

            var confirmed = outJson.GetProperty("confirmed").EnumerateArray().Select(c =>
                new JudgedClaimant(c.GetProperty("claimant_ref").GetString()!,
                                   c.GetProperty("reason").GetString() ?? "",
                                   c.GetProperty("strength").GetString() ?? "strong")).ToList();
            var excluded = outJson.GetProperty("excluded").EnumerateArray().Select(c =>
                new JudgedClaimant(c.GetProperty("claimant_ref").GetString()!,
                                   c.GetProperty("reason").GetString() ?? "", "weak")).ToList();

            // THE MONEY IS COMPUTED HERE, IN C#, FROM THE RECORDS. The model decides WHO. It never
            // decides HOW MUCH. Every figure that reaches a human is a sum over rows that exist.
            var rows = await Dataverse.ClaimsOfManyAsync(confirmed.Select(c => c.ClaimantRef));
            var claimRefs = rows.Select(r => Dataverse.Str(r, "cp_bookclaimref")).ToList();
            var exposure = rows.Sum(r => Dataverse.Num(r, "cp_amountclaimed"));
            var recommend = outJson.GetProperty("recommend_refer").GetBoolean();

            Console.WriteLine($"[challenge]   confirmed {confirmed.Count}, "
                            + $"excluded {excluded.Count}  -> refer={recommend}");
            foreach (var e in excluded)
                Console.WriteLine($"                excluded {e.ClaimantRef}: "
                                + e.Reason[..Math.Min(70, e.Reason.Length)]);

            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            trail.Add($"challenge: confirmed {confirmed.Count}, excluded {excluded.Count}, "
                    + $"recommend_refer={recommend}");
            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.QueueStateUpdateAsync("proposal", new ReferralProposal(
                t.SeedClaimRef, confirmed, excluded, recommend,
                outJson.GetProperty("rationale").GetString() ?? "", t.Hops, claimRefs, exposure), Shared);

            Console.WriteLine($"[refer]       {confirmed.Count} claimant(s), {claimRefs.Count} "
                            + $"claim(s), GBP {exposure:N0} needs a named human. PAUSING (durable).");
            await ctx.SendMessageAsync(new ReferralApprovalRequest(
                t.SeedClaimRef, confirmed.Count, claimRefs.Count, exposure,
                outJson.GetProperty("rationale").GetString() ?? "",
                confirmed.Select(c => c.ClaimantRef).ToList(),
                excluded.Select(c => c.ClaimantRef).ToList()));
        }
    }

    // ----------------------------------------------------------------- 4. refer
    //
    // What the human's answer means. Pure, so it can be tested for real.
    //   not approved               -> dismissed      a person overrode the recommendation
    //   approved + recommended     -> referred       it goes to the investigations unit
    //   approved + not recommended -> not_referred   the desk cleared them, and a person agreed
    public static string DecideOutcome(bool approved, bool recommended) =>
        !approved ? "dismissed" : recommended ? "referred" : "not_referred";

    public sealed partial class Refer() : Executor("refer")
    {
        // The 1.17.0 way: declare the protocol explicitly. The source generator that would
        // write this for you does not ship in this package, and `ReflectingExecutor` discovers
        // nothing while looking like it works.
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
            builder.YieldsOutput<FraudOutcome>()
                   .ConfigureRoutes(r => r.AddHandler<ReferralDecision>(HandleAsync));

        /// <summary>
        /// A person answered. Record what they decided, and only now write to Dataverse.
        ///
        /// Runs in a process that never saw the traversal, so everything comes off the checkpoint.
        /// The ONLY place that writes, and the only way in is a ReferralDecision, which can arrive
        /// only through the approval port - so the guarantee is structural, not a promise. A
        /// decline is written too.
        /// </summary>
        [MessageHandler]
        public async ValueTask HandleAsync(ReferralDecision decision, IWorkflowContext ctx)
        {
            var p = await ctx.ReadStateAsync<ReferralProposal>("proposal", Shared)
                ?? throw new InvalidOperationException("no proposal in state");
            var trail = await ctx.ReadStateAsync<List<string>>("trail", Shared) ?? new();
            var toolLog = await ctx.ReadStateAsync<List<string>>("tool_log", Shared) ?? new();
            var outcome = DecideOutcome(decision.Approved, p.RecommendRefer);

            var runId = Dataverse.NewRunId();
            var who = await Dataverse.WhoAmIAsync();
            // The ref carries WHO and WHEN, so many people running the same book at the same time
            // produce distinguishable rows and nobody overwrites anybody.
            var referralRef = $"FR-{who.Split('@')[0]}-{runId}";
            if (referralRef.Length > 99) referralRef = referralRef[..99];

            trail.Add($"refer: approved={decision.Approved} outcome={outcome} "
                    + $"by {(string.IsNullOrWhiteSpace(decision.Approver) ? "unnamed" : decision.Approver)} "
                    + $"run={runId}");

            var evidence = $"{p.Rationale}\n\nHops: {p.Hops}\n"
                + $"Confirmed: {string.Join(", ", p.Confirmed.Select(c => c.ClaimantRef))}\n"
                + $"Excluded: {string.Join(", ", p.Excluded.Select(c => c.ClaimantRef))}\n"
                + $"Tool calls: {string.Join("; ", toolLog)}";

            await Dataverse.WriteReferralAsync(referralRef, p.SeedClaimRef, outcome,
                p.Confirmed.Count, p.ClaimRefs.Count, p.ExposureGbp, p.Hops,
                decision.Approver, evidence, Language, runId);

            var rows = await Dataverse.ClaimsOfManyAsync(p.Confirmed.Select(c => c.ClaimantRef));
            foreach (var row in rows)
            {
                var claimantRef = Dataverse.Str(row, "cp_claimantref");
                var judged = p.Confirmed.First(c => c.ClaimantRef == claimantRef);
                await Dataverse.WriteReferralClaimAsync(referralRef,
                    Dataverse.Str(row, "cp_bookclaimref"), claimantRef,
                    judged.Reason, judged.Strength);
            }

            Console.WriteLine($"[refer]       {outcome.ToUpperInvariant()} by "
                            + $"{(string.IsNullOrWhiteSpace(decision.Approver) ? "unknown" : decision.Approver)}");
            Console.WriteLine($"[refer]       written to Dataverse as {referralRef}");

            await ctx.QueueStateUpdateAsync("trail", trail, Shared);
            await ctx.YieldOutputAsync(new FraudOutcome(
                p.SeedClaimRef, outcome, p.Confirmed.Count, p.ClaimRefs.Count, p.ExposureGbp,
                p.Hops, decision.Approver, referralRef, trail));
        }
    }

    // ----------------------------------------------------------------- assembly
    // THE HUMAN GATE, AS A NODE. Python suspends inside an executor; .NET puts a typed port in the
    // graph, so the wiring alone shows that nothing reaches Refer without passing through a person.
    public static readonly RequestPort ApprovalPort =
        RequestPort.Create<ReferralApprovalRequest, ReferralDecision>("approval");

    /// <summary>
    /// The orchestration graph. This is the artefact: versioned, reviewable, testable. It is a
    /// DESCRIPTION, not a running thing, so rebuilding it in a fresh process and pointing it at a
    /// checkpoint is how a resume works. Five nodes to Python's four, because of the port above.
    /// </summary>
    public static Workflow BuildWorkflow()
    {
        var intake = new Intake();
        var investigate = new Investigate();
        var challenge = new Challenge();
        var refer = new Refer();

        // Every edge carries a DISTINCT message type. Routing is by type, so two executors
        // accepting the same type would both receive it.
        return new WorkflowBuilder(intake)
            .WithName(WorkflowName)
            .AddEdge(intake, investigate)             // VerifiedClaim
            .AddEdge(investigate, challenge)          // TraversalComplete
            .AddEdge(challenge, ApprovalPort)         // ReferralApprovalRequest
            .AddEdge(ApprovalPort, refer)             // ReferralDecision
            .WithOutputFrom(refer)
            .Build();
    }
}
