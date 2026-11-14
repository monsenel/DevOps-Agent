using System.Text.RegularExpressions;

namespace DevOpsAgent.Application.Discovery;

/// <summary>Removes <c>user:password@</c> credentials from URLs before they reach labels, evidence, or logs.</summary>
public static partial class UrlUserInfo
{
    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^/\s@]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoRegex();

    public static string Strip(string value) =>
        string.IsNullOrEmpty(value) || !value.Contains('@', StringComparison.Ordinal)
            ? value
            : UserInfoRegex().Replace(value, "${scheme}");
}
