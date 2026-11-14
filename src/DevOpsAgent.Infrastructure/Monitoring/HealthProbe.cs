using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DevOpsAgent.Application.Monitoring;

namespace DevOpsAgent.Infrastructure.Monitoring;

public sealed class HealthProbe(IHttpClientFactory httpClientFactory, TimeProvider timeProvider) : IHealthProbe
{
    public async Task<HealthProbeResult> ProbeAsync(Domain.Monitoring.HealthCheckType type, string target,
        CancellationToken cancellationToken)
    {
        if (type == Domain.Monitoring.HealthCheckType.Tcp)
        {
            var address = new Uri($"tcp://{target}");
            using var client = new TcpClient();
            await client.ConnectAsync(address.DnsSafeHost, address.Port, cancellationToken);
            return new HealthProbeResult(true, null, null, null);
        }

        var uri = new Uri(target, UriKind.Absolute);
        var tlsDays = type == Domain.Monitoring.HealthCheckType.Https
            ? await GetTlsDaysRemainingAsync(uri, cancellationToken)
            : null;
        try
        {
            using var response = await httpClientFactory.CreateClient("health")
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var healthy = (int)response.StatusCode is >= 200 and < 400 && tlsDays is not < 0;
            var error = tlsDays is < 0 ? "TLS certificate has expired." : healthy ? null : $"HTTP returned {(int)response.StatusCode}.";
            return new HealthProbeResult(healthy, (int)response.StatusCode, tlsDays, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new HealthProbeResult(false, null, tlsDays, exception.Message);
        }
    }

    private async Task<int?> GetTlsDaysRemainingAsync(Uri uri, CancellationToken cancellationToken)
    {
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(uri.DnsSafeHost, port, cancellationToken);
            DateTimeOffset? expiresAt = null;
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, _, errors) =>
                {
                    if (certificate is X509Certificate2 certificate2)
                        expiresAt = new DateTimeOffset(certificate2.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                    else if (certificate is not null)
                        expiresAt = new DateTimeOffset(DateTime.Parse(certificate.GetExpirationDateString()).ToUniversalTime(), TimeSpan.Zero);
                    return errors == SslPolicyErrors.None;
                });
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.DnsSafeHost }, cancellationToken);
            }
            catch (AuthenticationException) when (expiresAt.HasValue) { }

            return expiresAt is null ? null : (int)Math.Floor((expiresAt.Value - timeProvider.GetUtcNow()).TotalDays);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}
