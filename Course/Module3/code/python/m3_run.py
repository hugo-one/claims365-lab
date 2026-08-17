"""Drive the fraud desk, one process at a time.

DEMONSTRATION CODE - part of the Claims 365 training lab.

Every command below is a SEPARATE OS PROCESS holding nothing in memory from the last one. If
`approve` works, the durability is real. If it did not, we would have to say so.

  python m3_login.py                          sign in as yourself, once
  python m3_run.py start                      investigate the seeded claim, stop at the gate
  python m3_run.py start --claim BCL-2026-0007 investigate a different claim
  python m3_run.py status                     what is waiting, read from disk only
  python m3_run.py approve --by "K Bell"      approve, and write the referral to Dataverse
  python m3_run.py decline --by "K Bell"      dismiss it, and record that too
  python m3_run.py mine                       every referral YOU have written
"""
import argparse
import asyncio
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import m3_dataverse as dv                                              # noqa: E402
from m3_types import ReferralApprovalRequest, ReferralDecision, SeedClaim   # noqa: E402
from m3_frauddesk import build_workflow, WORKFLOW_NAME, CHECKPOINT_DIR      # noqa: E402

DEFAULT_SEED = "BCL-2026-0201"


def load_seed(claim_ref: str) -> SeedClaim:
    """Just the reference. Validating it is intake's job, inside the graph where it is testable."""
    return SeedClaim(claim_ref=claim_ref)


async def _pending():
    """The latest checkpoint and what it is waiting on. Reads the file ONLY - no model call, no
    Dataverse call - which is what makes `status` free and instant."""
    _, storage = build_workflow()
    cp = await storage.get_latest(workflow_name=WORKFLOW_NAME)
    if cp is None:
        return None, {}
    return cp, (cp.pending_request_info_events or {})


async def cmd_start(claim_ref: str):
    """Investigate a claim from scratch, and stop at the human gate.

    Old checkpoints are cleared first: one pending investigation keeps `status` and `approve`
    unambiguous, and discards nothing real - referrals already written are rows, not checkpoints.
    """
    seed = load_seed(claim_ref)
    CHECKPOINT_DIR.mkdir(parents=True, exist_ok=True)
    stale = list(CHECKPOINT_DIR.glob("*.json"))
    if stale:
        print(f"(clearing {len(stale)} checkpoint(s) from a previous investigation. Referrals "
              f"already written to Dataverse are not affected.)")
    for old in stale:
        old.unlink()
    print(f"=== FRAUD DESK  {seed.claim_ref} ===")
    print(f"    signed in as {dv.whoami()}\n")
    wf, _ = build_workflow()
    result = await wf.run(seed)
    print(f"\nworkflow state: {result.get_final_state()}")
    await cmd_status()


async def cmd_status():
    """What is waiting, read from disk in a process that never ran the investigation.

    The demonstration, not a convenience: asking "what is my desk waiting on?" with no ID to hand
    costs nothing, because a suspended workflow is just a file.
    """
    cp, pending = await _pending()
    print("\n=== STATUS (read from disk, nothing in memory) ===")
    if cp is None:
        print("  no checkpoint. run 'start' first.")
        return
    print(f"  checkpoint files : {len(list(CHECKPOINT_DIR.glob('*.json')))}")
    print(f"  latest           : {cp.checkpoint_id}")
    if not pending:
        print("  pending          : none. The investigation is finished.")
        return
    for rid, ev in pending.items():
        d = getattr(ev, "data", ev)
        if isinstance(d, ReferralApprovalRequest):
            print(f"  pending          : a REFERRAL DECISION on {d.seed_claim_ref}")
            print(f"    claimants      : {d.claimant_count}")
            print(f"    claims         : {d.claim_count}")
            print(f"    exposure       : GBP {d.exposure_gbp:,.0f}")
            print(f"    would refer    : {', '.join(d.confirmed_refs) or 'nobody'}")
            print(f"    ruled out      : {', '.join(d.excluded_refs) or 'nobody'}")
            print(f"    -> python m3_run.py approve --by \"Your Name\"")
            print(f"    -> python m3_run.py decline --by \"Your Name\"")


async def cmd_decide(approved: bool, by: str, note: str):
    """Answer the pending gate, in a process that never started the investigation.

    The whole durability claim is one call: rebuild the graph, hand it the checkpoint id and
    `{request_id: response}`, and the run continues. Nothing was held in memory between commands.
    (C# answers the same gate as an event stream - same guarantee, different shape.)
    """
    cp, pending = await _pending()
    if not pending:
        print("nothing is waiting for a decision. run 'status'.")
        return
    rid, _ = next(iter(pending.items()))
    print(f"=== RESUMING from checkpoint {cp.checkpoint_id} in a NEW process ===\n")
    wf, _ = build_workflow()
    result = await wf.run(responses={rid: ReferralDecision(approved=approved, approver=by,
                                                           note=note)},
                          checkpoint_id=cp.checkpoint_id)
    outs = result.get_outputs()
    if not outs:
        await cmd_status()
        return
    o = outs[0]
    print("\n=== OUTCOME ===")
    print(f"  seed claim   : {o.seed_claim_ref}")
    print(f"  decision     : {o.decision}")
    print(f"  claimants    : {o.claimant_count}")
    print(f"  claims       : {o.claim_count}")
    print(f"  exposure     : GBP {o.exposure_gbp:,.0f}")
    print(f"  hops         : {o.hops}")
    print(f"  approver     : {o.approver}")
    print(f"  referral     : {o.referral_ref}")
    print("  audit trail  :")
    for line in o.audit_trail:
        print(f"    - {line}")
    print("\n  Open it in Dataverse:")
    print(f"  {dv.DATAVERSE}/main.aspx?pagetype=entitylist&etn=cp_fraudreferral")


def cmd_mine():
    """Every referral you have written, filtered by your own UPN.

    Nobody's row is hidden and nobody can edit anybody else's, so "mine" is a filter, not a race.
    The `built with` column shows which language produced each one.
    """
    rows = dv.my_referrals()
    print(f"\n=== REFERRALS WRITTEN BY {dv.whoami()} ===")
    if not rows:
        print("  none yet.")
        return
    print(f"  {'referral':<40} {'decision':<14} {'people':>6} {'claims':>6} "
          f"{'exposure':>12}  built with")
    for r in rows:
        print(f"  {r['cp_referralref']:<40} {r.get('cp_decision', ''):<14} "
              f"{r.get('cp_ringsize', 0):>6} {r.get('cp_claimcount', 0):>6} "
              f"{float(r.get('cp_exposure') or 0):>12,.0f}  {r.get('cp_language', '')}")
    print(f"\n  {len(rows)} referral(s). Everyone else's are separate rows; yours are the ones")
    print(f"  stamped with your name.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=["start", "status", "approve", "decline", "mine"])
    ap.add_argument("--claim", default=DEFAULT_SEED)
    ap.add_argument("--by", default="", help="who decided. REQUIRED for approve/decline")
    ap.add_argument("--note", default="")
    args = ap.parse_args()

    # Nothing is referred without a NAMED human. Enforced here rather than defaulted, because a
    # referral raised by "unnamed" would make that guarantee false.
    if args.command in ("approve", "decline") and not args.by.strip():
        raise SystemExit(
            f"--by is required for '{args.command}'. Nobody is accused without a named human.\n"
            f'  e.g.  python m3_run.py {args.command} --by "K. Bell (fraud desk)"')

    # An ordinary problem should read as one. A missing key otherwise arrives as forty lines of
    # traceback with the useful sentence at the bottom. `M3_TRACE=1` restores the full trace.
    def fail(exc):
        print(f"\n  {exc}\n", file=sys.stderr)
        if os.environ.get("M3_TRACE") == "1":
            raise exc
        print("  (set M3_TRACE=1 for the full stack trace)", file=sys.stderr)
        raise SystemExit(1)

    try:
        if args.command == "start":
            asyncio.run(cmd_start(args.claim))
        elif args.command == "status":
            asyncio.run(cmd_status())
        elif args.command == "approve":
            asyncio.run(cmd_decide(True, args.by, args.note))
        elif args.command == "decline":
            asyncio.run(cmd_decide(False, args.by, args.note))
        else:
            cmd_mine()
    except SystemExit:
        raise                      # our own deliberate exits, already carrying a clear message
    except Exception as exc:       # noqa: BLE001 - the point is to catch everything and explain it
        fail(exc)
