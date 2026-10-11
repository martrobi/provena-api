namespace Provena.Api.Infrastructure;
public interface IEmailSender { Task SendSupplierRequestAsync(string? recipient, Uri publicUrl, CancellationToken ct); }
// Explicit delivery stub. Tokens/URLs are returned once to the authenticated caller, never written to logs.
public sealed class DevelopmentEmailSender(ILogger<DevelopmentEmailSender> logger) : IEmailSender
{
    public Task SendSupplierRequestAsync(string? recipient, Uri publicUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        logger.LogInformation("Development email stub: supplier request created for {Recipient}. Copy the link from the authenticated response; email was not sent.", recipient ?? "(no email)");
        return Task.CompletedTask;
    }
}
