"""Assertions over the fraud desk. No model, no Dataverse, no cost, under a second.

DEMONSTRATION CODE - part of the Claims 365 training lab.

The point: the guarantees an insurer would ask about are checked in CODE, not argued for in a
document. "Nothing is referred without a named human" is a test that re-runs for free, not a
promise - and sampling model runs could never establish it.

    python m3_test.py          # exits non-zero if anything fails
"""
import inspect
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import m3_dataverse as dv
import m3_frauddesk as fd
import m3_types as t

PASS, FAIL = [], []


def check(name, cond, detail=""):
    """Record one assertion and print it. Detail is shown only on failure, so a passing run reads
    as a list of the guarantees this module makes rather than as debug output."""
    (PASS if cond else FAIL).append(name)
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail and not cond
                                                        else ""))


print("=== the graph ===")

# Delivery is by message type. If two executors accept the same type, BOTH receive the message,
# which produces no output and no error. The graph is only safe because the types are disjoint.
OUR_TYPES = {n for n in dir(t) if not n.startswith("_")}


def handler_types(cls):
    """The message types THIS class declares a handler for.

    `cls.__dict__` and not `getmembers`, because the latter also walks methods inherited from
    Executor and reports base-class types the graph never uses.
    """
    out = set()
    for fn in cls.__dict__.values():
        fn = getattr(fn, "__wrapped__", fn)
        if not callable(fn) or not hasattr(fn, "__annotations__"):
            continue
        try:
            params = list(inspect.signature(fn).parameters.values())
        except (TypeError, ValueError):
            continue
        for p in params[1:]:
            name = getattr(p.annotation, "__name__", str(p.annotation))
            if name in OUR_TYPES:
                out.add(name)
                break
    return out


types_by_node = {c.__name__: handler_types(c)
                 for c in (fd.Intake, fd.Investigate, fd.Challenge, fd.Refer)}
seen, overlap = set(), set()
for node, ts in types_by_node.items():
    overlap |= (seen & ts)
    seen |= ts
check("every executor accepts a distinct message type", not overlap, f"shared: {overlap}")

# Asserted from the source of build_workflow, the same way the yield_output checks below read
# their classes: the wiring is a claim about the REAL graph, so the test must look at the real
# add_edge calls rather than at a list this file constructed itself.
wiring = inspect.getsource(fd.build_workflow)
edge_positions = [wiring.find("add_edge(intake, investigate)"),
                  wiring.find("add_edge(investigate, challenge)"),
                  wiring.find("add_edge(challenge, refer)")]
check("the graph wires intake -> investigate -> challenge -> refer",
      -1 not in edge_positions and edge_positions == sorted(edge_positions),
      f"positions {edge_positions}")
check("the workflow builds", fd.build_workflow()[0] is not None)

# A checkpoint tags every value `module:QualName` and refuses anything not on the allowlist. If the
# types were defined in the entry-point script the tag would be `__main__:...`, and a checkpoint
# written by one command would be unreadable by the next - silently, as an empty list.
check("checkpoint allowlist is module-qualified, not __main__",
      all(x.startswith("m3_types:") for x in t.CHECKPOINT_TYPES))
check("every message type is on the checkpoint allowlist",
      {f"m3_types:{n}" for n in ("SeedClaim", "TraversalComplete", "ReferralProposal",
                                 "ReferralApprovalRequest", "ReferralDecision", "FraudOutcome")}
      <= set(t.CHECKPOINT_TYPES))

print("\n=== nothing is referred without a person ===")

# The question an insurer would ask, answered from the structure of the code itself.
src = {c.__name__: inspect.getsource(c)
       for c in (fd.Intake, fd.Investigate, fd.Challenge, fd.Refer)}
yielders = [n for n, s in src.items() if "yield_output" in s]
check("only Refer can produce an outcome", yielders == ["Refer"], f"found {yielders}")
check("Refer only yields inside its response handler, i.e. after a human answered",
      "yield_output" in src["Refer"].split("@response_handler")[-1]
      and "yield_output" not in src["Refer"].split("@response_handler")[0])
check("the desk asks for a decision before it can finish", "request_info" in src["Refer"])

# The runner refuses to act without a name: a referral raised by "unnamed" would defeat the point
# of having a human gate at all. Run it for real, in a child process, rather than grepping the
# source for the error message - the guard exits before any sign-in or model call, so this is free.
guard = subprocess.run([sys.executable, str(Path(__file__).resolve().parent / "m3_run.py"),
                        "approve"], capture_output=True, text=True, timeout=120)
check("approve without --by is refused, in a real process",
      guard.returncode != 0 and "named human" in (guard.stderr + guard.stdout),
      f"rc={guard.returncode} out={((guard.stderr + guard.stdout)[:80])!r}")

print("\n=== the module is self-contained ===")

# Nothing in this folder may import anything outside it, except installed packages. A cross-folder
# import works wherever that folder happens to exist and fails where it does not - and one inside a
# function fails only when that function first runs.
HERE = Path(__file__).resolve().parent
LOCAL = {p.stem for p in HERE.glob("m3_*.py")}
cross_module, unresolved, path_hacks = [], [], []
for p in sorted(HERE.glob("m3_*.py")):
    body = p.read_text(encoding="utf-8")
    for stmt in re.findall(r"^[ \t]*(?:from|import)[ \t]+(m\d_[A-Za-z0-9_]+)", body, re.M):
        if stmt.startswith("m3_"):
            if stmt not in LOCAL:
                unresolved.append(f"{p.name} -> {stmt}")
        else:
            cross_module.append(f"{p.name} -> {stmt}")
    for line in re.findall(r"^[ \t]*sys\.path\.insert.*$", body, re.M):
        if re.search(r"[\"']Module\d", line):
            path_hacks.append(f"{p.name}: {line.strip()[:60]}")

check("no file imports another module's Python", not cross_module, f"{cross_module}")
check("every local import resolves to a file in this folder", not unresolved, f"{unresolved}")
check("no sys.path hack reaches into another module's folder", not path_hacks, f"{path_hacks}")

print("\n=== the shape of what a model returns is ENFORCED, not requested ===")

# Requesting JSON in the prompt is not the same as enforcing a schema: a drifting model returns
# well-formed JSON carrying a different JUDGEMENT, which is far harder to spot. Checked this way
# because a schema constant can be perfectly correct and never actually passed to a model call.
for node, schema in (("Investigate", "TRAVERSAL_SCHEMA"), ("Challenge", "JUDGEMENT_SCHEMA")):
    check(f"{node} enforces a response schema",
          "response_format" in src[node] and schema in src[node])

print("\n=== the money ===")
# Prove the arithmetic, not the source text: the pricing function is pure, so it can be run
# against fixture rows right here, with no Dataverse and no model.
fixture = [{"cp_bookclaimref": "BCL-TEST-1", "cp_amountclaimed": "3100"},
           {"cp_bookclaimref": "BCL-TEST-2", "cp_amountclaimed": 2500.5}]
refs, total = fd.price_confirmed(fixture)
check("exposure is summed in Python from claim rows, not returned by a model",
      refs == ["BCL-TEST-1", "BCL-TEST-2"] and total == 5600.5, f"got {refs}, {total}")
check("no schema lets a model return an amount",
      "amount" not in str(fd.JUDGEMENT_SCHEMA) and "exposure" not in str(fd.TRAVERSAL_SCHEMA))

print("\n=== the decision table ===")
cases = [
    (True, True, "referred"),      # a person agreed with the desk
    (True, False, "not_referred"),  # the desk cleared them and a person agreed
    (False, True, "dismissed"),    # a person overrode a recommendation to refer
    (False, False, "dismissed"),   # a person overrode a recommendation not to refer
]
for approved, recommended, want in cases:
    got = fd.decide_outcome(approved, recommended)
    check(f"decide_outcome(approved={approved}, recommended={recommended}) == {want}", got == want,
          f"got {got}")

print("\n=== the guardrails on a shared deployment ===")
for fn in fd.LINK_TOOLS:
    name = getattr(fn, "name", None) or getattr(fn, "__name__", str(fn))
    cap = getattr(fn, "max_invocations", None)
    check(f"tool {name} has a call ceiling", isinstance(cap, int) and cap > 0, f"got {cap}")

print("\n=== the data layer ===")
# The book contains a repairer called Eccles Panel & Paint. Unencoded, the & splits the query
# string and the filter fails as malformed - so the escape helper must survive both parsers,
# OData's and the URL's. This is the regression test for exactly that name.
encoded = dv.esc("Eccles Panel & Paint")
check("identifier values are URL-encoded before they reach Dataverse",
      "&" not in encoded and "%26" in encoded and " " not in encoded, f"got {encoded!r}")

print("\n=== the judgement the module is about ===")
check("the challenger is told a postcode alone is not evidence",
      re.search(r"postcode", fd.CHALLENGER, re.I) is not None
      and "Exclude anyone whose ONLY link is a postcode" in fd.CHALLENGER)
check("the investigator is told a shared repairer is not a link",
      "not a link" in fd.INVESTIGATOR)
check("the investigator is bounded to a fixed number of hops",
      f"at most {fd.MAX_HOPS} hops" in fd.INVESTIGATOR)

print(f"\n{len(PASS)} passed, {len(FAIL)} failed")
sys.exit(1 if FAIL else 0)
