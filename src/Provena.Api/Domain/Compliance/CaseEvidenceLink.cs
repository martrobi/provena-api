namespace Provena.Api.Domain;

public enum EvidencePurpose { Legality, DeforestationFree, Applicability, ProductionSource }

// Records that a retained document of the same case is OFFERED for a purpose. It is presence only: not reviewed, not sufficient, not legal,
// not compliant and not deforestation-free. Withdrawn, never deleted (one-way WithdrawnAtUtc with WithdrawnByUserId).
// At most one current link per (case, document, purpose). It never changes requirement statuses or the case status.
public sealed class CaseEvidenceLink : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid CaseDocumentId { get; set; }
    public EvidencePurpose Purpose { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? WithdrawnAtUtc { get; set; }
    public Guid? WithdrawnByUserId { get; set; }
}
