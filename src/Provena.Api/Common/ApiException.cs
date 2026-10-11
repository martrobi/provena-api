namespace Provena.Api.Common;

public sealed class ApiException(int status, string message, object? errors = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public object? Errors { get; } = errors;

    public static ApiException NotFound() => new(404, "Resource not found.");

    public static ApiException Conflict(string message) => new(409, message);
}
