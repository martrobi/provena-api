using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Provena.Api.Common;
public sealed class ExceptionHandler(IProblemDetailsService problems, ILogger<ExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            ApiException e => e.Status, ValidationException => 400, BadHttpRequestException e => e.StatusCode,
            DbUpdateConcurrencyException => 409,
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" or "23503" or "23001" } } => 409,
            _ => 500
        };
        var message = status == 500 ? "An unexpected error occurred." : exception switch
        { DbUpdateException => "A conflicting or referenced record prevents this operation.", _ => exception.Message };
        // Avoid exception text/SQL/URLs in logs: supplier tokens appear in request paths.
        logger.Log(status == 500 ? LogLevel.Error : LogLevel.Warning, "Request failed: {ExceptionType}, status {Status}, correlation {CorrelationId}", exception.GetType().Name, status, context.TraceIdentifier);
        var problem = new ProblemDetails { Status = status, Title = status == 400 ? "Validation failed" : "Request failed", Detail = message, Type = $"https://httpstatuses.com/{status}" };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (exception is ValidationException validation) problem.Extensions["errors"] = validation.Errors.GroupBy(x => x.PropertyName).ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray());
        if (exception is ApiException { Errors: not null } api) problem.Extensions["errors"] = api.Errors;
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
        return true;
    }
}
