using System.Text.RegularExpressions;
namespace Provena.Api.Infrastructure;
public sealed record FileStorageResult(string StorageKey, string StoredFileName, long SizeBytes);
public interface IFileStorage
{
    Task<FileStorageResult> SaveAsync(Stream input, string extension, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string root;
    public LocalFileStorage(IConfiguration config, IWebHostEnvironment environment)
    {
        var configured = config["FileStorage:RootPath"] ?? "./data/files";
        root = Path.GetFullPath(configured, environment.ContentRootPath); Directory.CreateDirectory(root);
    }
    private string Resolve(string key)
    {
        if (!Regex.IsMatch(key, "^[a-f0-9]{32}\\.[a-z0-9]{1,10}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Invalid storage key.");
        return Path.Combine(root, key);
    }
    public async Task<FileStorageResult> SaveAsync(Stream input, string extension, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N") + extension.ToLowerInvariant(); var path = Resolve(key);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct); await output.FlushAsync(ct); return new(key, key, output.Length);
        }
        catch { File.Delete(path); throw; }
    }
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)); }
    public Task DeleteAsync(string key, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); File.Delete(Resolve(key)); return Task.CompletedTask; }
}
