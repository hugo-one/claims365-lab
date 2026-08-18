"""Claims 365 Fraud Desk - Module 3 (Microsoft Agent Framework).

=========================================================================================
DEMONSTRATION CODE for a fictional insurer, written to be READ: it shows how a workflow,
agents, tools and a human approval gate fit together, in as few lines as the idea allows.

It is NOT production code. A real fraud desk would add secret management, idempotent writes,
structured logging and telemetry, data protection controls, and ongoing evaluation of the
model's judgements. Take the shape away with you, not the file.
=========================================================================================

Module 2 assessed ONE claim. This asks the question Module 2 structurally cannot: is that claim part
of a pattern? Triage runs per claim and returns a per-claim verdict, so a ring is invisible to it -
not because the model is weaker, but because it only ever sees one claim.

THE SHAPE, WHICH IS MICROSOFT'S OWN RECOMMENDATION
--------------------------------------------------
    "A workflow defines the high-level process, and individual executors within that workflow use
     agents for the steps that benefit from LLM reasoning."
    - https://learn.microsoft.com/agent-framework/journey/workflows

So this is ONE artefact, not two. The graph fixes the process, because the process has rules that
must not be negotiable. Inside it, two nodes are real agents with real tools, because which link to
follow next genuinely cannot be known in advance.

    intake  ->  investigate  ->  challenge  ->  refer
    (code)      (agent+tools)    (agent)        (human gate, then Dataverse)

WHY EACH NODE IS WHAT IT IS
    intake       code. A validation rule is not a judgement.
    investigate  AGENT WITH TOOLS. A fixed query cannot express "follow whatever the last hop
                 turned up". On this book it takes three hops: 3 claimants, then 5, then 9.
    challenge    AGENT. The traversal drags in neighbours who merely share a postcode, and telling
                 evidence from coincidence is a judgement.
    refer        code + a human. Nothing is referred without a named person, and the money is
                 computed in Python, never by the model.
"""

import json
import sys
from pathlib import Path
from typing import Never

HERE = Path(__file__).resolve().parent
# This folder is self-contained: it imports installed packages and its own modules, nothing else.
sys.path.insert(0, str(HERE))

from agent_framework import (                                          # noqa: E402
    Executor, WorkflowBuilder, WorkflowContext, FileCheckpointStorage, handler,
    response_handler, tool,
)
from agent_framework.openai import OpenAIChatClient                    # noqa: E402

import m3_dataverse as dv                                              # noqa: E402
import m3_env                                                          # noqa: E402
from m3_types import (                                                 # noqa: E402
    SeedClaim, VerifiedClaim, LinkedClaimant, TraversalComplete, JudgedClaimant, ReferralProposal,
    ReferralApprovalRequest, ReferralDecision, FraudOutcome, CHECKPOINT_TYPES,
)

WORKFLOW_NAME = "claims365-fraud-desk"
CHECKPOINT_DIR = HERE / "_checkpoints"
MAX_HOPS = 3
LANGUAGE = "python"

# Every tool call the MODEL chose to make, so the run can print it: "the agent decided to look
# something up" should be checkable rather than taken on trust.
TOOL_LOG: list[str] = []


# --------------------------------------------------------------------------- the tools
# Ordinary functions. `@tool` publishes them to the model, and the DESCRIPTION is what it reads when
# deciding whether to call one - so that sentence is prompt engineering, not documentation.
# `max_invocations` is a ceiling the framework enforces: a guardrail in code, not a hope in a prompt.
#
# WHY `expand` TAKES A LIST. An agent's cost is dominated by ROUND TRIPS, not tool calls, because
# every trip re-sends the whole conversation. A hop is a set operation, so batching it into one call
# makes an investigation several times cheaper for the same answer - and the model still decides
# whether to hop, who to expand and when to stop.
@tool(description="Follow every shared identifier out from a set of claimants, in one step. Give "
                  "it every claimant you know about; it returns who else shares a bank account, "
                  "phone or postcode with any of them, and says which. This is one hop.",
      max_invocations=6)
def expand(claimant_refs: list[str]) -> dict:
    """ONE HOP. Given everyone found so far, return everyone newly connected and say how.

    Three queries whatever the size of the set. `shared_with` records WHICH known person each new
    one connects to, because the challenge step judges links between members.

    Returns an error dict rather than raising: an exception inside a tool kills the run, whereas a
    returned error lets the model notice its own bad argument and correct itself.
    """
    TOOL_LOG.append(f"expand({len(claimant_refs)} claimant(s))")
    known = dv.claimants_by_refs(claimant_refs)
    if not known:
        return {"error": "none of those claimant refs exist"}
    by_attr = {"bank_account": "cp_bankaccount", "phone": "cp_phone", "postcode": "cp_postcode"}
    seen = {k["cp_claimantref"] for k in known}
    found: dict[str, dict] = {}
    for attr, col in by_attr.items():
        values = {k[col] for k in known if k.get(col)}
        for row in dv.claimants_sharing_any(attr, values):
            ref = row["cp_claimantref"]
            if ref in seen:
                continue
            shared_with = [k["cp_claimantref"] for k in known if k.get(col) == row[col]]
            f = found.setdefault(ref, {"claimant_ref": ref, "full_name": row["cp_fullname"],
                                       "links": []})
            f["links"].append({"attribute": attr, "value": row[col],
                               "shared_with": shared_with})
    return {"queried": len(known), "new_claimants": list(found.values()),
            "new_count": len(found)}


@tool(description="Identifiers held by a set of claimants: bank account, phone, postcode.",
      max_invocations=4)
def get_claimants(claimant_refs: list[str]) -> dict:
    """Identifiers for people the model already knows about, without hopping."""
    TOOL_LOG.append(f"get_claimants({len(claimant_refs)})")
    return {"claimants": [
        {"claimant_ref": c["cp_claimantref"], "full_name": c["cp_fullname"],
         "bank_account": c["cp_bankaccount"], "phone": c["cp_phone"],
         "postcode": c["cp_postcode"]}
        for c in dv.claimants_by_refs(claimant_refs)]}


@tool(description="Claims belonging to a set of claimants.", max_invocations=4)
def list_claims_for(claimant_refs: list[str]) -> dict:
    """The claims behind a set of claimants, so the model can see scale.

    The model may quote these figures, which is harmless: the number a human is shown is summed
    from the same rows in Python, after the model has finished deciding WHO.
    """
    TOOL_LOG.append(f"list_claims_for({len(claimant_refs)})")
    rows = dv.claims_of_many(claimant_refs)
    return {"count": len(rows),
            "claims": [{"claim_ref": r["cp_bookclaimref"], "claimant_ref": r["cp_claimantref"],
                        "repairer": r["cp_repairername"],
                        "amount_gbp": float(r["cp_amountclaimed"])} for r in rows]}


@tool(description="How many claims a repairer handled. A COUNT only, because a shared repairer is "
                  "not a link between people.",
      max_invocations=3)
def count_claims_at_repairer(repairer_name: str) -> dict:
    """A COUNT, deliberately.

    The seed claim's repairer has 42 claims across 36 people whose only connection is a garage.
    Returning that list would drown the traversal; a count lets the model see the repairer explains
    nothing and move on. What a tool REFUSES to return shapes behaviour as much as what it returns.
    """
    TOOL_LOG.append(f"count_claims_at_repairer({repairer_name!r})")
    rows = dv.claims_by_repairer(repairer_name)
    return {"repairer": repairer_name, "claim_count": len(rows),
            "distinct_claimants": len({r["cp_claimantref"] for r in rows})}


LINK_TOOLS = [expand, get_claimants, list_claims_for, count_claims_at_repairer]


# --------------------------------------------------------------------------- the agents
INVESTIGATOR = """You are a claims investigator at Contoso Insurance, a fictional insurer used for
training. You have been handed one claim that triage flagged.

Your job is to find out whether that claim is part of an ORGANISED GROUP, by following identifiers
that separate households do not normally share.

How to work:
- Call `expand` with EVERY claimant you know about so far. That is one hop, and it returns everyone
  newly connected plus the identifier that connects them.
- If a hop finds someone new, hop again with the enlarged set. Stop after at most %d hops, or as
  soon as a hop returns nobody new.
- Do not call `expand` one person at a time. Pass the whole set. One call per hop.
- A shared REPAIRER is not a link between people. A busy garage serves hundreds of unconnected
  customers. Check its volume if you like, but never treat it as evidence of a connection.

Report every claimant you reached, including the one you started from, and say which identifier
brought each of them in. Report candidates, not conclusions. Somebody else decides what they mean.
""" % MAX_HOPS

TRAVERSAL_SCHEMA = {
    "type": "object", "additionalProperties": False,
    "required": ["claimants", "hops", "narrative"],
    "properties": {
        "hops": {"type": "integer"},
        "narrative": {"type": "string",
                      "description": "How you moved, hop by hop. Three or four sentences."},
        "claimants": {
            "type": "array",
            "items": {
                "type": "object", "additionalProperties": False,
                "required": ["claimant_ref", "full_name", "why_linked", "link_type",
                             "shared_with"],
                "properties": {
                    "claimant_ref": {"type": "string"},
                    "full_name": {"type": "string"},
                    "why_linked": {"type": "string"},
                    "link_type": {"type": "string",
                                  "enum": ["seed", "bank_account", "phone", "postcode"]},
                    # The pairwise structure, stated rather than left in prose. The challenge step
                    # has to judge links BETWEEN members, so it needs to know which members.
                    "shared_with": {"type": "array", "items": {"type": "string"},
                                    "description": "claimant refs this person shares the "
                                                   "identifier with"},
                },
            },
        },
    },
}

CHALLENGER = """You are the fraud desk's challenge officer at Contoso Insurance, a fictional insurer
used for training. An investigator has handed you a list of claimants reached by following shared
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

Do not manufacture doubt to appear rigorous, and do not wave things through to appear decisive.
"""

JUDGEMENT_SCHEMA = {
    "type": "object", "additionalProperties": False,
    "required": ["confirmed", "excluded", "recommend_refer", "rationale"],
    "properties": {
        "recommend_refer": {"type": "boolean"},
        "rationale": {"type": "string"},
        "confirmed": {
            "type": "array",
            "items": {"type": "object", "additionalProperties": False,
                      "required": ["claimant_ref", "reason", "strength"],
                      "properties": {"claimant_ref": {"type": "string"},
                                     "reason": {"type": "string"},
                                     "strength": {"type": "string",
                                                  "enum": ["strong", "weak"]}}},
        },
        "excluded": {
            "type": "array",
            "items": {"type": "object", "additionalProperties": False,
                      "required": ["claimant_ref", "reason"],
                      "properties": {"claimant_ref": {"type": "string"},
                                     "reason": {"type": "string"}}},
        },
    },
}


def _client() -> OpenAIChatClient:
    """A chat client for the Foundry deployment, configured from `lab/.env`.

    `base_url` rather than an Azure-style endpoint: gpt-5-mini rejects dated `api_version` values,
    so the OpenAI-compatible `/openai/v1/` route is the one that works.
    """
    cfg = m3_env.foundry()
    return OpenAIChatClient(model=cfg["MODEL_DEPLOYMENT"], api_key=cfg["FOUNDRY_KEY"],
                            base_url=cfg["FOUNDRY_OPENAI_V1"])


def investigator_agent():
    """The traversal agent: instructions plus the four link tools.

    Built fresh per call, so each investigation starts with an empty conversation - a reused agent
    would carry the last run's history into the next.
    """
    return _client().as_agent(name="investigator", instructions=INVESTIGATOR, tools=LINK_TOOLS)


def challenger_agent():
    """The adversarial agent. NO TOOLS on purpose: it judges evidence it was handed, and letting it
    find its own would remove the separation this step exists to create."""
    return _client().as_agent(name="challenge-officer", instructions=CHALLENGER)


# --------------------------------------------------------------------------- 1. intake
class Intake(Executor):
    """Code, not a judgement. It refuses anything that is not a real claim in the book."""

    def __init__(self) -> None:
        # The id names this node in the graph AND in every checkpoint. Renaming one after a run has
        # been saved makes that checkpoint unrestorable, so treat these four strings as a schema.
        super().__init__(id="intake")

    @handler
    async def receive(self, seed: SeedClaim, ctx: WorkflowContext[VerifiedClaim]) -> None:
        """Turn a reference into a verified claim, or refuse.

        A rule, not a judgement, so it is code: a model asked "does this claim exist?" can be
        argued with, a lookup cannot. It also seeds the shared `trail` the graph appends to.
        """
        row = dv.get_claim(seed.claim_ref)
        if row is None:
            raise ValueError(
                f"{seed.claim_ref} is not in the claims book. The fraud desk only investigates "
                f"claims that exist; it does not take a reference on trust.")
        v = VerifiedClaim(claim_ref=row["cp_bookclaimref"], claimant_ref=row["cp_claimantref"],
                          repairer=row["cp_repairername"],
                          amount_claimed=float(row["cp_amountclaimed"]))
        print(f"[intake]      {v.claim_ref}  claimant={v.claimant_ref}  "
              f"repairer={v.repairer}  GBP {v.amount_claimed:,.0f}")
        ctx.set_state("seed", v)
        ctx.set_state("trail", [f"intake: accepted {v.claim_ref} from triage"])
        await ctx.send_message(v)


# --------------------------------------------------------------------------- 2. investigate
class Investigate(Executor):
    """The agent half. The model chooses which link to follow, using tools that hit Dataverse."""

    def __init__(self) -> None:
        super().__init__(id="investigate")

    @handler
    async def run_traversal(self, seed: VerifiedClaim,
                            ctx: WorkflowContext[TraversalComplete]) -> None:
        """Let the model plan and run the traversal, then hand on CANDIDATES, not conclusions.

        The node that justifies the whole tier: the second hop's arguments are the first hop's
        results, which no fixed query can express. The model calls `expand` with 1, then 3, then 5
        claimants, and stops when a hop returns nobody new.

        `response_format` ENFORCES the reply's shape rather than requesting it. A model that drifts
        from a requested shape returns well-formed JSON carrying a different judgement, which is
        much harder to spot than a parse error.
        """
        TOOL_LOG.clear()
        # Hand the agent what the graph already knows. Making it fetch the seed identifiers costs
        # two round trips. Give an agent its starting facts; make it earn only the decisions.
        start = dv.get_claimant(seed.claimant_ref) or {}
        opening = (
            f"Seed claim {seed.claim_ref}, claimant {seed.claimant_ref} "
            f"({start.get('cp_fullname', 'unknown')}), repairer {seed.repairer}, "
            f"GBP {seed.amount_claimed:,.0f}.\n"
            f"Their identifiers: bank {start.get('cp_bankaccount', '?')}, "
            f"phone {start.get('cp_phone', '?')}, postcode {start.get('cp_postcode', '?')}.\n\n"
            f"Begin by calling expand with [\"{seed.claimant_ref}\"]. "
            f"Find out who else is connected.")
        agent = investigator_agent()
        result = await agent.run(
            opening, options={"response_format": TRAVERSAL_SCHEMA, "max_tokens": 8000})
        out = json.loads(str(result))

        cands = [LinkedClaimant(claimant_ref=c["claimant_ref"], full_name=c["full_name"],
                                why_linked=c["why_linked"], link_type=c["link_type"],
                                shared_with=list(c.get("shared_with") or []))
                 for c in out["claimants"]]
        print(f"[investigate] the model made {len(TOOL_LOG)} tool call(s):")
        for t in TOOL_LOG:
            print(f"                -> {t}")
        print(f"[investigate] reached {len(cands)} claimant(s) over {out['hops']} hop(s)")

        trail = list(ctx.get_state("trail") or [])
        trail.append(f"investigate: {len(cands)} candidates over {out['hops']} hops, "
                     f"{len(TOOL_LOG)} tool calls")
        ctx.set_state("trail", trail)
        ctx.set_state("tool_log", list(TOOL_LOG))
        await ctx.send_message(TraversalComplete(seed_claim_ref=seed.claim_ref, candidates=cands,
                                                 hops=int(out["hops"]),
                                                 narrative=out["narrative"]))


def price_confirmed(rows) -> tuple[list[str], float]:
    """The money, computed from claim rows the model never wrote.

    Pure and separate so the test suite can prove the arithmetic against fixture rows, rather
    than merely checking that a line of source mentions a sum. The model decides WHO is
    confirmed; every figure a human sees comes out of this function, over rows that exist.
    """
    refs = [r["cp_bookclaimref"] for r in rows]
    return refs, sum(float(r["cp_amountclaimed"]) for r in rows)


# --------------------------------------------------------------------------- 3. challenge
class Challenge(Executor):
    """The adversarial half. Recall going up is not the same as being right."""

    def __init__(self) -> None:
        super().__init__(id="challenge")

    @handler
    async def review(self, t: TraversalComplete,
                     ctx: WorkflowContext[ReferralProposal]) -> None:
        """Argue against the traversal, keep what survives, then price it in Python.

        The traversal reaches nine. Six are the ring; three share only a postcode. A separate agent
        rather than a second prompt to the same one: this one never saw the traversal happen, so it
        cannot defend a choice it made itself.
        """
        listing = "\n".join(
            f"  {c.claimant_ref}  {c.full_name}  linked by {c.link_type}: {c.why_linked}"
            for c in t.candidates)
        agent = challenger_agent()
        result = await agent.run(
            f"Seed claim {t.seed_claim_ref}.\nThe investigator reached these claimants over "
            f"{t.hops} hops:\n{listing}\n\nWhich of them would you actually refer?",
            options={"response_format": JUDGEMENT_SCHEMA, "max_tokens": 6000})
        out = json.loads(str(result))

        confirmed = [JudgedClaimant(**c) for c in out["confirmed"]]
        excluded = [JudgedClaimant(claimant_ref=c["claimant_ref"], reason=c["reason"],
                                   strength="weak") for c in out["excluded"]]

        # THE MONEY IS COMPUTED HERE, FROM THE RECORDS. The model decides WHO, never HOW MUCH.
        # One batched read for the whole confirmed set: this module's own lesson is that cost
        # lives in round trips, so the desk practises it at the data layer too, as Desk.cs does.
        rows = dv.claims_of_many([c.claimant_ref for c in confirmed])
        claim_refs, exposure = price_confirmed(rows)

        print(f"[challenge]   confirmed {len(confirmed)}, excluded {len(excluded)}"
              f"  -> refer={out['recommend_refer']}")
        for e in excluded:
            print(f"                excluded {e.claimant_ref}: {e.reason[:70]}")

        trail = list(ctx.get_state("trail") or [])
        trail.append(f"challenge: confirmed {len(confirmed)}, excluded {len(excluded)}, "
                     f"recommend_refer={out['recommend_refer']}")
        ctx.set_state("trail", trail)
        # Recorded in the checkpoint so a saved run says which deployment produced it. Read via
        # m3_env, the same way the client is built - the value normally lives in lab/.env, not
        # the process environment.
        ctx.set_state("model", m3_env.setting("MODEL_DEPLOYMENT", "unknown"))
        await ctx.send_message(ReferralProposal(
            seed_claim_ref=t.seed_claim_ref, confirmed=confirmed, excluded=excluded,
            recommend_refer=bool(out["recommend_refer"]), rationale=out["rationale"],
            hops=t.hops, claim_refs=claim_refs, exposure_gbp=exposure))


# --------------------------------------------------------------------------- 4. refer
def decide_outcome(approved: bool, recommended: bool) -> str:
    """What the human's answer means. Pure, so it can be tested for real.

      not approved                  -> dismissed     a person overrode the recommendation
      approved + recommended        -> referred      it goes to the investigations unit
      approved + not recommended    -> not_referred  the desk cleared them, and a person agreed
    """
    if not approved:
        return "dismissed"
    return "referred" if recommended else "not_referred"


class Refer(Executor):
    """The gate. Nothing is referred, and nothing is written to Dataverse, without a named human."""

    def __init__(self) -> None:
        super().__init__(id="refer")

    @handler
    async def ask(self, p: ReferralProposal,
                  ctx: WorkflowContext[Never, FraudOutcome]) -> None:
        """Stop the workflow dead and wait for a person. Possibly for days.

        `ctx.request_info` suspends the run and writes it to disk, so this process can exit. The
        approval arrives from a DIFFERENT process and picks up exactly where it stopped - which is
        why the lab runs this as three commands rather than one script that waits. The proposal
        goes into shared state first, because the resuming process holds nothing in memory.

        `WorkflowContext[Never, FraudOutcome]`: sends no messages onward, produces a FraudOutcome.
        `Never` rather than `None`, which the framework's type validator rejects.
        """
        ctx.set_state("proposal", p)
        print(f"[refer]       {len(p.confirmed)} claimant(s), {len(p.claim_refs)} claim(s), "
              f"GBP {p.exposure_gbp:,.0f} needs a named human. PAUSING (durable).")
        await ctx.request_info(
            ReferralApprovalRequest(
                seed_claim_ref=p.seed_claim_ref, claimant_count=len(p.confirmed),
                claim_count=len(p.claim_refs), exposure_gbp=p.exposure_gbp,
                rationale=p.rationale,
                confirmed_refs=[c.claimant_ref for c in p.confirmed],
                excluded_refs=[c.claimant_ref for c in p.excluded]),
            ReferralDecision)

    @response_handler
    async def on_decision(self, request: ReferralApprovalRequest, decision: ReferralDecision,
                          ctx: WorkflowContext[Never, FraudOutcome]) -> None:
        """A person answered. Record what they decided, and only now write to Dataverse.

        Runs in a process that never saw the traversal, so everything comes off the checkpoint. The
        ONLY place that writes, reachable only through a human's answer - which makes "nothing is
        referred without a named person" structural rather than a promise. A decline is written
        too: that the desk looked and somebody said no is worth keeping.
        """
        p: ReferralProposal = ctx.get_state("proposal")
        trail = list(ctx.get_state("trail") or [])
        outcome = decide_outcome(decision.approved, p.recommend_refer)

        run_id = dv.new_run_id()
        who = dv.whoami()
        # The ref carries WHO and WHEN, so many people running the same book at the same time
        # produce fifty distinguishable rows and nobody overwrites anybody.
        referral_ref = f"FR-{who.split('@')[0]}-{run_id}"[:99]
        trail.append(f"refer: approved={decision.approved} outcome={outcome} "
                     f"by {decision.approver or 'unnamed'} run={run_id}")

        evidence = (f"{p.rationale}\n\nHops: {p.hops}\n"
                    f"Confirmed: {', '.join(c.claimant_ref for c in p.confirmed) or 'none'}\n"
                    f"Excluded: {', '.join(c.claimant_ref for c in p.excluded) or 'none'}\n"
                    f"Tool calls: {'; '.join(ctx.get_state('tool_log') or [])}")

        dv.write_referral(referral_ref=referral_ref, seed_claim=p.seed_claim_ref,
                          decision=outcome, ring_size=len(p.confirmed),
                          claim_count=len(p.claim_refs), exposure=p.exposure_gbp, hops=p.hops,
                          approved_by=decision.approver, evidence=evidence,
                          language=LANGUAGE, run_id=run_id)
        judged = {c.claimant_ref: c for c in p.confirmed}
        for row in dv.claims_of_many(list(judged)):
            j = judged[row["cp_claimantref"]]
            dv.write_referral_claim(referral_ref=referral_ref,
                                    claim_ref=row["cp_bookclaimref"],
                                    claimant_ref=row["cp_claimantref"],
                                    reason=j.reason,
                                    strength=j.strength)
        print(f"[refer]       {outcome.upper()} by {decision.approver or 'unknown'}")
        print(f"[refer]       written to Dataverse as {referral_ref}")

        ctx.set_state("trail", trail)
        await ctx.yield_output(FraudOutcome(
            seed_claim_ref=p.seed_claim_ref, decision=outcome,
            claimant_count=len(p.confirmed), claim_count=len(p.claim_refs),
            exposure_gbp=p.exposure_gbp, hops=p.hops, approver=decision.approver,
            referral_ref=referral_ref, audit_trail=trail))


# --------------------------------------------------------------------------- assembly
def build_workflow():
    """The orchestration graph. This is the artefact: versioned, reviewable, testable.

    Called identically by `start`, `status` and `approve`. The graph is a DESCRIPTION, not a running
    thing, so rebuilding it in a fresh process and pointing it at a checkpoint is how a resume works.

    `allowed_checkpoint_types` is a restore-time allowlist; `max_iterations` is the runaway guard.
    Returns the workflow AND its storage, so `status` can read the checkpoint without replaying.
    """
    CHECKPOINT_DIR.mkdir(parents=True, exist_ok=True)
    storage = FileCheckpointStorage(CHECKPOINT_DIR, allowed_checkpoint_types=CHECKPOINT_TYPES)
    intake, investigate, challenge, refer = Intake(), Investigate(), Challenge(), Refer()
    wf = (WorkflowBuilder(start_executor=intake, checkpoint_storage=storage,
                          name=WORKFLOW_NAME, max_iterations=30)
          .add_edge(intake, investigate)        # VerifiedClaim
          .add_edge(investigate, challenge)     # TraversalComplete
          .add_edge(challenge, refer)           # ReferralProposal
          .build())
    return wf, storage
