# Architecture decision records

An ADR preserves a significant decision, its context and consequences. It is not an implementation plan or a place to declare every coding convention. Start from [TEMPLATE](TEMPLATE.md). Keep decisions small and link source evidence.

| Record | Status / meaning |
| --- | --- |
| [0001: service architecture](0001-single-api-service-architecture.md) | Observed baseline; reconstructed from source, no historical approval claimed |
| [0002: production concepts](0002-supplier-producer-production-plot.md) | Accepted target direction explicitly supplied by owner; partially implemented (see 0006) |
| [0003: Minimal API HTTP layer](0003-minimal-api-http-layer.md) | Accepted by owner request; implemented |
| [0004: paginated collection endpoints](0004-paginated-collection-endpoints.md) | Accepted by owner approval of the plan; implemented |
| [0005: retained evidence](0005-retained-evidence-append-only-rows.md) | Accepted by owner approval of the plan; implemented (write paths and internal history read API) |
| [0006: production provenance](0006-production-provenance-plot-revisions.md) | Accepted by owner approval of the plan; partial (persistence model and internal API implemented, attribution pending) |

Status vocabulary: Proposed (awaiting owner decision), Accepted (approved direction), Observed baseline (source-derived, history unknown), Superseded (link replacement). Track implementation separately: not implemented / partial / implemented with evidence. Never convert a target decision into a claim about current schema. Supersede material decisions rather than silently rewriting their history; correct factual mistakes transparently.
