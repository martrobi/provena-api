# ADR 0013: Risk Assessment & Mitigation v1 (a separate retained decision layer on an immutable review)

- Date: 2026-10-16
- Decision status: Accepted
- Implementation status: **Implemented** (backend only; the separate client has not adapted). Risk Assessment & Mitigation v1 was introduced by backend implementation commit `e0836e0d338b539f1da761259188f7412c72606c` (`e0836e0`, `feat(api): add risk assessment and mitigation`), directly on top of `9ac0f08`. This documentation slice follows it; the frontend handoff is [2026-10-risk-assessment-and-mitigation](../frontend-handoffs/2026-10-risk-assessment-and-mitigation.md).
- Decision owner / approval evidence: the owner stated the rules in the Risk Assessment & Mitigation v1 implementation request. The plan artifact itself is not retained in the repository; this ADR documents the **implemented** decision, not the earlier design proposal.
- Supersedes / superseded by: supersedes nothing. It adds a layer next to [ADR 0008](0008-completeness-facts-and-reviewed-history.md) (completeness and review), [ADR 0009](0009-case-lifecycle-v2.md) (closure), [ADR 0011](0011-external-dds-record-v1.md) (External DDS record) and [ADR 0012](0012-post-completion-concerns-v1.md) (concerns). It adds one closing condition for `ExternalDdsCompleted` (amending ADR 0009's list of closing conditions narrowly, as ADR 0010 and ADR 0011 did) and changes none of the other rules of those records.

## Context and evidence

Before this decision (observed at `9ac0f08`):

- The Owner review (ADR 0008) records that the dossier facts satisfy the versioned pilot **completeness** policy (`AcceptedAsCompleteUnderPolicy`) or another of four decisions. The completeness policy states it is not a risk determination.
- Closing as `ExternalDdsCompleted` needed a Current accepted review, Matched quantities (ADR 0010) and a current External DDS record (ADR 0011). Nothing recorded a reasoned conclusion about risk, or what was done about it. See [EUDR_GAP_ANALYSIS](../EUDR_GAP_ANALYSIS.md) G20.

## Decision

**What it is.** A *risk assessment* is a human conclusion, recorded by an Owner in Provena, that the risk of the dossier version captured by one immutable review is `Negligible` or `NotNegligible`, with a finding for every criterion of a versioned catalogue and a written rationale. A *mitigation measure* is a record that a member **already performed** something in response to a `NotNegligible` assessment. Both are retained and append-only. Provena computes nothing: **no risk score, no automatic conclusion and no regulatory, legal or compliance claim.** `Negligible` is an internal Provena risk conclusion recorded by a person, not regulatory approval and not a general compliance certificate. Wording to use: "risk assessment recorded", "Negligible risk conclusion", "NotNegligible risk conclusion", "internal decision recorded in Provena".

**Why completeness and review are not a risk decision.** Six things stay separate and none implies another:

1. **Completeness** (derived, ADR 0008): which recorded facts are present under the policy.
2. **The `AcceptedAsCompleteUnderPolicy` review** (ADR 0008): the Owner states the facts satisfy the implemented completeness policy. It is **not** a Negligible-risk decision.
3. **The risk assessment** (this ADR): a judgement about risk, on the exact review.
4. **Mitigation** (this ADR): measures performed after a `NotNegligible` conclusion.
5. **The External DDS record** (ADR 0011): a recorded external submission fact. Recording it has **no** risk gate.
6. **Case completion** (ADR 0009): the closure, which for `ExternalDdsCompleted` now requires items 2, 3 (Negligible) and 5 together with Matched quantities.

A complete dossier can carry concern or insufficient information, and a risk conclusion can rest on a dossier that is complete only by presence checks, so the judgement needs its own record, actor, rationale and history.

**Why a separate retained immutable layer.** Merging the conclusion into the review would either make an immutable review carry a second, later human decision (and change the reviewed snapshot/export schema) or make a risk conclusion editable. A separate append-only layer keeps the reviewed history byte-for-byte as it was and gives reassessment its own numbered history. A later conclusion is a new `RiskAssessment`; nothing is edited.

**ReviewRecord anchoring.** Every `RiskAssessment` pins exactly one `ReviewRecord` of its own case through a case-consistent composite Restrict FK. "What was assessed" is whatever that immutable review captured (its snapshot and fingerprint). The assessment must rest on the **latest** review of the case (highest revision number), that review must have decision `AcceptedAsCompleteUnderPolicy`, must not already back a closure and must be Current.

**Reuse of `ReviewFreshnessService`; no second fingerprint.** Risk freshness is derived only through the existing review machinery and never stored: `None` (no assessment exists), `Current` (the latest assessment rests on the latest review and that review is Current) or `Stale` with reason `ReviewSuperseded` (a newer review exists) or `ReviewStale` (the review's facts changed; the review's own `changedAreas` are returned). There is no risk fingerprint, no risk staleness concept and no change to `FingerprintVersion` or the reviewed snapshot schema (both stay version 1). Consequence: a newly uploaded mitigation evidence document is a normal case document and can stale the current review through the existing document/fingerprint semantics. The normal path is then **mitigation/evidence -> new review -> new risk assessment**. If only already-reviewed evidence is used, reassessment may stay on the same Current review when every other precondition still holds.

**Entities** (tenant-owned, `Domain/Risk/RiskAssessment.cs`; all fully immutable and never deleted; no PUT, PATCH or DELETE):

- `RiskAssessment`: `Id`, `OrganizationId`, `EudrCaseId`, `Number` (per-case sequence from 1, the only definition of "latest", never a timestamp), `ReviewRecordId`, `CriteriaCatalogueId`, `CriteriaCatalogueVersion`, `Conclusion`, `Rationale` (required, non-blank, <= 4000), `ConsideredMitigationThroughNumber` (nullable), `AssessedByUserId`, `AssessedAtUtc` (server clock, truncated to the microsecond PostgreSQL stores).
- `RiskAssessmentFinding`: `RiskAssessmentId`, `CriterionCode` (<= 100), `Finding`, `Note` (<= 2000; null or non-blank). Exactly one per catalogue criterion and assessment.
- `RiskMitigationMeasure`: `Number` (per-case sequence), `RiskAssessmentId`, `Description` (required, non-blank, <= 4000), `PerformedAtUtc` (not in the future), optional `EvidenceDocumentId`, `RecordedByUserId`; the DTO's `recordedAtUtc` is the row's `CreatedAtUtc`.

There is **no mutable risk workflow status**: no column, no transition, no "open/closed" mitigation state.

**Enums** (serialized as strings; integer storage follows declaration order):

- `RiskConclusion`: `Negligible` (0), `NotNegligible` (1). There is no `MitigationRequired` conclusion, no `MitigationNotRecorded` gate and no automatic risk calculation. The Owner records the conclusion.
- `RiskCriterionFinding`: `NoConcern` (0), `Concern` (1), `InsufficientInformation` (2).
- Derived, never stored: `RiskFreshness` (`None`, `Current`, `Stale`) and `RiskStaleReason` (`ReviewSuperseded`, `ReviewStale`).

**Finding rules** (pure, `RiskAssessmentRules`; 400 with `errors.<Property>`, messages never echo client values): every catalogue criterion exactly once; no unknown and no duplicate code; `finding` required and defined; `Concern` requires a nonblank note; `InsufficientInformation` requires a nonblank note; `Negligible` together with any `InsufficientInformation` finding is invalid (self-contradictory only; it is not a sufficiency algorithm). **`Concern` together with `Negligible` is valid**: a concern is a human-observed indicator, not an automatic workflow requirement, and the Owner stays accountable through the finding notes and the rationale.

**Criteria catalogue.** A static, code-defined, versioned catalogue (`Services/RiskCriteriaCatalogue.cs`): id `provena-pilot-risk-criteria`, version `1`, seven criteria:

| Code | Title | Guidance |
| --- | --- | --- |
| `PRODUCTION_COUNTRY_AND_AREA` | Production country and area | Country classification, presence of forest, and prevalence of deforestation or forest degradation in the countries and areas of production. |
| `INDIGENOUS_PEOPLES_AND_LAND_RIGHTS` | Indigenous peoples and land rights | Presence of indigenous peoples, consultation and cooperation, and substantiated land-tenure or land-use claims. |
| `GOVERNANCE_AND_LEGALITY_CONTEXT` | Governance and legality context | Corruption, document falsification, law enforcement, human rights, armed conflict or sanctions concerns in the production context. |
| `EVIDENCE_RELIABILITY` | Reliability of the collected information | Whether the collected information and documents are reliable and valid, beyond the presence checks of the completeness policy. |
| `SUPPLY_CHAIN_COMPLEXITY_AND_MIXING` | Supply-chain complexity and mixing | Complexity of the supply chain, risk of circumvention, and risk of mixing with products of unknown or unassessed origin. |
| `CONCERNS_AND_PAST_NONCOMPLIANCE` | Concerns and past non-compliance | Substantiated concerns and any past non-compliance of the supplier, the producers or others in the supply chain. |
| `OTHER_RISK_INDICATORS` | Other risk indicators | Any other relevant indicator, including certification or third-party verification information considered. |

**The catalogue is a structured prompt list for human judgement.** It is grouped after the Article 10(2) themes summarised in the gap analysis, but it is **not** an automated Article 10 legal-compliance engine, claims no legal coverage of Article 10, has not had its wording legally reviewed and does not compute the conclusion. An assessment keeps the catalogue id and version it was recorded under; changing a criterion needs a new version. The request carries `criteriaCatalogueVersion`; a version other than the current one is 409 `RiskCriteriaCatalogueChanged`.

**Mitigation semantics.** A `RiskMitigationMeasure` represents something **already performed**. It is **not a task**: no assignment, due date, priority, reminder, mutable status, Kanban or work queue. It may be recorded by any member, must anchor to the exact latest assessment (`expectedRiskAssessmentId`), is allowed only while that latest assessment is `NotNegligible`, and may link one **current** evidence document of the same case. If that document is withdrawn later, the link and the measure stay readable. A correction is another immutable measure. The measure records a traceability anchor, not sufficiency: no rule says that any measure mitigates any criterion.

**`ConsideredMitigationThroughNumber`.** Set by the server to the number of the latest mitigation measure existing when the assessment is recorded (null when none existed). It proves only that mitigation records 1..N existed at assessment time, under the case lock and the optimistic-history contract (the request named measure N as the latest). It does **not** prove that the Owner read them, their relevance, effectiveness or sufficiency, or that a measure mitigated a particular criterion. No conclusion is derived from it.

**Residual risk.** There is no separate residual-risk object. After mitigation the Owner records a **new assessment**; a `Negligible` later assessment on a Current review is the recorded residual-risk decision.

**Authorization.** `GET` risk data: any authenticated organization member. `POST` risk assessment: **Owner only** through the shared `OwnerGate` (live `OrganizationUsers` check, the JWT role is not trusted); authorization happens **before case lookup and before the body is read**, so a Member gets 403 whatever the case id or body. `POST` mitigation: any authenticated member. No new role exists. Supplier and public routes expose no risk data, and no request or response carries `organizationId`.

**Concurrency.** Both writes run in one transaction under the tenant-scoped case lock. Order for an assessment: live Owner check (403); case lock (404); mutable case (409, `EnsureMutable`); body (400/415); validators and finding rules (400); catalogue version (409 `RiskCriteriaCatalogueChanged`); `expectedLatestRiskAssessmentId` (409 `RiskAssessmentHistoryChanged`); `expectedLatestMitigationMeasureId` (409 `MitigationHistoryChanged`); latest review (409 `ReviewNotLatest`); review already backs a closure (409 `ReviewAlreadyBacksClosure`); review not accepted (409 `ReviewNotAccepted`); stale review (409 `StaleReview`, with `changedAreas`); write. Both expected ids are `null` when none existed, otherwise the id the Owner loaded. For a mitigation: case lock; mutable case; body; validators and the not-in-the-future rule (400); `expectedRiskAssessmentId` equals the latest assessment (409 `RiskAssessmentChanged`); latest is `NotNegligible` (409 `MitigationNotExpected`); evidence current (409 `EvidenceDocumentNotCurrent`); write. The unique per-case `Number` indexes are database backstops. The client must not retry silently: if history changed it refreshes and lets the user consciously retry.

**Closure gate.** Closing as `ExternalDdsCompleted` additionally requires the **latest** risk assessment of the case to exist (409 `RiskAssessmentMissing`), to rest on exactly the latest review (409 `RiskAssessmentReviewMismatch`) and to be `Negligible` (409 `RiskNotNegligible`). Order inside Close, after the lifecycle checks: latest-review/expected-review match, `ReviewAlreadyBacksClosure`, outcome mapping (`ReturnedForCorrection` cannot close), `StaleReview`, `QuantityNotReconciled`, `RiskAssessmentMissing`, `RiskAssessmentReviewMismatch`, `RiskNotNegligible`, `ExternalDdsRecordMissing`, `ExternalDdsReviewMismatch`, `ExternalDdsReferenceMismatch`, then the closure write. The gate reads **no timestamps**: there is no chronology rule between the External DDS `submittedAtUtc` and `AssessedAtUtc` (the server time of an assessment says nothing about when the human judgement was made). The closure pins both the exact `ReviewRecordId` and the exact `RiskAssessmentId` (`CaseClosureDto.riskAssessmentId`; null for legacy closures and for the other outcomes). `ConfirmedOutsideRegulatoryScope` and `ConfirmedUnsupportedFlow` have no risk gate and keep `riskAssessmentId` null. **Recording the External DDS record itself has no risk gate.** The failure ProblemDetails carries a safe summary (`riskAssessment { assessmentId, number, reviewId, conclusion }`) and never the rationale or finding notes. The existing Close conflict "this review already backs a closure" is now machine-readable as `ReviewAlreadyBacksClosure` with its pre-existing detail text unchanged; risk-assessment creation uses the same typed conflict.

**Reopen interaction.** Reopening (ADR 0009) is unchanged and risk does not create or trigger a reopen. Reviews, risk assessments, mitigations, External DDS records and closures all stay immutable and readable. After a reopen, the old review may still temporarily be the latest and Current, but it already backs a closure; therefore a new risk assessment on it fails `ReviewAlreadyBacksClosure`, and so does closing on it. The required path for a new `ExternalDdsCompleted` closure is: **new review -> new risk assessment -> quantity/risk/DDS gates -> new closure**. A closed or archived case keeps the risk history readable and rejects new assessments and mitigations (409, `EnsureMutable`).

**Evidence retention.** `RiskMitigationMeasure.EvidenceDocumentId` must be a **current** (not withdrawn) document of the **same case** when linked; otherwise 409 `EvidenceDocumentNotCurrent`, identical for withdrawn, unknown, other-case and other-tenant ids. The database enforces the same-case/same-tenant relation with a composite Restrict FK to `Documents (OrganizationId, EudrCaseId, Id)`. A later withdrawal sets only `WithdrawnAtUtc` and keeps the link.

**Audit.** Two events, safe metadata only: `RiskAssessmentRecorded` (assessment id and number, review id, conclusion name, catalogue id and version, considered-mitigation number) and `RiskMitigationRecorded` (measure id and number, assessment id and number). The existing `CaseClosed` event additionally names the `riskAssessmentId` for an `ExternalDdsCompleted` closure. Audit text **never** contains the rationale, finding notes, mitigation description or document names, and makes no regulatory claim.

**Database constraints** (details in [DATABASE](../DATABASE.md)). Migration `20261016000117_RiskAssessmentAndMitigation`: tables `RiskAssessments`, `RiskAssessmentFindings`, `RiskMitigationMeasures` and column `CaseClosures.RiskAssessmentId`. All foreign keys are composite and tenant/case-aware with Restrict. The decisive invariant: `CaseClosures (OrganizationId, EudrCaseId, ReviewRecordId, RiskAssessmentId)` references the alternate key `RiskAssessments (OrganizationId, EudrCaseId, ReviewRecordId, Id)`, so the database guarantees that a closure cannot reference an assessment made on another review (or case or organization); legacy closures and other outcomes keep the column null. Further backstops: unique per-case assessment and mitigation numbers, unique finding per criterion, a filtered unique index on `CaseClosures.RiskAssessmentId` (an assessment backs at most one closure), CHECK `CK_CaseClosures_RiskAssessmentOutcome` (only an `ExternalDdsCompleted` closure, outcome 0, carries an assessment), nonblank rationale/description/note CHECKs, the note-required CHECK and the application-level `EnforceRiskHistoryImmutable` guard. **Down** is refused while any risk row exists or a closure references an assessment. No backfill: no assessment is invented for any existing case, and legacy `ExternalDdsCompleted` closures keep a null `riskAssessmentId`.

**Working export vs reviewed export.** The working export `manifest.json` gains a `risk` section built by the same read model as `GET risk` under the export's case lock (`freshness`, `staleReason`, `changedAreas`, `latestReviewId`, `latestReviewBacksClosure`, `latestAssessmentId`, the full `assessments` history with findings, and the full `mitigations` history, each ordered oldest first by `number`) and a `riskScope` text. It may contain the full rationale, finding notes and mitigation descriptions because it is the retained operator record. The **immutable reviewed export was not changed**: risk data is not injected into the reviewed snapshot or the review export, and the tests prove the reviewed export content and schema are unchanged except its existing export timestamp.

## Alternatives considered

- **Treat `AcceptedAsCompleteUnderPolicy` as a risk decision.** Rejected: it states policy completeness only; a complete dossier can carry concern.
- **Store the conclusion on the review or case, or add risk to the reviewed snapshot.** Rejected: reviews are immutable and the reviewed export must stay stable.
- **A second risk fingerprint and staleness concept.** Rejected: the pinned review already provides freshness and `changedAreas`.
- **An automatic or scored conclusion, or a `MitigationRequired` conclusion / `MitigationNotRecorded` gate.** Rejected: the Owner records the conclusion; Provena does not decide that a measure is required or sufficient.
- **Reject `Negligible` whenever a `Concern` finding exists.** Rejected: that would be a hidden scoring rule. Only the self-contradiction with `InsufficientInformation` is rejected.
- **Mitigation as a task (assignee, due date, priority, status).** Rejected: a measure is a record of something performed.
- **A mutable assessment or measure, or a residual-risk status.** Rejected: corrections and reassessment are new rows.
- **A chronology rule between the External DDS record and the assessment.** Rejected: server timestamps do not say when the judgement was made; the pinned review ties both to the same dossier version.
- **A risk gate on recording the External DDS record or on the other closure outcomes.** Rejected: only `ExternalDdsCompleted` closing is gated.
- **Owner-only mitigation.** Rejected: any member may record what was done; the Owner is accountable for the later assessment.

## Consequences

- An `ExternalDdsCompleted` closure now needs a Current accepted review, Matched quantities, a Negligible assessment of that exact review and a current External DDS record of that review.
- A `NotNegligible` conclusion blocks `ExternalDdsCompleted`; the way forward is mitigation and reassessment, and possibly evidence, a new review and a new assessment.
- Four internal routes (`GET risk`, `GET risk-assessments/{id}`, `POST risk-assessments`, `POST risk-mitigations`), one migration, two audit event types, one added field on `CaseClosureDto` (`riskAssessmentId`), a typed `ReviewAlreadyBacksClosure` reason on the existing close conflict and a `risk` section in the working export.
- Lists are unpaginated per-case histories (pilot scale, like the other per-case histories).
- Guards are application-level; the database backstop is the composite Restrict FKs, unique indexes and CHECKs.
- Existing closed cases and legacy closures are untouched; a reopened case needs a new review and a new assessment before it can close as `ExternalDdsCompleted` again.
- Nothing here is a legal-compliance, regulatory-approval, release or DDS decision.

## Deferred scope

Not implemented in v1: legally reviewed Article 10 catalogue wording; automatic official country-risk data (G19) and structured per-country classification; Article 13 simplified due-diligence eligibility; automated risk scoring or an automatic conclusion; cross-case and supplier-wide aggregation; an annual organization-level due-diligence review; a separation-of-duties role (the Owner who reviews may also assess); an automated mitigation workflow or tasks; risk data in the reviewed export or snapshot; per-criterion evidence links; a regulatory approval determination; retention or erasure of risk rows; pagination of risk history.

## Implementation and verification

- Implementation: commit `e0836e0d338b539f1da761259188f7412c72606c`. Sources: `Domain/Risk/RiskAssessment.cs` (three entities, two enums), `Contracts/Risk.cs` (requests, responses, `RiskFreshness`, `RiskStaleReason`; `CreateRiskAssessmentValidator` and `RecordRiskMitigationValidator` in `Contracts/Validators.cs`), `Services/RiskCriteriaCatalogue.cs`, `Services/RiskAssessmentRules.cs`, `Services/RiskAssessmentService.cs`, `Services/CaseClosurePolicy.cs` (`RiskError`, `ReviewAlreadyBacksClosure`), `Services/CaseLifecycleService.cs` (`Close`), `Services/ExportService.cs`, `Endpoints/Cases/RiskEndpoints.cs` (registered by `MapProvenaEndpoints`), mapping and `EnforceRiskHistoryImmutable` in `Data/AppDbContext.cs`, migration `Data/Migrations/20261016000117_RiskAssessmentAndMitigation.cs`.
- Tests: `RiskDomainTests` (unit: finding rules, validators, closure risk policy), `RiskTests` (HTTP and PostgreSQL: authorization, shapes, conflict order and reasons, freshness, reopen path, mitigation, close gate and precedence, audit, tenant isolation, concurrency, working and reviewed export), `RiskModelTests` (guards, constraints, composite FKs), the risk migration tests in `MigrationTests`, and the route rows in `EndpointInventoryTests` (see [TESTING](../TESTING.md)).
- Verification: `scripts/verify.ps1` on `e0836e0` plus this documentation slice: Release build with warnings as errors, **250 unit and 329 integration tests passed**.
- The local client compatibility suite has not been run.

## Revisit trigger

Legal review of the Article 10 wording or a catalogue change (new catalogue version); official country-risk data or Article 13 eligibility; a need to correct or withdraw a recorded assessment or measure; separation of duties between reviewer and assessor; an organization-level annual review; supplier-wide or cross-case risk aggregation; per-criterion evidence links; mitigation tasks or deadlines; risk content in the reviewed export; retention or erasure rules; pagination if history grows.
