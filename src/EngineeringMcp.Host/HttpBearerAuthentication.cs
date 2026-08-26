using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace EngineeringMcp.Host;

internal static class HttpBearerAuthentication
{
    private const int MinimumTokenLength = 32;

    public static bool IsStrongToken([NotNullWhen(true)] string? token)
        => !string.IsNullOrWhiteSpace(token) && token.Length >= MinimumTokenLength;

    public static bool IsAuthorized(string? authorizationHeader, string expectedToken)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suppliedToken = authorizationHeader[prefix.Length..].Trim();
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public static string DeriveClientId(string token)
        => "http-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
}
