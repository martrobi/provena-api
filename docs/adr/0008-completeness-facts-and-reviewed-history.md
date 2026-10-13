# ADR 0008: Completeness facts, case master data and reviewed dossier history

- Date: 2026-10-13
- Decision status: Accepted
- Implementation status: **Partial.** Commit 1 is implemented: `OperatorProfile`, `Supplier.PostalAddress`, `CaseMasterDataVersion` (copy, refresh, drift, shipment binding), `CaseApplicability` (with the derived status), `CaseRecipientStatement`/`CaseRecipient`, `CaseEvidenceLink`, their guards, migration `CompletenessFacts` and the working-fact routes. **Not implemented (commits 2 and 3):** the completeness evaluator and `GET completeness`, `CaseRevision`, `ReviewRecord`, the Owner review gate, the fingerprint and staleness, the reviewed export and the `CK_CaseRevisions_DossierLotPair` check constraint. Sections below that describe those are the approved target, not current behaviour.
- Decision owner / approval evidence: Owner approval of `docs/plans/2026-10-completeness-reviewed-history.md` (revision 3 as supplied with the commit 1 request) with the recommended pilot defaults D1-D7.
- Supersedes / superseded by: builds on [ADR 0005](0005-retained-evidence-append-only-rows.md), [ADR 0006](0006-production-provenance-plot-revisions.md) and [ADR 0007](0007-case-dossier-lot-attribution.md); supersedes none of them.

## Context and evidence

Slices 1-4 retain evidence, model producer/plot provenance and record a case's lot with pinned plot revisions. Provena still cannot say whether the *working* dossier is factually complete under a stated policy, nor exactly what an accountable human reviewed. Party and product facts live in shared, in-place-edited catalogue rows (`CatalogService` takes no case lock), `Organization` has no operator identity, and nothing records the human assessment of scope or the recipients. Source: `Domain/Compliance/*`, `Data/AppDbContext.cs`, `Services/CaseMasterDataService.cs`, `Services/CaseApplicabilityService.cs`, `Services/ApplicabilityDerivation.cs`, `Data/Migrations/*CompletenessFacts*`.

## Decision

Implemented in commit 1:

- **Case-owned master-data copy with explicit refresh (`CaseMasterDataVersion`).** The values a case relies on (operator, supplier, product) are copied into an immutable, versioned, case-owned row (sequential `Number`, one current per case via a partial unique index, one-way `SupersededAtUtc`). Shared catalogue edits never change the copy; only a deliberate, audited `POST .../master-data/refresh` creates the next version (`CaseMasterDataRefreshed`). The refresh runs under the case lock on a case that is still editable, reads the **current shipment** under that lock, and locks `OperatorProfile`, the shipment's `Supplier` and `Product` `FOR SHARE`. If every copied field (context fields included) equals the current version it returns that version with no new row and no audit event.
- **Shipment binding.** `SupplierId`/`ProductId` of the copy come from the case's current shipment, never from the client, and carry tenant-safe composite FKs. A shipment may change supplier/product while the case has no dossier, so the copy is only valid while `copy.SupplierId == shipment.SupplierId && copy.ProductId == shipment.ProductId`; this is exposed as `shipmentMatch` and enforced on applicability saves (409 until refreshed). The Slice 4 shipment lock is unchanged.
- **Drift is advisory.** `drift` compares the live operator profile and the copy's own supplier and product with the copy over fact-bearing fields only (`ProductName` and `ProductCatalogueDescription` are context). It is computed on read, never mutates the case, never refreshes and never uses `UpdatedAtUtc`. Its role in review preconditions belongs to commit 3.
- **Applicability is a recorded human assessment, not a determination by Provena.** Immutable versions; the assessed product identity (`AssessedProductId`, `AssessedCnCode`, `AssessedCommodity`) is copied by the server from the **matching master-data copy**, never from the client and never from the live `Product`. `ProductDescription` is the authoritative assessed trade description and is distinct from the reusable catalogue `Product.Description`. `OperatorRole` is `Operator` or `OtherUnsupported` (the latter always requires a role description and always derives `UnsupportedFlow`).
- **Derived applicability status** (pure function `ApplicabilityDerivation.Derive`, not a rules engine, never inferred from CN format): `NotRecorded`, then `ProductChanged` when the assessed `ProductId`, CN code or commodity differ from the current copy (so a supplier-only or operator-only refresh never causes it, while a different Product with the same CN code and commodity does), then `Pending`, `OutsideRegulatoryScope` (`FlowSupport` ignored), and for `InScope`: `Pending`, `UnsupportedFlow` (recorded, or the policy-v1 envelope rejects Wood/Cattle/Other, Export, a non-single-commodity flow or a non-Operator role) or `WithinSupportedScope`.
- **Recipients:** versioned statement, `RecipientsIdentified` (1-50 complete recipients) or `NotApplicable` (no recipients, recorded basis). No statement means unresolved and is never silently converted.
- **Evidence links record presence only.** A `CaseEvidenceLink` says a retained document of the *same case* is offered for a purpose (`Legality`, `DeforestationFree`, `Applicability`, `ProductionSource`). It does not mean reviewed, sufficient, compliant, deforestation-free or legal. Withdrawn (one-way, with actor), never deleted; one current link per (case, document, purpose). The same-case guarantee is database-enforced by a new `Documents (OrganizationId, EudrCaseId, Id)` alternate key and a composite FK.
- **Operator profile:** one per organization, mutable, deletion rejected. It is a *source* for the copy, never a case fact. EORI is stored, not evaluated, and not DDS readiness.
- **Persistence guards (application-level, like ADR 0005-0007):** `CaseMasterDataVersion`, `CaseApplicability` and `CaseRecipientStatement` join the `Retirement` map (`SupersededAtUtc`), `CaseEvidenceLink` too (`WithdrawnAtUtc` with `WithdrawnByUserId`), `CaseRecipient` is fully immutable, `OperatorProfile` deletes are rejected. Other save overloads, bulk writes and raw SQL bypass them; the Restrict FKs and unique indexes are the database backstop.
- **Working-fact write protocol:** transaction, case lock `FOR UPDATE` (404), `EnsureEditable` (409), body validation (400), then supersede, save and insert; identical normalized content is a 200 no-op without a version or audit event. Terminal (Completed/Archived) cases reject every write, including refresh. Audit text carries identifiers only.
- **Additive migration `CompletenessFacts`** with a `DO $$` Down guard that refuses once any new table has a row or any supplier has a postal address. No backfill and no fabrication: legacy cases have no copy, applicability, recipients or links.

Approved target, **not yet implemented**: a versioned pilot completeness policy and evaluator (`CompletenessEvaluator`, `DossierFactsBuilder`, `GET completeness` with three axes: data coverage, evidence readiness, review freshness); immutable reviewed snapshots (`CaseRevision`, hybrid pinned rows plus `SnapshotJson`) and `ReviewRecord` with the four decisions (`AcceptedAsCompleteUnderPolicy`, `ConfirmedOutsideRegulatoryScope`, `ConfirmedUnsupportedFlow`, `ReturnedForCorrection`), an Owner-only live membership gate, a fingerprint over case-owned facts with algorithm versioning, staleness (a pinned plot revision superseded later does *not* stale a review), a reviewed export built only from the immutable snapshot, and the one deliberate CHECK constraint `CK_CaseRevisions_DossierLotPair`. Acceptance would additionally require no catalogue drift. These will be recorded as implemented when their commits land.

Deliberately undecided: risk assessment and mitigation, release/approval, DDS and EU Information System semantics, downstream/trader workflows, cattle establishments, wood species, composite products, Article 4a, split/merge, coordinate-precision evaluation, per-source evidence links, document hashing/WORM.

## Alternatives considered

- **Live catalogue reads for party and product facts.** Rejected: any member's catalogue edit would silently change a case's facts (and, later, a reviewed fingerprint) without a case lock or audit trail.
- **Freeze the facts when the case completes (B).** Rejected: needs a second frozen code path and still lets an unrelated shared-row edit act as an unreviewed change to an open case.
- **Fingerprint only case-owned data and ignore party facts (B').** Rejected: loses reproducibility of the facts a review relied on.
- **Assessed product facts read from the live `Product`.** Rejected: the assessment would drift with the catalogue; it is copied from the case's own master-data copy.
- **Product identity by CN code and commodity only.** Rejected: two products with the same CN code and commodity are different goods; `AssessedProductId` is part of the comparison.
- **Treating a document link as evidence sufficiency.** Rejected: sufficiency is a human legal judgement, not a stored per-document finding.

## Consequences

- A supplier or product referenced by any master-data copy or applicability can no longer be physically deleted (FK Restrict; surfaces as the generic 409 for a referenced record), even if a shipment no longer uses it.
- Existing-table changes are only `Suppliers.PostalAddress` (nullable, max 1000, optional trailing field of `SupplierInput`, CSV header unchanged) and the `Documents` alternate key. A supplier PUT that omits `postalAddress` clears it (complete-replacement semantics, as for the other optional supplier fields).
- Legacy cases keep working unchanged; applicability needs a master-data refresh first.
- Rollback of `CompletenessFacts` is possible only while the six tables are empty and no supplier has a postal address.
- No requirement status, case status or `Complete` eligibility changes; nothing here is a legal-compliance claim.

## Implementation and verification

Source: `Domain/Compliance/*`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Services/{OperatorProfile,CaseMasterData,CaseApplicability,CaseRecipient,CaseEvidenceLink}Service.cs`, `Services/ApplicabilityDerivation.cs`, `Endpoints/Compliance/*`, `Data/Migrations/*CompletenessFacts*`. Tests: unit `CompletenessFactsDomainTests`; integration `CompletenessFactsModelTests` (guards, partial indexes, raw-SQL FK violations, tenant filter), `CompletenessFactsTests` (HTTP behaviour), `MigrationTests` (schema, upgrade, Down guard per category), the endpoint inventory and OpenAPI pins.

## Revisit trigger

A change to the pilot flow envelope (D1), the geolocation or production-period rules (D2/D3), recipient or legality-evidence requirements (D4/D7), who may record applicability or review (D5), the review/lifecycle relationship (D6), or any move towards risk, release or DDS semantics.
