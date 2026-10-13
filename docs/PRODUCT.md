# Product scope

## Observed in the supplied source

Provena supports an organization's operational evidence workflow around a shipment. An authenticated user manages suppliers and products, creates a shipment and its six-item case checklist, generates a public supplier link, reviews submitted evidence, downloads a ZIP and optionally records an external DDS reference when completing the case.

Public suppliers use possession of a secret link rather than a user account. They submit production information, supporting files and polygon geolocation, and mark the submission complete. The internal user remains the reviewer. `Owner` and `Member` exist as membership roles; requirement review and all working-fact endpoints require authentication only, without distinct reviewer permissions, and the single role-restricted route is recording a dossier review (Owner only).

Other implemented source paths include supplier/product/shipment CSV imports, paginated case, supplier, product and shipment lists (the case list also filters), dashboard counts, request revocation/rotation and shipment archival. Persistence source is included in this revision; these remain source observations, not a verified runtime result.

## Explicit direction and boundaries

- AI may implement routine engineering; the owner decides product scope, domain rules and significant architecture changes.
- Supplier is not necessarily Producer. Producers and ProductionPlots exist as separate internal provenance records (no supplier link). Operators can record a case dossier: the case's one physical lot, producer contributions and the exact plot revisions used, as working facts only (no review or completeness decision); see [EUDR_DOMAIN](EUDR_DOMAIN.md), ADR 0002 and ADR 0006.
- Operators can also record an operator profile, a case-owned master-data copy (explicitly refreshed from the catalogue), a human applicability assessment, a recipient statement and typed evidence links as working facts. They are not a completeness, compliance or review decision; the current dossier's completeness is evaluated as a separate, read-only report under a versioned pilot policy (`GET completeness`: data coverage, evidence readiness, advisories, fingerprint; never a compliance claim), and an organization Owner can record an immutable review of exactly the dossier they loaded (accepted as complete under the policy, confirmed outside scope, confirmed unsupported flow, or returned for correction) with a Current/Stale freshness and a reviewed export built only from the stored snapshot; none of this is a legal-compliance, risk or release determination ([ADR 0008](adr/0008-completeness-facts-and-reviewed-history.md)).
- Evidence collection, format validation, internal review and regulatory conclusions are separate responsibilities. A supplier submission must never by itself mean compliance.
- A ZIP is operational documentation. The app has no DDS submission connector, deforestation analysis, country-risk provider or regulatory eligibility engine.

## Not implemented / not promised

No frontend is included here. No real email delivery, invitations, organization switching, refresh-token workflow, background worker or production hosting configuration is supplied. Multi-product shipment lines, multi-lot cases, unit conversion, per-plot quantities, risk assessment and release decisions are not modeled (evidence versions are retained as withdrawn/superseded rows and exposed through the internal evidence-history route, but only an Owner's review snapshots them). Internal producer and production-plot provenance with immutable plot revisions exists with no link to suppliers; a case's operator-entered lot attribution (internal API) pins plot revisions without determining completeness or compliance. Do not add these implicitly while implementing smaller work.

Use [API](API.md) for contracts, [EUDR_DOMAIN](EUDR_DOMAIN.md) for state semantics and [AI_DISCOVERY_REPORT](AI_DISCOVERY_REPORT.md) for prioritized gaps. This document is not a legal specification or a statement of current regulatory deadlines.
