using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Redaction;

public interface IRedactionService
{
    string Redact(string input);
    string Redact(string input, PiiMode piiMode);
    bool LooksSensitive(string? value);
    bool LooksSensitiveOrPii(string? value);
}

public sealed partial class RedactionService : IRedactionService
{
    public string Redact(string input) => Redact(input, PiiMode.Mask);

    public string Redact(string input, PiiMode piiMode)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var output = RedactUserProfile(input);
        output = AuthorizationRegex().Replace(output, "$1[REDACTED:CREDENTIAL]");
        output = JwtRegex().Replace(output, "[REDACTED:JWT]");
        output = ConnectionPasswordRegex().Replace(output, "$1[REDACTED:CREDENTIAL]");
        output = CommonSecretRegex().Replace(output, "$1[REDACTED:CREDENTIAL]");
        output = PrivateKeyRegex().Replace(output, "[REDACTED:PRIVATE_KEY]");
        output = CookieRegex().Replace(output, "$1[REDACTED:COOKIE]");
        output = AwsAccessKeyRegex().Replace(output, "[REDACTED:ACCESS_KEY]");

        if (piiMode != PiiMode.Off)
        {
            output = EmailRegex().Replace(output, m => ApplyPii(m.Value, "EMAIL", piiMode));
            output = PhoneRegex().Replace(output, m => ApplyPii(m.Value, "PHONE", piiMode));
            output = SsnRegex().Replace(output, m => ApplyPii(m.Value, "IDENTIFIER", piiMode));
            output = VinRegex().Replace(output, m => ApplyPii(m.Value, "VIN", piiMode));
            output = LabeledPiiRegex().Replace(output, m => m.Groups[1].Value + ApplyPii(m.Groups[2].Value, "IDENTITY", piiMode));
            output = CreditCardCandidateRegex().Replace(output, m => IsValidPaymentCard(m.Value)
                ? ApplyPii(m.Value, "PAYMENT_CARD", piiMode)
                : m.Value);
        }

        return output;
    }

    private static string RedactUserProfile(string input)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? input
            : input.Replace(profile, "[USER_PROFILE]", StringComparison.OrdinalIgnoreCase);
    }

    public bool LooksSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return AuthorizationRegex().IsMatch(value)
            || JwtRegex().IsMatch(value)
            || ConnectionPasswordRegex().IsMatch(value)
            || CommonSecretRegex().IsMatch(value)
            || PrivateKeyRegex().IsMatch(value)
            || AwsAccessKeyRegex().IsMatch(value);
    }

    public bool LooksSensitiveOrPii(string? value)
        => LooksSensitive(value) || (!string.IsNullOrWhiteSpace(value) &&
            (EmailRegex().IsMatch(value) || PhoneRegex().IsMatch(value) || SsnRegex().IsMatch(value) ||
             VinRegex().IsMatch(value) || LabeledPiiRegex().IsMatch(value) ||
             CreditCardCandidateRegex().Matches(value).Any(match => IsValidPaymentCard(match.Value))));

    private static bool IsValidPaymentCard(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if (doubleDigit && (digit *= 2) > 9) digit -= 9;
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static string ApplyPii(string value, string kind, PiiMode mode) => mode switch
    {
        PiiMode.Remove => $"[PII:{kind}:REMOVED]",
        PiiMode.Hash => $"[PII:{kind}:SHA256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16]}]",
        _ => $"[PII:{kind}]"
    };

    [GeneratedRegex(@"(?i)(Authorization\s*:\s*)(?:Bearer\s+|Basic\s+)?[^\r\n\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)((?:Password|Pwd)\s*=\s*)[^;\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionPasswordRegex();

    [GeneratedRegex(@"(?i)((?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|secret|password)\s*[:=]\s*)[^\s,;\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CommonSecretRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)((?:Set-)?Cookie\s*:\s*)[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CookieRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\d .()\-]{7,}\d)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b[A-HJ-NPR-Z0-9]{17}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VinRegex();

    [GeneratedRegex(@"(?i)((?:first\s*name|last\s*name|full\s*name|customer\s*name|date\s*of\s*birth|dob|street\s*address|mailing\s*address|driver(?:'s)?\s*licen[cs]e|licen[cs]e\s*plate|bank\s*account|routing\s*number)\s*[:=]\s*)([^,;\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledPiiRegex();

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CreditCardCandidateRegex();
}
