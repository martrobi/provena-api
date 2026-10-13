namespace Provena.Api.Domain;

// Read-model vocabulary of the versioned completeness policy (ADR 0008). Deliberately free of any compliance, legality, safety or
// deforestation-free verdict: completeness only states whether the recorded facts satisfy the policy's checks.
public enum CompletenessItemStatus { Satisfied, Missing, Invalid, AwaitingReview, NotApplicable }
public enum CompletenessGroup { MasterData, Applicability, Product, Quantity, Production, Parties, Evidence }
// Axis 1: groups MasterData..Parties only. Evidence items never decide it.
public enum DataCoverage { Complete, Incomplete }
// Axis 2: there is deliberately no "Reviewed" value; a reviewed state can only come from a human review record (a later commit).
public enum EvidenceReadiness { EvidenceMissing, AwaitingRequirementDecisions, ReadyForOwnerReview }
// Axis 3 (latest human review): None = no review yet; Current = the facts still equal the reviewed ones; Stale = facts changed since. Never a compliance verdict.
public enum ReviewFreshness { None, Current, Stale }
