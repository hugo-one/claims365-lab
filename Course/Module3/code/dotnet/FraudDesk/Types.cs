// Message types for the fraud desk.
//
// DEMONSTRATION CODE - part of the Claims 365 training lab.
//
// One type per edge, exactly as in the Python track. Delivery is by type: if two executors accept
// the same type, both receive the message, which is a class of bug that produces no output and no
// error. Intake takes SeedClaim and emits VerifiedClaim rather than re-sending what it received,
// which is what keeps every edge distinct.
namespace FraudDesk;

public sealed record SeedClaim(string ClaimRef);

public sealed record VerifiedClaim(
    string ClaimRef, string ClaimantRef, string Repairer, double AmountClaimed);

public sealed record LinkedClaimant(
    string ClaimantRef, string FullName, string WhyLinked, string LinkType,
    List<string> SharedWith);

public sealed record TraversalComplete(
    string SeedClaimRef, List<LinkedClaimant> Candidates, int Hops, string Narrative);

public sealed record JudgedClaimant(string ClaimantRef, string Reason, string Strength);

public sealed record ReferralProposal(
    string SeedClaimRef, List<JudgedClaimant> Confirmed, List<JudgedClaimant> Excluded,
    bool RecommendRefer, string Rationale, int Hops, List<string> ClaimRefs, double ExposureGbp);

// The human gate. Accusing a customer of fraud is as consequential as paying them, so it is a
// person's decision, and the desk may wait days for it.
public sealed record ReferralApprovalRequest(
    string SeedClaimRef, int ClaimantCount, int ClaimCount, double ExposureGbp,
    string Rationale, List<string> ConfirmedRefs, List<string> ExcludedRefs);

public sealed record ReferralDecision(bool Approved, string Approver, string Note);

public sealed record FraudOutcome(
    string SeedClaimRef, string Decision, int ClaimantCount, int ClaimCount,
    double ExposureGbp, int Hops, string Approver, string ReferralRef,
    List<string> AuditTrail);
