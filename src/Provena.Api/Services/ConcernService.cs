using System.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// Post-completion Concern & Corrective Action v1: records concerns discovered after a case was Completed (Closed) or archived, the corrective actions and recipient notifications that
// followed, and the Owner's resolution. It is a retained layer on top of the completed case: it never changes the case status, reviews, reviewed snapshots, closures, External DDS records,
// quantity reconciliation, the completeness fingerprint or supplier links, and it never reopens the case. Provena sends nothing and claims no compliance, approval, recall completion or
// regulatory acceptance. Every write runs in one transaction under the tenant-scoped case lock (404 for an unknown or foreign case): the terminal-state check (concern creation only), the body
// shape (400), then the state rules (409) are evaluated under that lock, so a resolution and a concurrent action, notification or second resolution are serialized and the loser sees the
// resolution (409). Resolving is Owner-only: the live Owner check runs before any case lookup and before the body is read. Audit text carries ids and numbers only, never the free text or
// recipient details. Foreign or unknown document and recipient ids answer with the same 409 as a withdrawn or wrong one, so nothing leaks.
public sealed class ConcernService(AppDbContext db, TenantContext tenant, CaseService cases, OwnerGate owner, AuditService audit, IValidator<CreateConcernInput> concernValidator,
    IValidator<RecordConcernActionInput> actionValidator, IValidator<RecordConcernNotificationInput> notificationValidator, IValidator<ResolveConcernInput> resolutionValidator)
{
    // ---- reads (any member) -------------------------------------------------------------------------------------------------------------
    // Newest first (highest Number first). Unpaginated: a per-case collection at pilot scale.
    public async Task<List<ConcernSummaryDto>> List(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (!await db.Cases.AsNoTracking().AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var concerns = await db.CaseConcerns.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct);
        var actions = await db.ConcernActions.AsNoTracking().Where(x => x.EudrCaseId == caseId).GroupBy(x => x.ConcernId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var notifications = await db.ConcernNotifications.AsNoTracking().Where(x => x.EudrCaseId == caseId).GroupBy(x => x.ConcernId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var resolutions = await db.ConcernResolutions.AsNoTracking().Where(x => x.EudrCaseId == caseId).ToDictionaryAsync(x => x.ConcernId, x => x.ResolvedAtUtc, ct);
        return concerns.Select(c => new ConcernSummaryDto(c.Id, c.Number, c.Category, c.Summary, c.RaisedAtUtc, resolutions.ContainsKey(c.Id) ? ConcernStatus.Resolved : ConcernStatus.Open,
            actions.GetValueOrDefault(c.Id), notifications.GetValueOrDefault(c.Id), resolutions.TryGetValue(c.Id, out var at) ? at : null)).ToList();
    }

    public async Task<ConcernDto> Get(Guid caseId, Guid concernId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (!await db.Cases.AsNoTracking().AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var concern = await db.CaseConcerns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == concernId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        return await Detail(concern, ct);
    }

    // ---- writes ---------------------------------------------------------------------------------------------------------------------------
    // Any member. Only a Completed (Closed) or archived case accepts a new concern; the case itself is left untouched.
    public async Task<ConcernDto> Create(Guid caseId, Func<CancellationToken, Task<CreateConcernInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct);
        if (c.Status != EudrCaseStatus.Closed && !await db.CaseArchivals.Current().AnyAsync(x => x.EudrCaseId == caseId, ct))
            throw new ApiException(409, "A concern can only be recorded for a completed or archived case; use the normal case workflow while the case is open.", new { reason = "CaseNotCompleted" });
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Summary = (input.Summary ?? "").Trim(), Details = (input.Details ?? "").Trim(), RaisedAtUtc = input.RaisedAtUtc?.ToUniversalTime() };
        await concernValidator.ValidateAndThrowAsync(request, ct);
        if (request.RaisedAtUtc > DateTimeOffset.UtcNow) throw Invalid(nameof(CreateConcernInput.RaisedAtUtc), "raisedAtUtc cannot be in the future.");

        var number = (await db.CaseConcerns.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var concern = new CaseConcern
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, Category = request.Category!.Value, Summary = request.Summary!, Details = request.Details!,
            RaisedAtUtc = request.RaisedAtUtc!.Value, RecordedByUserId = tenant.UserId!.Value,
        };
        db.CaseConcerns.Add(concern);
        audit.Add(caseId, "ConcernRecorded", $"Concern {concern.Id} (number {number}, category {concern.Category}) recorded after case completion. The case itself is unchanged.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(concern, [], [], null);
    }

    // Any member. Rejected once the concern is resolved (re-checked under the case lock).
    public async Task<ConcernActionDto> RecordAction(Guid caseId, Guid concernId, Func<CancellationToken, Task<RecordConcernActionInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct);
        var concern = await RequireConcern(caseId, concernId, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Description = (input.Description ?? "").Trim(), PerformedAtUtc = input.PerformedAtUtc?.ToUniversalTime() };
        await actionValidator.ValidateAndThrowAsync(request, ct);
        if (request.PerformedAtUtc > DateTimeOffset.UtcNow) throw Invalid(nameof(RecordConcernActionInput.PerformedAtUtc), "performedAtUtc cannot be in the future.");
        await RequireOpen(concern.Id, ct);
        await RequireCurrentEvidence(caseId, request.EvidenceDocumentId, ct);

        var action = new ConcernAction
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, ConcernId = concern.Id, Description = request.Description!, PerformedAtUtc = request.PerformedAtUtc!.Value,
            RecordedByUserId = tenant.UserId!.Value, EvidenceDocumentId = request.EvidenceDocumentId,
        };
        db.ConcernActions.Add(action);
        audit.Add(caseId, "ConcernActionRecorded", $"Corrective action {action.Id} recorded for concern {concern.Id} (number {concern.Number}).");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(action);
    }

    // Any member. Records that a recipient was notified outside Provena; nothing is sent. Rejected once the concern is resolved (re-checked under the case lock).
    public async Task<ConcernNotificationDto> RecordNotification(Guid caseId, Guid concernId, Func<CancellationToken, Task<RecordConcernNotificationInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct);
        var concern = await RequireConcern(caseId, concernId, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), NotifiedAtUtc = input.NotifiedAtUtc?.ToUniversalTime() };
        await notificationValidator.ValidateAndThrowAsync(request, ct);
        if (request.NotifiedAtUtc > DateTimeOffset.UtcNow) throw Invalid(nameof(RecordConcernNotificationInput.NotifiedAtUtc), "notifiedAtUtc cannot be in the future.");
        await RequireOpen(concern.Id, ct);

        // The recipient must be an (immutable) recipient row of one of this case's recipient statements, current or superseded.
        var recipientId = request.RecipientId!.Value;
        var recipient = await db.CaseRecipients.AsNoTracking().SingleOrDefaultAsync(r => r.Id == recipientId && db.CaseRecipientStatements.Any(s => s.Id == r.CaseRecipientStatementId && s.EudrCaseId == caseId), ct)
            ?? throw new ApiException(409, "The recipient must be a recipient recorded for this case.", new { reason = "RecipientNotFound" });
        await RequireCurrentEvidence(caseId, request.EvidenceDocumentId, ct);

        var notification = new ConcernNotification
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, ConcernId = concern.Id, CaseRecipientId = recipient.Id, Channel = request.Channel!.Value,
            NotifiedAtUtc = request.NotifiedAtUtc!.Value, Note = request.Note, EvidenceDocumentId = request.EvidenceDocumentId, RecordedByUserId = tenant.UserId!.Value,
        };
        db.ConcernNotifications.Add(notification);
        audit.Add(caseId, "ConcernNotificationRecorded", $"Notification {notification.Id} (channel {notification.Channel}) recorded for concern {concern.Id} (number {concern.Number}), recipient {recipient.Id}.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(notification, recipient);
    }

    // Owner only: the live check runs before any case lookup and before the body is read. At most one resolution per concern; the server sets ResolvedAtUtc.
    public async Task<ConcernResolutionDto> Resolve(Guid caseId, Guid concernId, Func<CancellationToken, Task<ResolveConcernInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can resolve a concern.", ct);
        var c = await cases.Lock(caseId, ct);
        var concern = await RequireConcern(caseId, concernId, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Rationale = (input.Rationale ?? "").Trim() };
        await resolutionValidator.ValidateAndThrowAsync(request, ct);
        await RequireOpen(concern.Id, ct);

        var now = DateTimeOffset.UtcNow; // truncated to the microsecond PostgreSQL stores, so the response equals what is read back
        var resolution = new ConcernResolution
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, ConcernId = concern.Id, Outcome = request.Outcome!.Value, Rationale = request.Rationale!,
            ResolvedByUserId = tenant.UserId!.Value, ResolvedAtUtc = now.AddTicks(-(now.Ticks % 10)),
        };
        db.ConcernResolutions.Add(resolution);
        audit.Add(caseId, "ConcernResolved", $"Concern {concern.Id} (number {concern.Number}) resolved with outcome {resolution.Outcome}. This is an internal record, not a compliance or regulatory statement.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(resolution);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------
    private async Task<CaseConcern> RequireConcern(Guid caseId, Guid concernId, CancellationToken ct)
        => await db.CaseConcerns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == concernId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();

    // Under the case lock: a resolved concern accepts nothing further.
    private async Task RequireOpen(Guid concernId, CancellationToken ct)
    {
        if (await db.ConcernResolutions.AnyAsync(x => x.ConcernId == concernId, ct))
            throw new ApiException(409, "The concern is already resolved; record a new concern for any later issue.", new { reason = "ConcernResolved" });
    }

    // Evidence must be a current (not withdrawn) document of this case when it is linked; a later withdrawal keeps the historical link.
    private async Task RequireCurrentEvidence(Guid caseId, Guid? documentId, CancellationToken ct)
    {
        if (documentId is { } id && !await db.Documents.Current().AnyAsync(x => x.Id == id && x.EudrCaseId == caseId, ct))
            throw new ApiException(409, "The evidence document must be a current document of this case.", new { reason = "EvidenceDocumentNotCurrent" });
    }

    private static ValidationException Invalid(string property, string message) => new([new FluentValidation.Results.ValidationFailure(property, message)]); // 400, same shape as the validators

    private async Task<ConcernDto> Detail(CaseConcern concern, CancellationToken ct)
    {
        var actions = await db.ConcernActions.AsNoTracking().Where(x => x.ConcernId == concern.Id).OrderBy(x => x.PerformedAtUtc).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var notifications = await db.ConcernNotifications.AsNoTracking().Where(x => x.ConcernId == concern.Id).OrderBy(x => x.NotifiedAtUtc).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var recipientIds = notifications.Select(x => x.CaseRecipientId).Distinct().ToList();
        var recipients = await db.CaseRecipients.AsNoTracking().Where(x => recipientIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var resolution = await db.ConcernResolutions.AsNoTracking().SingleOrDefaultAsync(x => x.ConcernId == concern.Id, ct);
        return ToDto(concern, actions.Select(a => ToDto(a)).ToList(), notifications.Select(n => ToDto(n, recipients[n.CaseRecipientId])).ToList(), resolution == null ? null : ToDto(resolution));
    }

    private static ConcernDto ToDto(CaseConcern c, IReadOnlyList<ConcernActionDto> actions, IReadOnlyList<ConcernNotificationDto> notifications, ConcernResolutionDto? resolution)
        => new(c.Id, c.Number, c.Category, c.Summary, c.Details, c.RaisedAtUtc, c.RecordedByUserId, c.CreatedAtUtc, resolution == null ? ConcernStatus.Open : ConcernStatus.Resolved, actions, notifications, resolution);
    private static ConcernActionDto ToDto(ConcernAction a) => new(a.Id, a.Description, a.PerformedAtUtc, a.RecordedByUserId, a.CreatedAtUtc, a.EvidenceDocumentId);
    private static ConcernNotificationDto ToDto(ConcernNotification n, CaseRecipient r) => new(n.Id, new(r.Id, r.Name, r.PostalAddress, r.Email), n.Channel, n.NotifiedAtUtc, n.Note, n.EvidenceDocumentId,
        n.RecordedByUserId, n.CreatedAtUtc);
    private static ConcernResolutionDto ToDto(ConcernResolution r) => new(r.Id, r.Outcome, r.Rationale, r.ResolvedByUserId, r.ResolvedAtUtc);
}
