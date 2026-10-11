# Actual architecture

## Observed source structure

A single ASP.NET Core application contains controllers, record contracts, services, mutable domain entities and infrastructure adapters. Domain folders organize code but share the same namespace and assembly. Services call EF directly; no repository layer, mediator, aggregate implementation or event bus is supplied. `Data/AppDbContext.cs` derives from IdentityDbContext and contains inline EF mappings, tenant query filters and tracked-write guards; [DATABASE](DATABASE.md) owns their scope.

HTTP → middleware/auth → controller model binding → service validation/orchestration → EF and optional file/email I/O → DTO/entity/anonymous response. Read methods frequently use `AsNoTracking`. FluentValidation is invoked explicitly by services, not through an automatic MVC FluentValidation pipeline.

## Composition and lifetime

`Program.cs` registers `AppDbContext` via `AddDbContext` (scoped default); `TenantContext`, orchestration/auth/audit/seeder services and `IEmailSender` are scoped. Token generation, file/geometry validation and `IFileStorage` are singleton. Do not add request/tenant state to singleton services. Identity uses `ApplicationUser`, Guid roles, EF stores and SignInManager. Existing adapters are `LocalFileStorage` and `DevelopmentEmailSender` in every environment.

Middleware: exception handler → status code pages → production HSTS/HTTPS redirect → no-store/nosniff/no-referrer headers → routing → CORS → authentication → authorization → rate limiter → controller endpoints. OpenAPI/Scalar are mapped only in Development/Testing. Startup validates selected configuration; [DEPLOYMENT](DEPLOYMENT.md) owns the settings.

## Transactions and consistency

Registration explicitly transacts user, organization and membership creation. New shipments transact shipment, case, checklist and audit writes. Existing case operations commonly begin a transaction then call `CaseService.Lock`, a PostgreSQL `SELECT ... FOR UPDATE` with explicit case and organization predicates. Preserve the transaction around the lock. Public requests resolve the capability, acquire this case lock, then recheck hash/revocation/expiry to address concurrent rotation/revocation.

Review, supplier writes, uploads, deletion, request lifecycle and export use case locking. Export adds an audit record, gathers details and copies file bytes into a disk-backed ZIP while holding the transaction; this can hold a lock for a long export. Normal Details uses multiple queries without the same snapshot/locking guarantee. Catalog saves do not acquire case locks. Do not claim that all data associated with a case is frozen or that every read is an atomic snapshot.

File storage and database are not one transaction. Upload attempts to delete new bytes on DB failure, and deletes replaced geometry bytes after commit. Document deletion removes its row before best-effort file deletion. There is no durable cleanup queue. Email runs after request creation commits; failures would not roll back the request. Current email is a logging stub, so no delivery retry exists.

## Error, audit and observability conventions

`ApiException` represents expected HTTP failures. `ExceptionHandler` maps validation to 400, concurrency and selected PostgreSQL constraint errors to 409, and unexpected errors to generic 500. It logs type/status/trace ID without exception text. [API](API.md) owns response details.

`AuditService.Add` adds an entity to the current DbContext and logs event/case/organization IDs; callers save it. Descriptions are usually concise and IDs-based. `AppDbContext.SaveChangesAsync(CancellationToken)` rejects tracked AuditEvent modification/deletion. No database trigger or restricted audit-table grants are supplied; other save overloads and direct/bulk writes are outside that guard. This is application-level append-only enforcement, not a tamper-proof audit guarantee.

No hosted/background service, scheduler, message broker, external HTTP integration, distributed cache or tracing backend is supplied. Expiry is checked at request time. Logging uses ASP.NET logging; the development email stub logs the recipient email. [SECURITY](SECURITY.md) owns redaction and deployment boundaries.

## Project rules / recommendations

Keep the existing service architecture for routine features. Preserve cancellation and transaction ordering. Use DTO projections at public boundaries. Do not introduce generic repositories or additional assemblies solely to match a theoretical architecture. A substantial boundary change needs a concrete problem, plan and owner decision. Durable current choices and target decisions are recorded selectively in [ADRs](adr/README.md); source remains the evidence for behaviour.
