using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
namespace Provena.Api.Services;
public sealed class SupplierTokenService
{
    public string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public bool IsWellFormed(string token) => token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
