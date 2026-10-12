# ADR 0003: Minimal APIs organized by feature as the HTTP layer

- Date: 2026-10-12
- Decision status: Accepted
- Implementation status: Implemented
- Decision owner / approval evidence: Owner request on 2026-10-12 to replace MVC Controllers with Minimal APIs as the long-term HTTP architecture. The owner also chose that framework binding failures use the ExceptionHandler ProblemDetails envelope.
- Supersedes / superseded by: Refines [ADR 0001](0001-single-api-service-architecture.md) at the HTTP entry layer only. The service and DbContext structure is unchanged.

## Context and evidence

The API used five MVC controller files (11 classes, 39 endpoints) that delegated directly to scoped services. Validation already ran explicitly in services, so the controllers depended on MVC for only a few things:
- `[ApiController]` binding errors;
- the null result → 204 conversion;
- `CreatedAtAction`;
- the optional-body attribute;
- request and form size attributes;
- MVC JSON options (string enums, depth 32).

`tests/Provena.Api.IntegrationTests/EndpointInventoryTests.cs` and `HttpContractTests.cs` were written and passed against the MVC implementation before the switch.

## Decision

Application endpoints are ASP.NET Core Minimal APIs:
- Feature modules live in `src/Provena.Api/Endpoints/<Feature>/` (namespace `Provena.Api.Endpoints`). Each one is a static class with a `Map<Feature>Endpoints` extension and named private static handlers that return `TypedResults`.
- `Endpoints/ProvenaEndpoints.cs` provides the single `MapProvenaEndpoints()` call used by Program.cs. Program.cs maps no individual endpoints.
- Internal routes nest under an `/api` group with `RequireAuthorization()`. Only explicit `AllowAnonymous` endpoints opt out (register/login). Public capability routes are a separate `/public/supplier-requests/{token}` group (anonymous at JWT level, `supplier` rate limiter).
- Handlers bind HTTP input, call one service method and map the result. Validation, transactions, locks, tenant checks and state transitions stay in services.
- Multipart groups use `WithUploadLimits()` (21 MiB request and multipart limits) and `DisableAntiforgery()`. Authentication is bearer-header or URL-capability only, with no cookies.
- `RouteHandlerOptions.ThrowOnBadRequest = true` sends parameter-binding failures through `Common/ExceptionHandler`. HTTP JSON options keep string-only enums and `MaxDepth = 32`.

MVC controllers are not used. Endpoint authorization policies beyond "authenticated" and a fallback policy remain undecided (see SECURITY).

## Alternatives considered

- **Keep MVC Controllers.** Rejected by the owner's direction.
- **Vertical slices that also move services and contracts into feature folders.** Rejected as an unrelated restructuring. Only the HTTP entry layer changed.
- **Inline lambdas or endpoints in Program.cs.** Rejected because they hurt readability and conflict with keeping Program.cs a composition root.
- **Emulating MVC's ValidationProblemDetails for binding errors.** Rejected by the owner in favour of the existing ExceptionHandler envelope.

## Consequences

- Routes, methods, success status codes, Location behaviour, file responses and the null-geolocation 204 are preserved, and HTTP tests pin them.
- Framework binding failures keep their status but now return the ExceptionHandler body (title "Validation failed" for 400, plus detail and traceId) instead of MVC's ValidationProblemDetails.
- Minimal APIs would answer 400 to a request with neither body nor Content-Type where a JSON body is required. `EndpointConventions.WithRequiredJsonBodyContentTypeCheck` (applied to the `/api` and public groups) restores MVC's 415. Optional bodies are unaffected.
- Minimal enum binding is case-sensitive. Query and form enums therefore bind as strings and use `EndpointConventions.ParseEnum`, which keeps the former case-insensitive and numeric acceptance. Services still reject undefined values.
- The antiforgery opt-out is safe only while no cookie authentication exists. Introducing cookies requires revisiting it.
- OpenAPI operation IDs and schemas may differ from the controller-generated document. It is a Development/Testing aid, not a versioned contract.
- New endpoints must update `EndpointInventoryTests`. That test fails on unexpected routes, missing authorization, wrong rate-limit policy, missing upload limits, antiforgery requirements or reintroduced controllers.

## Implementation and verification

Source: `src/Provena.Api/Endpoints/`, `src/Provena.Api/Program.cs`.

Tests: `EndpointInventoryTests`, `HttpContractTests` and the existing `WorkflowTests`, run with `scripts/verify.ps1`. The Kestrel request-size limit is verified only through endpoint metadata, because TestServer does not enforce it.

## Revisit trigger

Revisit if any of these happens:
- cookie or browser-session authentication is introduced;
- a role or permission model requires per-endpoint policies;
- API versioning is adopted;
- endpoint count or ownership makes the folder layout impractical.
