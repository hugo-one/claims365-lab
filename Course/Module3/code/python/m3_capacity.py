"""What does one investigation actually cost in tokens?

DEMONSTRATION CODE - part of the Claims 365 training lab.

A tool-calling agent makes many more model calls than a single-shot assessment does, so its cost is
worth measuring rather than assuming. This runs one real investigation with a meter attached and
prints what crossed the wire, then scales it to a shared deployment's per-minute limit.

    python m3_capacity.py
"""
import asyncio
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from agent_framework import ChatMiddleware                            # noqa: E402

import m3_frauddesk as fd                                            # noqa: E402
from m3_types import SeedClaim                                        # noqa: E402

SEED = "BCL-2026-0201"
# A worked example: how many people share the deployment, and its tokens-per-minute ceiling.
PEOPLE = 50
TPM_LIMIT = 150_000


class Meter(ChatMiddleware):
    """Counts what actually crossed the wire, per model call."""

    def __init__(self):
        self.calls = []

    async def process(self, context, next):
        """Run the call, then dig the token usage out of wherever the framework exposes it.

        Usage has moved between the result and the context across versions, so this checks both and
        records an `unmeasured` call rather than silently reporting zero.
        """
        await next()   # the pipeline hands the next handler a closure, not the context
        usage = None
        resp = getattr(context, "result", None) or getattr(context, "response", None)
        for holder in (resp, context):
            if holder is None:
                continue
            try:
                d = holder.to_dict() if hasattr(holder, "to_dict") else dict(holder)
            except Exception:
                continue
            u = d.get("usage_details") or d.get("usage")
            if u:
                usage = u
                break
        if usage:
            self.calls.append({
                "input": usage.get("input_token_count") or usage.get("prompt_tokens") or 0,
                "output": usage.get("output_token_count") or usage.get("completion_tokens") or 0,
                "total": usage.get("total_token_count") or usage.get("total_tokens") or 0,
            })
        else:
            self.calls.append({"input": 0, "output": 0, "total": 0, "unmeasured": True})


async def main():
    """Run ONE real investigation with a meter attached, then scale it to a group."""
    meter = Meter()

    # Patch the agent factories so both run through the meter. Keeping this out of the workflow
    # means the desk has no idea it is being measured, so measuring cannot change what it does.
    orig_inv, orig_chal = fd.investigator_agent, fd.challenger_agent
    fd.investigator_agent = lambda: fd._client().as_agent(
        name="investigator", instructions=fd.INVESTIGATOR, tools=fd.LINK_TOOLS,
        middleware=[meter])
    fd.challenger_agent = lambda: fd._client().as_agent(
        name="challenge-officer", instructions=fd.CHALLENGER, middleware=[meter])

    for p in fd.CHECKPOINT_DIR.glob("*.json"):
        p.unlink()
    wf, _ = fd.build_workflow()
    await wf.run(SeedClaim(claim_ref=SEED))
    fd.investigator_agent, fd.challenger_agent = orig_inv, orig_chal

    measured = [c for c in meter.calls if not c.get("unmeasured")]
    total_in = sum(c["input"] for c in measured)
    total_out = sum(c["output"] for c in measured)
    total = sum(c["total"] for c in measured) or (total_in + total_out)

    print("\n" + "=" * 74)
    print("  ONE INVESTIGATION")
    print("=" * 74)
    print(f"  model calls        : {len(meter.calls)}  ({len(measured)} measured)")
    print(f"  tool calls         : {len(fd.TOOL_LOG)}")
    print(f"  input tokens       : {total_in:,}")
    print(f"  output tokens      : {total_out:,}")
    print(f"  total tokens       : {total:,}")
    if not measured:
        print("  (usage was not exposed by this framework version - see note below)")
        return
    print()
    print("=" * 74)
    print(f"  A GROUP OF {PEOPLE} SHARING ONE DEPLOYMENT")
    print("=" * 74)
    group = total * PEOPLE
    print(f"  if everyone runs it once   : {group:,} tokens")
    print(f"  deployment ceiling         : {TPM_LIMIT:,} tokens per minute")
    print(f"  so the group needs at least: {group / TPM_LIMIT:.1f} minute(s) of quota")
    print(f"  and at 3 runs each         : {3 * group / TPM_LIMIT:.1f} minute(s)")
    print()
    minutes_once = group / TPM_LIMIT
    print(f"  One run each consumes {minutes_once:.1f} minute(s) of quota, and people re-run, so")
    print("  three runs each does not fit under this ceiling.")
    print()
    print(f"  At 500K tokens per minute, one run each takes {group / 500_000:.1f} minute(s)")
    print(f"  and three runs each {3 * group / 500_000:.1f} minute(s), which does fit.")
    print("  The model is billed per token, so a higher ceiling costs nothing extra unless it is")
    print("  used: raising the limit is usually the better answer to a burst than doing less work.")


if __name__ == "__main__":
    asyncio.run(main())
