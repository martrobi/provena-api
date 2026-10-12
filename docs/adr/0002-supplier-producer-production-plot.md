# ADR 0002: Supplier, Producer and ProductionPlot are distinct concepts

- Date: 2026-10-11
- Decision status: Accepted target direction
- Implementation status: Partial. Supplier and case-level geolocation exist; Producer, ProductionPlot and PlotRevision exist as a persistence model ([ADR 0006](0006-production-provenance-plot-revisions.md), no API yet). Supplier/producer relationships and case/lot-to-plot attribution are not implemented
- Approval evidence: Owner's attached foundation brief explicitly states this direction

## Context

`Domain/Suppliers/Supplier.cs` models a commercial supplier; `Domain/Shipments/Shipment.cs` references one supplier/product. `Domain/Files/CaseFiles.cs` models current case-level GeoSubmission. No Producer or ProductionPlot entity is supplied.

## Decision

Supplier != Producer != ProductionPlot. A supplier can be an exporter, cooperative or intermediary and need not be the producer. A producer may have multiple production plots. Future design must preserve these distinctions and the separation of supplier evidence from reviewer/regulatory conclusions.

This does not decide supplier-to-producer cardinality, whether one entity can hold several roles, plot ownership/tenancy, stable identity, shipment allocation, cross-organization sharing, revision history or backfill. Those require explicit feature decisions.

## Alternatives and consequences

Treating every Supplier as Producer would encode an unsupported identity assumption. Treating every uploaded polygon as an already identified plot would confuse evidence geometry with a domain entity. These shortcuts are incompatible with the stated direction.

A future implementation needs separately reviewed data/API changes and migration treatment of existing evidence. Existing GeoSubmission contains insufficient structured identity to infer producer/plot relationships automatically. Avoid guessing during migration.

## Verification and implementation

No schema or endpoint is created by this ADR. A feature plan must define cardinalities, identity, evidence linkage, tenant rules, acceptance criteria, upgrade/backfill and tests. The target must remain labeled unimplemented until source and tests support it. See [EUDR_DOMAIN](../EUDR_DOMAIN.md).
