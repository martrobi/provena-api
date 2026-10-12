using Provena.Api.Domain;
namespace Provena.Api.Data;

// Retained evidence is append-only: rows are retired with a one-way marker, never deleted or overwritten. "Current" is the single source of truth for what workflows, exports and the public portal see.
public static class RetainedEvidence
{
    public static IQueryable<CaseDocument> Current(this IQueryable<CaseDocument> q) => q.Where(x => x.WithdrawnAtUtc == null);
    public static IQueryable<GeoSubmission> Current(this IQueryable<GeoSubmission> q) => q.Where(x => x.SupersededAtUtc == null);
    public static IQueryable<SupplierSubmission> Current(this IQueryable<SupplierSubmission> q) => q.Where(x => x.SupersededAtUtc == null);
    public static IQueryable<PlotRevision> Current(this IQueryable<PlotRevision> q) => q.Where(x => x.SupersededAtUtc == null);
}
