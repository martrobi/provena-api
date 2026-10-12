---
name: security-sensitive-change
description: Use for Provena authentication, authorization, tenant isolation, supplier tokens, public endpoints, files, logging or integrations.
---

# security-sensitive-change

Paths below are relative to the repository root. Load this procedure only for a matching task.

1. Read `docs/SECURITY.md` and affected source/tests. Draw the specific trust boundary in words: caller, credential, allowed tenant/case/resource, sensitive output and possible misuse.
2. Verify actual backend authorization plus organization constraints, foreign references and write paths. Inspect the configured filters, composite FKs and `SaveChangesAsync(CancellationToken)` guard; other save overloads/bulk/raw writes are not covered by that override. Do not rely on frontend checks, DTO omissions or a filter assumed to exist. For endpoints:
   - Internal routes must sit under the authorized `/api` route group, and `AllowAnonymous` must be deliberate.
   - Multipart routes need `WithUploadLimits()`. Their antiforgery opt-out is valid only while authentication stays cookie-free.
   - `EndpointInventoryTests` must reflect the intended access metadata.
3. For tokens preserve random generation, hash-only persistence, expiry/revocation and post-lock revalidation. For uploads review size/type/name/storage/cleanup; for logs inspect all newly logged fields. Do not add filter bypasses casually.
4. Preserve transaction/locking and reviewed public projections. New permissions/credential lifecycle/integration policies require explicit decisions, not inferred role semantics.
5. Add relevant negative HTTP/PostgreSQL tests: no auth, other tenant, wrong case/document, malformed/expired/revoked/rotated capability, sensitive response leakage and concurrency where changed. Never use real secrets/evidence in fixtures.
6. Update SECURITY for changed controls or newly verified gaps, API for changed exposure, and DEPLOYMENT for operational assumptions. Run `verify-change`; report residual risks without claiming full security assurance.

Output: boundary/control changes, denial tests, operational dependencies and unresolved risks.
