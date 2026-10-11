using Provena.Api.Common;
namespace Provena.Api.Services;
public sealed class FileValidationService(IConfiguration config)
{
    public const long AbsoluteMaxBytes = 20 * 1024 * 1024;
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ["application/pdf"], [".xls"] = ["application/vnd.ms-excel"],
        [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
        [".csv"] = ["text/csv", "application/csv", "application/vnd.ms-excel"], [".txt"] = ["text/plain"],
        [".jpg"] = ["image/jpeg"], [".jpeg"] = ["image/jpeg"], [".png"] = ["image/png"],
        [".json"] = ["application/json", "application/geo+json"], [".geojson"] = ["application/geo+json", "application/json"]
    };
    public string Validate(IFormFile file, bool geo = false)
    {
        var limit = Math.Min(config.GetValue("FileStorage:MaxFileSizeMb", 20) * 1024L * 1024, AbsoluteMaxBytes);
        if (file.Length > limit) throw new ApiException(413, $"File exceeds the {limit / 1024 / 1024} MB limit.");
        if (file.Length == 0) throw new ApiException(400, "Empty files are not allowed.");
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (name.Length is 0 or > 255 || name.Any(char.IsControl)) throw new ApiException(400, "Invalid file name.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var mime = file.ContentType.Split(';')[0].Trim();
        if (!Allowed.TryGetValue(extension, out var types) || !types.Contains(mime, StringComparer.OrdinalIgnoreCase) || (geo && extension is not (".json" or ".geojson")))
            throw new ApiException(400, "Unsupported file extension or MIME type.");
        return extension;
    }
}
