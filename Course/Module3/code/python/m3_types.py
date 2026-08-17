"""Message types for the fraud desk.

DEMONSTRATION CODE - part of the Claims 365 training lab.

WHY THEY LIVE IN THEIR OWN MODULE, and it is not style: a checkpoint tags every value
`module:QualName` and refuses to restore anything off its allowlist. Defined in the script you run,
the tag would be `__main__:ReferralProposal`, so a checkpoint written by one command would be
unreadable by the next - silently. Here it is always `m3_types:ReferralProposal`.
"""
from dataclasses import dataclass, field


# --------------------------------------------------------------- one type per edge
# Delivery is BY TYPE: two executors accepting the same type would both receive the message, a bug
# that produces no output and no error. Every edge carries its own type.
@dataclass
class SeedClaim:
    """-> intake. Just a reference. Nothing has been checked yet."""
    claim_ref: str


@dataclass
class VerifiedClaim:
    """intake -> investigate. The claim exists in the book, and here is what it says.

    A different type from SeedClaim on purpose: if intake re-sent what it received, the graph would
    work today and break the moment anyone added a second consumer.
    """
    claim_ref: str
    claimant_ref: str
    repairer: str
    amount_claimed: float


@dataclass
class LinkedClaimant:
    claimant_ref: str
    full_name: str
    why_linked: str
    link_type: str                    # seed | bank_account | phone | postcode
    # Who they share it WITH - the challenge step judges links between members, not attributes.
    shared_with: list = field(default_factory=list)


@dataclass
class TraversalComplete:
    """investigate -> challenge. Candidates, NOT conclusions."""
    seed_claim_ref: str
    candidates: list[LinkedClaimant] = field(default_factory=list)
    hops: int = 0
    narrative: str = ""


@dataclass
class JudgedClaimant:
    claimant_ref: str
    reason: str
    strength: str             # strong | weak


@dataclass
class ReferralProposal:
    """challenge -> refer. What survived the argument."""
    seed_claim_ref: str
    confirmed: list[JudgedClaimant] = field(default_factory=list)
    excluded: list[JudgedClaimant] = field(default_factory=list)
    recommend_refer: bool = False
    rationale: str = ""
    hops: int = 0
    claim_refs: list[str] = field(default_factory=list)
    exposure_gbp: float = 0.0


# --------------------------------------------------------------- the human gate
@dataclass
class ReferralApprovalRequest:
    """The desk stops here. Accusing a customer is as consequential as paying them, so a person
    decides - and the process may wait days."""
    seed_claim_ref: str
    claimant_count: int
    claim_count: int
    exposure_gbp: float
    rationale: str
    confirmed_refs: list[str] = field(default_factory=list)
    excluded_refs: list[str] = field(default_factory=list)


@dataclass
class ReferralDecision:
    approved: bool
    approver: str = ""
    note: str = ""


@dataclass
class FraudOutcome:
    seed_claim_ref: str
    decision: str             # referred | not_referred | dismissed
    claimant_count: int
    claim_count: int
    exposure_gbp: float
    hops: int
    approver: str
    referral_ref: str = ""
    audit_trail: list[str] = field(default_factory=list)


CHECKPOINT_TYPES = [f"m3_types:{c.__name__}" for c in (
    SeedClaim, VerifiedClaim, LinkedClaimant, TraversalComplete, JudgedClaimant, ReferralProposal,
    ReferralApprovalRequest, ReferralDecision, FraudOutcome,
)]
