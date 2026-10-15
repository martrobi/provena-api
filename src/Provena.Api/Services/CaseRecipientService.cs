using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Versioned, immutable recipient statement (ADR 0008). The PUT body is the complete statement: either 1..50 identified recipients or a
// NotApplicable resolution with a recorded basis. No statement means unresolved; missing recipients are never silently converted to NotApplicable.
// Audit text carries identifiers only (no names, addresses or emails).
public sealed class CaseRecipientService(AppDbContext db, TenantContext tenant, CaseService cases, AuditService audit, IValidator<CaseRecipientsInput> validator)
{
    // 204 (null) when no statement exists; 404 for an unknown or foreign case.
    public async Task<CaseRecipientStatementDto?> Current(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var current = await db.CaseRecipientStatements.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        return current == null ? null : (await Statements([current], ct)).Single();
    }

    // Retained statements, newest first. Unpaginated: per-case, pilot scale.
    public async Task<List<CaseRecipientStatementDto>> History(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        return await Statements(await db.CaseRecipientStatements.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct), ct);
    }

    public async Task<CaseRecipientStatementDto> Save(Guid caseId, CaseRecipientsInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The route case is resolved and locked (404) and must be editable (409) before the body is examined (400).
        var c = await cases.Lock(caseId, ct); await cases.EnsureMutable(c, ct);
        var normalized = Normalize(input);
        await validator.ValidateAndThrowAsync(normalized, ct);

        var current = await db.CaseRecipientStatements.Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        if (current != null)
        {
            var existing = await db.CaseRecipients.AsNoTracking().Where(x => x.CaseRecipientStatementId == current.Id).ToListAsync(ct);
            if (IsIdentical(normalized, current, existing)) { await tx.CommitAsync(ct); return (await Statements([current], ct)).Single(); }
        }

        var now = DateTimeOffset.UtcNow;
        if (current != null)
        {
            // Supersede first and save before inserting the successor: EF cannot order writes around the filtered unique index.
            current.SupersededAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        var number = (await db.CaseRecipientStatements.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var saved = new CaseRecipientStatement { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, Resolution = normalized.Resolution, NotApplicableBasis = normalized.NotApplicableBasis, CreatedByUserId = tenant.UserId };
        db.CaseRecipientStatements.Add(saved);
        foreach (var r in normalized.Recipients!)
            db.CaseRecipients.Add(new CaseRecipient { OrganizationId = tenant.OrganizationId, CaseRecipientStatementId = saved.Id, Name = r.Name, PostalAddress = r.PostalAddress, Email = r.Email });
        audit.Add(caseId, "CaseRecipientsSaved", $"Case recipient statement version {saved.Id} (number {number}) saved" + (current == null ? "." : $"; superseded {current.Id}."));
        c.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return (await Statements([saved], ct)).Single();
    }

    // Trims text, turns a blank basis into null and a missing recipient list into an empty one; tolerates null members so validation reports them.
    public static CaseRecipientsInput Normalize(CaseRecipientsInput input) => input with
    {
        NotApplicableBasis = string.IsNullOrWhiteSpace(input.NotApplicableBasis) ? null : input.NotApplicableBasis.Trim(),
        Recipients = input.Recipients?.Select(r => r == null ? null! : r with { Name = (r.Name ?? "").Trim(), PostalAddress = (r.PostalAddress ?? "").Trim(), Email = (r.Email ?? "").Trim() }).ToList() ?? [],
    };

    // Order-independent equality of the normalized input and the stored current statement: recipients compare as a sorted multiset (ordinal).
    public static bool IsIdentical(CaseRecipientsInput input, CaseRecipientStatement current, IReadOnlyCollection<CaseRecipient> recipients)
    {
        if (input.Resolution != current.Resolution || input.NotApplicableBasis != current.NotApplicableBasis || input.Recipients!.Count != recipients.Count) return false;
        static string Key(string name, string address, string email) => string.Join('\u001f', name, address, email);
        var left = input.Recipients.Select(r => Key(r.Name, r.PostalAddress, r.Email)).Order(StringComparer.Ordinal);
        var right = recipients.Select(r => Key(r.Name, r.PostalAddress, r.Email)).Order(StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private async Task<List<CaseRecipientStatementDto>> Statements(List<CaseRecipientStatement> statements, CancellationToken ct)
    {
        var ids = statements.Select(x => x.Id).ToList();
        var recipients = await db.CaseRecipients.AsNoTracking().Where(x => ids.Contains(x.CaseRecipientStatementId)).ToListAsync(ct);
        return statements.Select(s => s.ToDto(recipients.Where(r => r.CaseRecipientStatementId == s.Id))).ToList();
    }
}
