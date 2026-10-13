using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Typed evidence links (ADR 0008): "this retained document is offered for this purpose". A link is presence only: not reviewed, not sufficient, not legal,
// not compliant. It never changes requirement statuses or the case status. Links are withdrawn, never physically deleted. Audit text carries identifiers only.
public sealed class CaseEvidenceLinkService(AppDbContext db, TenantContext tenant, CaseService cases, AuditService audit, IValidator<CaseEvidenceLinkInput> validator)
{
    // ALL links of the case, oldest first, withdrawn ones included (retained history). The link withdrawal and the linked document's withdrawal are separate facts and both are exposed.
    public async Task<List<CaseEvidenceLinkDto>> List(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var query = from l in db.CaseEvidenceLinks.AsNoTracking() join d in db.Documents.AsNoTracking() on l.CaseDocumentId equals d.Id
                    where l.EudrCaseId == caseId
                    orderby l.CreatedAtUtc, l.Id
                    select new CaseEvidenceLinkDto(l.Id, l.CaseDocumentId, l.Purpose, l.Note, d.OriginalFileName, d.WithdrawnAtUtc, l.CreatedByUserId, l.CreatedAtUtc, l.WithdrawnAtUtc, l.WithdrawnByUserId);
        return await query.ToListAsync(ct);
    }

    public async Task<CaseEvidenceLinkDto> Create(Guid caseId, CaseEvidenceLinkInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct); CaseService.EnsureEditable(c);
        await validator.ValidateAndThrowAsync(input with { Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim() }, ct);
        // Unknown, foreign and other-case documents are indistinguishable (400, non-disclosing).
        var document = await db.Documents.SingleOrDefaultAsync(x => x.Id == input.CaseDocumentId && x.EudrCaseId == caseId, ct) ?? throw new ApiException(400, "Document was not found for this case.");
        if (document.WithdrawnAtUtc != null) throw ApiException.Conflict("The document has been withdrawn and cannot be linked.");
        // The partial unique index is the race backstop (23505 -> 409).
        if (await db.CaseEvidenceLinks.Current().AnyAsync(x => x.EudrCaseId == caseId && x.CaseDocumentId == document.Id && x.Purpose == input.Purpose, ct))
            throw ApiException.Conflict("The document is already linked for this purpose.");

        var link = new CaseEvidenceLink { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CaseDocumentId = document.Id, Purpose = input.Purpose, Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), CreatedByUserId = tenant.UserId };
        db.CaseEvidenceLinks.Add(link);
        audit.Add(caseId, "CaseEvidenceLinked", $"Document {document.Id} linked as evidence link {link.Id}.");
        c.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(link.Id, link.CaseDocumentId, link.Purpose, link.Note, document.OriginalFileName, document.WithdrawnAtUtc, link.CreatedByUserId, link.CreatedAtUtc, null, null);
    }

    // Semantic withdrawal: the row is retained. Withdrawing an already withdrawn link is an idempotent no-op.
    public async Task Withdraw(Guid caseId, Guid linkId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct); CaseService.EnsureEditable(c);
        var link = await db.CaseEvidenceLinks.SingleOrDefaultAsync(x => x.Id == linkId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        if (link.WithdrawnAtUtc != null) { await tx.CommitAsync(ct); return; }
        var now = DateTimeOffset.UtcNow;
        link.WithdrawnAtUtc = now; link.WithdrawnByUserId = tenant.UserId;
        audit.Add(caseId, "CaseEvidenceLinkWithdrawn", $"Evidence link {link.Id} withdrawn.");
        c.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
