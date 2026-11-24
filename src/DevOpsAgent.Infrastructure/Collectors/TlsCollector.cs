using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Read-only TLS certificate metadata collector. Inspects ONLY public X.509 certificate metadata via
/// <c>openssl x509 -noout</c> (skipping key files) so private key material is never read or stored.
/// </summary>
public sealed class TlsCollector : ICollector
{
    public const string Command =
        "for f in /etc/letsencrypt/live/*/fullchain.pem /etc/nginx/ssl/*.crt /etc/nginx/ssl/*.pem /etc/haproxy/certs/*.crt /etc/traefik/certs/*.crt /etc/caddy/certificates/*.crt; do " +
        "[ -f \"$f\" ] || continue; " +
        "case \"$(basename \"$f\")\" in *key*|*priv*) continue;; esac; " +
        "printf '__DVA_CERT__:%s\\n' \"$f\"; " +
        "openssl x509 -in \"$f\" -noout -subject -issuer -dates -serial -ext subjectAltName 2>/dev/null || true; " +
        "done";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "tls";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v openssl >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, Command,
            new SshRunOptions(CommandTimeout, context.Server.CanUseSudo), cancellationToken);
        return new CollectorOutput(result.StandardOutput, result.StandardError, result.ExitCode, result.Duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        var parsed = TlsCollectorParser.Parse(output.StandardOutput);
        return new CollectorResult(JsonSerializer.SerializeToElement(parsed));
    }
}
