# API contracts

Authoritative source: the Minimal API modules in `src/Provena.Api/Endpoints/` (registered by `MapProvenaEndpoints()`, see [ADR 0003](adr/0003-minimal-api-http-layer.md)), `Contracts/Requests.cs`, `Contracts/Validators.cs` and the corresponding service. No versioned path prefix exists. OpenAPI says v1 but routes use `/api/...` or `/public/...`. OpenAPI/Scalar are available only in Development/Testing (`/openapi/v1.json`, `/scalar`). `HttpContractTests` checks that the document lists every route. Operation IDs and schemas are not treated as a contract.

## Routes at a glance

| Endpoint module (`Endpoints/`) | Routes / operations |
| --- | --- |
| Auth/AuthEndpoints | POST `/api/auth/register`, POST `/api/auth/login` anonymous + auth limiter; GET `/api/auth/me` authenticated |
| Catalog/SupplierEndpoints, ProductEndpoints | GET list, POST create, GET/PUT/DELETE `/{id}` under `/api/suppliers` and `/api/products` |
| Shipments/ShipmentEndpoints, Cases/DashboardEndpoints | GET/POST `/api/shipments`; GET/PUT/DELETE `/api/shipments/{id}` (DELETE archives case); GET `/api/dashboard` |
| Cases/CaseEndpoints | GET `/api/eudr-cases`; GET `/{id}`; PATCH `/{caseId}/requirements/{requirementId}`; POST `/{caseId}/supplier-request`; POST `/{caseId}/complete`; GET `/{caseId}/export` |
| SupplierRequests/SupplierRequestEndpoints | POST `/api/supplier-requests/{id}/revoke` and `/regenerate` |
| Cases/CaseFileEndpoints | GET/POST `/api/eudr-cases/{caseId}/documents`; GET/DELETE `/documents/{documentId}`; POST/GET `/geolocation` |
| Imports/ImportEndpoints | POST `/api/import/suppliers`, `/products`, `/shipments` |
| SupplierRequests/PublicSupplierEndpoints | GET `/public/supplier-requests/{token}`; PUT `/details`; POST `/documents`, `/geolocation`, `/complete` |

Every internal route nests under the `/api` route group, which requires authorization. Only register and login opt out with AllowAnonymous. Public supplier endpoints form a separate group that is anonymous at the JWT level; services validate the secret capability. There is no fallback authorization policy, so routes mapped outside the `/api` group are not protected automatically. `EndpointInventoryTests` pins the route/method set, authorization, rate-limit and upload metadata, so any endpoint change must update it deliberately. All named ID route parameters use Guid constraints. No public document-download/list-by-arbitrary-ID endpoint exists.

## Shapes, validation and success responses

JSON uses web defaults and string enums, with integer enum values disallowed for JSON conversion. Maximum JSON depth is 32. Query/form enums (`status` on case lists, document `category`) bind case-insensitively and accept numeric strings through `EndpointConventions.ParseEnum`; services reject undefined values. A missing document `category` defaults to SupportingDocument. CSV parsing has its own rules (below). Inputs and named DTOs are records in Requests.cs; responses also include mutable entities and anonymous objects. Current public projections omit reviewer notes, audit events, organization IDs, storage keys and token hashes; retain that restricted contract.

Catalog creates return 201 with an absolute Location (named GET route); shipment create returns 201 with `{ shipment, eudrCaseId }` without CreatedAtAction. Auth returns 200 with accessToken/expiresAtUtc. Request creation/rotation returns 200 with requestId/publicUrl/expiresAtUtc (a sensitive link shown to the internal caller). Review/complete/revoke/delete actions return 204. Uploads return document/geo DTOs. Export returns application/zip with a download name; document download uses stored MIME and original basename. Geolocation GET returns the DTO, or 204 with an empty body when no geolocation exists. The complete endpoint's body is optional (absent, empty or `{}`). File responses are attachments with `Content-Disposition` filenames and no range processing.

Explicit service FluentValidation includes length limits, email shape, uppercase two-letter country format, numeric CN code length 4–10, positive quantities and enum membership. These are not registry/legal validations. OIB validation is 11 digits only. Review notes are optional; completion DDS reference is optional with max 200 characters. Parameter binding can reject malformed JSON, missing bodies or unparsable values before services. Do not move validation only to the UI.

Cases alone use `PageResult<T>`: items/page/pageSize/totalCount. Defaults page=1, pageSize=25; page >=1 and size 1–100, integer skip overflow rejected. Filters: status, supplierId, productId, search <=200 characters over shipment reference/supplier/product name. Sort UpdatedAtUtc descending then case ID. Supplier/product lists have optional search <=200, sorted by name, and no pagination; shipments list is unpaginated, newest first. Do not silently apply case pagination to all endpoints.

## Errors

`Common/ExceptionHandler.cs` returns ProblemDetails with status/title/detail/type and traceId. FluentValidation errors are grouped by property into `errors`; ApiException may supply another errors shape. Known status mappings: 400 validation; 401 authentication; 403 membership check in Me; 404 unavailable records/tokens; 409 workflow or selected PostgreSQL conflicts; 413 file size; 429 rate limiting; generic 500 for unexpected errors. Parameter-binding failures (`RouteHandlerOptions.ThrowOnBadRequest`) also pass through this handler: 400 with title "Validation failed" and a framework detail message but no per-field `errors`, or 415 for a non-JSON/non-form content type, including a request with neither body nor Content-Type where a JSON body is required (`WithRequiredJsonBodyContentTypeCheck`). Auth (401/403), unmatched route (404), wrong method (405) and rate-limit (429) responses come from status-code pages or the limiter, so their details/extension shapes differ; do not assume every failure passed through this handler. ProblemDetails customization clears Instance to avoid token-bearing URLs. There is no universal error-code enum.

## Uploads and supplier capability

Multipart field is `file`; internal documents additionally accept form `category`. Upload route groups (internal case files, imports, public supplier) apply `WithUploadLimits()`: 21 MiB request and multipart limits; FileValidationService imposes configured 1–20 MiB per file (default 20). [SECURITY](SECURITY.md) describes validation limitations and capability lifecycle. [EUDR_DOMAIN](EUDR_DOMAIN.md) owns evidence/review effects; do not duplicate transition logic in endpoint handlers.

## CSV imports

UTF-8 comma-separated input, exact header names and order, trimmed fields, <=10,000 data rows and normal file limits. No Excel import despite accepting Excel as supporting documents.

| Kind | Exact header |
| --- | --- |
| suppliers | `externalReference,name,email,contactPerson,countryCode` |
| products | `externalReference,name,cnCode,commodityType,description` |
| shipments | `referenceNumber,supplierExternalReference,productExternalReference,quantity,unit,originCountryCode` |

Quantities use invariant decimal parsing; enum names are case-sensitive. The current Enum.TryParse + IsDefined combination also accepts defined numeric enum strings; do not claim names-only CSV validation. Shipment references resolve via ExternalReference within the tenant-filtered sets. The model and initial SQL have per-organization partial unique indexes for non-null supplier/product external references.

Rows create new records; this is not upsert. Invalid file/header/parser failures abort before writes. After parsing, valid rows persist independently; caught validation/API/DbUpdate failures are reported with row index `i+2` (logical data row plus header, not physical lines for multiline CSV). Result: totalRows/imported/failed/errors. No whole-file rollback, idempotency key or background queue. The declared external-reference indexes prevent duplicate non-null values within an organization when the supplied schema is applied; migrations and live schema still require runtime verification.

## Export contract

All case statuses can be exported. Package root `case-{safeReference}/` contains manifest.json, UTF-8 BOM summary.csv, documents named with ID + sanitized filename, and geolocation/original.geojson when present. Manifest includes organization, shipment, supplier, product, supplier submission, requirements, document/geo metadata, status/reference/timestamps and audit events, plus an operational-only notice. Tokens/storage keys are not intentionally projected. CSV values are quoted and leading formula characters are prefixed with an apostrophe. ZIP creation records CaseExported, so GET has a database side effect. Export is not a DDS message or external submission.

## Compatibility rule

Before changing an endpoint, inspect DTOs, service projection, tests and `scripts/smoke.py`. No frontend is supplied: do not claim frontend compatibility was tested. Treat renames, enum changes, nullability, status codes, pagination, CSV headers and ZIP manifest fields as contract changes. Prefer explicit additive evolution where safe; ask the owner about breaking changes and record migration/rollout expectations. Add HTTP assertions for externally visible changes, including negative access paths.
