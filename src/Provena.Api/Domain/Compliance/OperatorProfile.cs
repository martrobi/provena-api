namespace Provena.Api.Domain;

// The organization's own operator identity: one per organization, mutable catalogue-like data. It is a SOURCE for the case master-data copy
// (CaseMasterDataVersion) and is never read directly as a case fact. EoriNumber is stored only; it is not evaluated and not DDS readiness.
// Deletion is rejected by the persistence guard.
public sealed class OperatorProfile : TenantEntity
{
    public string LegalName { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    public string Email { get; set; } = "";
    public string EstablishmentCountryCode { get; set; } = "";
    public string? EoriNumber { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
