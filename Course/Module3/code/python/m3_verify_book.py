"""Check the claims book behaves as expected, reading it from Dataverse.

DEMONSTRATION CODE - part of the Claims 365 training lab.

The whole module rests on one property of the data: a single hop cannot see the fraud ring, and
following the link the last hop suggested can. This confirms that property through the same
functions the agent's tools use, so you can trust the exercise before spending a model call on it.

No model calls, so it is free to run and safe to re-run.

    python m3_verify_book.py
"""
import m3_dataverse as dv

# The three identifiers separate households do not normally share. Email is excluded: everyone has
# their own, so it links nobody.
LINKS = ("bank_account", "phone", "postcode")
COL = {"bank_account": "cp_bankaccount", "phone": "cp_phone", "postcode": "cp_postcode"}

# The claim triage flagged, and the busy garage that proves volume is not a link.
SEED = "BCL-2026-0201"
INNOCENT = "Haldane Accident Repair"


def expand(known: dict) -> dict:
    """One hop: everyone sharing any link attribute with anyone we already know.

    Deliberately the naive version - no batching, no model. It is obviously correct, so if it and
    the agent ever disagree, the agent is what changed.
    """
    out = dict(known)
    for ref, c in list(known.items()):
        for attr in LINKS:
            for other in dv.claimants_sharing(attr, c[COL[attr]]):
                out[other["cp_claimantref"]] = other
    return out


def main() -> None:
    print(f"signed in as {dv.whoami()}")
    seed = dv.get_claim(SEED)
    if seed is None:
        raise SystemExit(f"{SEED} not found. The claims book has not been loaded into Dataverse.")
    start = dv.get_claimant(seed["cp_claimantref"])
    print(f"seed claim {SEED}: {start['cp_fullname']} at {seed['cp_repairername']}, "
          f"GBP {float(seed['cp_amountclaimed']):,.0f}")

    print("\n--- the obvious first hop: same repairer ---")
    rep = dv.claims_by_repairer(seed["cp_repairername"])
    rep_people = {c["cp_claimantref"] for c in rep}
    print(f"  {len(rep)} claims, {len(rep_people)} claimants. Too many to accuse.")

    print("\n--- following the links ---")
    known = {start["cp_claimantref"]: start}
    hops = 0
    while hops < 5:
        nxt = expand(known)
        if len(nxt) == len(known):
            break
        hops += 1
        known = nxt
        print(f"  hop {hops}: {len(known)} claimant(s)")

    claims = [c for r in known for c in dv.claims_of(r)]
    print(f"\n  reached {len(known)} claimants over {hops} hops, {len(claims)} claims, "
          f"GBP {sum(float(c['cp_amountclaimed']) for c in claims):,.0f}")

    print("\n--- the control: the big innocent repairer ---")
    inn = dv.claims_by_repairer(INNOCENT)
    inn_people = sorted({c["cp_claimantref"] for c in inn})
    one = dv.get_claimant(inn_people[0])
    grew = expand({one["cp_claimantref"]: one})
    print(f"  {len(inn)} claims, {len(inn_people)} claimants at {INNOCENT}")
    print(f"  traversing from {one['cp_claimantref']} reaches {len(grew)} claimant(s)")

    print()
    # Assert the PROPERTY, never an exact count - the bounds are wide on purpose.
    checks = [
        ("the repairer hop returns a haystack", len(rep_people) > 20),
        ("following the links converges", 1 < hops <= 4),
        ("it reaches more than one hop's worth", len(known) > 3),
        ("the innocent cluster has volume", len(inn_people) > 30),
        ("and no links at all", len(grew) == 1),
    ]
    for label, ok in checks:
        print(f"  [{'PASS' if ok else 'FAIL'}]  {label}")
    print("\nRESULT:", "the book behaves in Dataverse as it did on disk"
          if all(o for _, o in checks) else "FAILED")


# Guarded, so importing this file does not fire Dataverse queries as a side effect.
if __name__ == "__main__":
    main()
