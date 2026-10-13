# Production provenance: producers, production plots and plot revisions

## 1. Summary

Eleven new internal endpoints manage **producers**, **production plots** and immutable **plot revisions**. A production plot is a producer-scoped plot registration; its location data (country of production, geometry, optional declared area) lives only in numbered plot revisions, of which exactly one is current. The change is **additive**: no existing endpoint, DTO or status code changes. Nothing is linked to suppliers, shipments or cases yet.

## 2. Changed endpoints or behaviour

All routes are authenticated, internal (`/api`), JSON only. There are no DELETE routes and no revision PUT/DELETE routes.

| Endpoint | Behaviour | Kind |
| --- | --- | --- |
| `GET /api/producers?search&isActive&page&pageSize` | `PageResult<ProducerDto>`, ordered name then id | New |
| `GET /api/producers/{id}` | `ProducerDto` | New |
| `POST /api/producers` | 201 + absolute `Location`; the producer is always created active | New |
| `PUT /api/producers/{id}` | 200 `ProducerDto`; edits descriptive fields and `isActive`, which must be sent explicitly | New |
| `GET /api/production-plots?producerId&search&isActive&page&pageSize` | Tenant-wide `PageResult<ProductionPlotListItem>`, ordered name then id | New |
| `GET /api/production-plots/{id}` | `ProductionPlotDto` with the full current revision including geometry | New |
| `POST /api/production-plots` | Creates the plot and its revision 1 in one step; 201 + `Location` | New |
| `PUT /api/production-plots/{id}` | 200 `ProductionPlotDto`; edits name, reference, notes, `isActive` only; `isActive` must be sent explicitly | New |
| `POST /api/production-plots/{plotId}/revisions` | Creates the next revision; 201 + `Location` of the revision | New |
| `GET /api/production-plots/{plotId}/revisions` | All revisions, newest first, unpaginated, without geometry | New |
| `GET /api/production-plots/{plotId}/revisions/{revisionId}` | One revision (current or superseded) including geometry | New |

## 3. Request/query contract

- List queries: `page` (default 1), `pageSize` (default 25, 1–100), `search` (max 200 characters; producers and plots match name or external reference, case-insensitive), `isActive` (boolean, omitted = both), plots also `producerId`. Invalid values give 400. An unknown or foreign `producerId` filter returns an empty page, not an error.
- `ProducerInput` (POST/PUT): `{ name (required, max 200), externalReference? (max 100), countryCode? (^[A-Z]{2}$, the producer's own country), notes? (max 4000), isActive (**required on PUT**, 400 if omitted or null; ignored on POST, which always creates an active producer) }`. `externalReference` is unique per organization when set (409 on duplicates).
- `ProductionPlotInput` (POST): `{ producerId, name (required, max 200), externalReference? (max 100, unique when set), notes? (max 4000), initialRevision: PlotRevisionInput }`.
- `ProductionPlotUpdateInput` (PUT): `{ name, externalReference?, notes?, isActive (**required**, 400 if omitted or null) }`. **`producerId` cannot be changed** (it is ignored if sent). A producer or tenure change means registering a new plot.
- `PlotRevisionInput`: `{ countryCode (required, ^[A-Z]{2}$, country of production), geometry (a bare GeoJSON geometry object), areaHectares? (declared, > 0, max 4 decimal places), note? (max 4000) }`.
- `geometry` must be one `Point`, `Polygon` or `MultiPolygon` object (3D positions allowed, only longitude/latitude are checked). `Feature`, `FeatureCollection` and every other type are rejected with 400. Maximum geometry size 5 MiB.

```json
POST /api/production-plots
{ "producerId": "…", "name": "North field", "externalReference": "PLOT-001",
  "initialRevision": { "countryCode": "BR", "areaHectares": 12.5,
    "geometry": { "type": "Polygon", "coordinates": [[[-48,-20],[-47,-20],[-47,-19],[-48,-19],[-48,-20]]] } } }
```

## 4. Response contract

No response contains `organizationId`. Entities are never returned directly.

- `ProducerDto`: `id, name, externalReference, countryCode, notes, isActive, createdAtUtc, updatedAtUtc`.
- `PlotRevisionSummaryDto`: `id, number, countryCode, geometryType ("Point" | "Polygon" | "MultiPolygon"), areaHectares, note, createdByUserId, createdAtUtc, supersededAtUtc`. The current revision has `supersededAtUtc: null`.
- `PlotRevisionDto`: the summary fields plus `productionPlotId` and `geometry` (a JSON **object**, not a string).
- `ProductionPlotListItem`: `id, producerId, name, externalReference, isActive, updatedAtUtc, currentRevision` (summary, no geometry).
- `ProductionPlotDto`: `id, producerId, name, externalReference, notes, isActive, createdAtUtc, updatedAtUtc, currentRevision` (full `PlotRevisionDto` with geometry).
- Lists use `{ items, page, pageSize, totalCount }`; the revision history is a plain array.

## 5. Error/status behaviour

| Situation | Status |
| --- | --- |
| Unknown or another organization's producer, plot or revision id in the route | 404 |
| Revision id that belongs to a different plot | 404 |
| `producerId` in the POST body unknown or from another organization | 400 `Producer was not found in your organization.` |
| Invalid input, invalid or unsupported geometry, oversize geometry | 400 (ProblemDetails, validation `errors` where applicable) |
| Duplicate producer or plot `externalReference` | 409 |
| Creating a plot for an **inactive** producer | 409 |
| Creating a revision for an **inactive plot** | 409 |
| Revision with **no material change** (see below) | 409 |
| Missing/invalid bearer token | 401 |
| DELETE on any of these routes, PUT/DELETE on a revision | 405 |

A revision is material only if the geometry, country of production or declared area changes. Equivalent geometry representations are **not** a change: a different ring start point, ring orientation, MultiPolygon component order, or a one-component MultiPolygon versus the identical Polygon. A changed `note` alone is not material. The producer's own activity is not checked when adding a revision to an active plot.

## 6. Breaking-change notes

None. All routes are new.

## 7. Required frontend adaptations (contract level)

- Surface producers and plots as internal master data; plot registration is always under one producer, and the producer cannot be changed afterwards.
- Show the current revision from the plot DTO and load history via the revisions routes; superseded revisions stay readable by id.
- Treat 409 as a business conflict (inactive producer/plot, duplicate reference, no material change) and 400 for validation.
- Always send `isActive` on PUT (use the current value to keep it unchanged); an omitted value is rejected so a resource is never reactivated by accident.
- Deactivate instead of delete. Deactivating a plot keeps its last revision current.
- Geometry is validated for structure only. Do not present a stored plot as EUDR-complete or sufficient geolocation.
- Do not assume a producer is a supplier; there is no link between them.

## 8. Compatibility risks

- The revision history list is unpaginated (pilot-scale assumption, same caveat as `evidence-history`); pagination may be introduced later as a contract change.
- Plot locations may relate to natural persons; retention and erasure are an open legal item. Do not log or display coordinates beyond what the user needs.
- Producer edits overwrite values (no producer history); plot locations are retained.
- Case geolocation evidence (`GeoSubmission`) is unchanged and not linked to plots; attributing case lots to plot revisions is a later slice.
- Client compatibility is not tested.

## 9. Out of scope

Supplier↔producer links, attributing cases/lots/quantities to plot revisions, CSV import or file upload of plots, map rendering, bulk endpoints, computed area, the EU GeoJSON profile and the 4 ha point/polygon rule, retention/erasure, public/supplier-portal access.

## 10. Backend verification performed

`scripts/verify` (Release build, unit tests, integration tests) including `ProvenanceTests` (HTTP behaviour, concurrency, tenant isolation, serialization), `ProvenanceModelTests`, `MigrationTests`, `DomainTests` geometry/material-change cases, the endpoint inventory and OpenAPI/401 contract tests. Client compatibility is not tested.
