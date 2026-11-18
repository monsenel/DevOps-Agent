using System.Net;
using System.Net.Sockets;

namespace DevOpsAgent.Application.Llm;

/// <summary>Decides whether an LLM base URL points at infrastructure the owner controls (no secret redaction needed).</summary>
public static class LlmEndpointLocality
{
    /// <summary>True when credentials may be sent to <paramref name="baseUrl"/>: HTTPS, or HTTP to a loopback/private/single-label host.</summary>
    public static bool AllowsCredentials(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsLocal(baseUrl);
    }

    public static bool IsLocal(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Length == 0) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address)) return IsPrivateAddress(address);

        // A single-label name (for example the Compose service "ollama") can only resolve on a private network.
        return !host.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254;
        }

        // IPv6 unique local (fc00::/7) and link-local (fe80::/10).
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && ((bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal);
    }
}
