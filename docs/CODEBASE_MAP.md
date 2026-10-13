# Codebase map

All paths below are repository-relative. One application project; two test projects both reference it. No separate application/domain/data assemblies or repository classes are supplied.

| Responsibility | Actual location / entry point |
| --- | --- |
| Composition, middleware, JWT, rate limits, CORS, OpenAPI | `src/Provena.Api/Program.cs` |
| Registration, login, membership, JWT issuance | `src/Provena.Api/Auth/AuthService.cs` |
| Claim/public-token tenant context | `src/Provena.Api/Auth/TenantContext.cs` |
| Endpoint registration (`MapProvenaEndpoints`, `/api` auth group) and shared HTTP conventions | `src/Provena.Api/Endpoints/ProvenaEndpoints.cs`, `Endpoints/EndpointConventions.cs` |
| Auth routes | `src/Provena.Api/Endpoints/Auth/AuthEndpoints.cs` |
| Supplier/product CRUD | `src/Provena.Api/Endpoints/Catalog/SupplierEndpoints.cs`, `ProductEndpoints.cs` → `Services/CatalogService.cs` |
| Shipments, cases, dashboard, request management | `src/Provena.Api/Endpoints/Shipments/ShipmentEndpoints.cs`, `Endpoints/Cases/CaseEndpoints.cs`, `Cases/DashboardEndpoints.cs`, `Endpoints/SupplierRequests/SupplierRequestEndpoints.cs` → `ShipmentService`, `CaseService`, `SupplierRequestService`, `ExportService` in `Services/` |
| Internal document/geo routes and CSV imports | `src/Provena.Api/Endpoints/Cases/CaseFileEndpoints.cs`, `Endpoints/Imports/ImportEndpoints.cs` → `Services/CaseFileService.cs`, `Services/ImportService.cs` |
| Public capability routes | `src/Provena.Api/Endpoints/SupplierRequests/PublicSupplierEndpoints.cs` → `Services/SupplierRequestService.cs`, `Services/CaseFileService.cs` |
| Case state priority | `src/Provena.Api/Services/EudrCaseStatusService.cs` |
| Token generation/hash/shape | `src/Provena.Api/Services/SupplierTokenService.cs` |
| Geometry and file validators | `src/Provena.Api/Services/GeoJsonValidationService.cs`, `FileValidationService.cs` |
| Request/response records, DTO projections | `src/Provena.Api/Contracts/Requests.cs` |
| FluentValidation rules | `src/Provena.Api/Contracts/Validators.cs` |
| Entity bases / tenant ownership field | `src/Provena.Api/Domain/BaseEntities.cs` |
| Entities / enums (all namespace `Provena.Api.Domain`) | `src/Provena.Api/Domain/{Organizations,Suppliers,Products,Shipments,EudrCases,SupplierRequests,Files,Audit,Production,Dossiers}/` |
| Production provenance (Producer, ProductionPlot, PlotRevision) | `src/Provena.Api/Domain/Production/Producer.cs`, `ProductionPlot.cs` (also `PlotRevision`, `PlotGeometryType`); mapping/guards in `Data/AppDbContext.cs`; plot geometry validation and material-change comparison in `Services/GeoJsonValidationService.cs`; routes `Endpoints/Provenance/ProducerEndpoints.cs`, `ProductionPlotEndpoints.cs` → `Services/ProvenanceService.cs`; DTOs/inputs in `Contracts/Requests.cs`, validators in `Contracts/Validators.cs`; tests `ProvenanceModelTests.cs`, `ProvenanceTests.cs` |
| Case dossier model (CaseDossier, CaseLot, LotContribution, ContributionPlot) | `src/Provena.Api/Domain/Dossiers/CaseDossier.cs` (all four types); mapping, alternate keys, indexes and `EnforceDossierImmutable`/`Retirement` guards in `Data/AppDbContext.cs`; routes `Endpoints/Cases/CaseDossierEndpoints.cs` → `Services/CaseDossierService.cs` (SaveLot, Dossier, History; static `Normalize`, `Reconcile`, `IsIdentical`), inputs/DTOs in `Contracts/Requests.cs`, validators in `Contracts/Validators.cs`; the shipment lock is in `Services/ShipmentService.cs` and the manifest/`plots/` additions in `Services/ExportService.cs`; tests `DossierModelTests.cs`, `CaseDossierTests.cs`, unit `DossierDomainTests.cs`, `MigrationTests.cs` |
| Completeness facts (OperatorProfile, CaseMasterDataVersion, CaseApplicability, CaseRecipientStatement/CaseRecipient, CaseEvidenceLink, `Supplier.PostalAddress`) | `src/Provena.Api/Domain/Compliance/*.cs`; mapping, `Documents` alternate key, partial current indexes and the `Retirement`/`EnforceCompletenessFacts` guards in `Data/AppDbContext.cs`, `Current()` in `Data/RetainedEvidence.cs`; routes `Endpoints/Compliance/OperatorProfileEndpoints.cs`, `CaseFactEndpoints.cs` → `Services/OperatorProfileService.cs`, `CaseMasterDataService.cs` (refresh, drift, shipment binding), `CaseApplicabilityService.cs`, `ApplicabilityDerivation.cs` (pure derived status), `CaseRecipientService.cs`, `CaseEvidenceLinkService.cs`; inputs/DTOs in `Contracts/Requests.cs`, validators in `Contracts/Validators.cs`; tests `CompletenessFactsModelTests.cs`, `CompletenessFactsTests.cs`, unit `CompletenessFactsDomainTests.cs`, `MigrationTests.cs`; decision [ADR 0008](adr/0008-completeness-facts-and-reviewed-history.md) (evaluator and reviewed history not implemented yet) |
| Retained evidence and plot revision "current" predicates (`Current()`) | `src/Provena.Api/Data/RetainedEvidence.cs`; write paths in `Services/CaseFileService.cs` (withdraw/supersede) and `SupplierRequestService.SaveDetails` |
| EF/Identity model, tenant filters and tracked-write guards | `src/Provena.Api/Data/AppDbContext.cs` (mapping is inline in OnModelCreating) |
| EF design-time connection configuration | `src/Provena.Api/Data/DesignTimeFactory.cs` |
| Migrations and model snapshot | `src/Provena.Api/Data/Migrations/20261010180000_InitialCreate.cs`, `20261012225511_RetainedEvidence.cs`, `20261012234200_ProductionProvenance.cs`, `20261013004014_CaseDossier.cs`, `20261013213911_CompletenessFacts.cs`, `InitialCreate.TargetModel.cs` (frozen original target model), `AppDbContextModelSnapshot.cs`; read [DATABASE](DATABASE.md) before adding migrations |
| File I/O / email adapter | `src/Provena.Api/Infrastructure/FileStorage.cs`, `EmailSender.cs` |
| Error mapping / app exceptions | `src/Provena.Api/Common/ExceptionHandler.cs`, `ApiException.cs` |
| Shared list paging (`ToPageAsync`, defaults/limits) | `src/Provena.Api/Common/Paging.cs`, `PageResult<T>` in `Contracts/Requests.cs` |
| Audit / demo data | `src/Provena.Api/Services/AuditService.cs`, `DevelopmentSeeder.cs` |
| Pure rules tests | `tests/Provena.Api.UnitTests/DomainTests.cs` |
| Real PostgreSQL HTTP tests | `tests/Provena.Api.IntegrationTests/ApiFactory.cs`, `WorkflowTests.cs`, `HttpContractTests.cs` |
| Endpoint surface inventory (routes, auth, rate limits, upload metadata) | `tests/Provena.Api.IntegrationTests/EndpointInventoryTests.cs` |
| CLI verification / live smoke | `scripts/verify.ps1`, `scripts/verify.sh`, `scripts/smoke.py` |
| SDK / build / solution | `global.json`, `Directory.Build.props`, `Provena.sln`, each `.csproj` |
| Local PostgreSQL only | `docker-compose.yml`, `.env.example` |
| Configuration | `src/Provena.Api/appsettings.json`, `appsettings.Development.json.example`, `Properties/launchSettings.json` |
| EF CLI pin | `.config/dotnet-tools.json` |

## Dependencies (declared, restore not verified)

`Directory.Build.props` sets net10.0, nullable, implicit usings, latest analysis and warnings as errors. SDK starts at 10.0.100 with `latestFeature` roll-forward. API references JWT bearer/Identity EF/OpenAPI/EF Design 10.0.12; Npgsql EF provider 10.0.3; FluentValidation DI 12.2.0; Scalar 2.17.14; NetTopologySuite 2.6.0; CsvHelper 33.1.0. These are archive values, not recommendations to upgrade/downgrade.

Tests use xUnit 2.9.3 and Microsoft.NET.Test.Sdk 17.14.1. Integration adds Microsoft.AspNetCore.Mvc.Testing 10.0.12 (WebApplicationFactory; not MVC controllers) and Testcontainers.PostgreSql 4.7.0. `SSH.NET` 2026.0.0 is also referenced there, with no usage found in supplied test source. No lock file, CI workflow, Dockerfile or package-version management beyond the project files is supplied.

For a small change, inspect one table row and its callers/tests first. Search more broadly only if those sources expose another dependency or contradict this map.
