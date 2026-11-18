using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Llm;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class SecretHandlingTests
{
    [Theory]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://ollama:11434", true)]
    [InlineData("http://api.example.com/v1", false)]
    [InlineData("http://203.0.113.9:8080", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("not a url", false)]
    public void AllowsCredentials_RequiresHttpsUnlessTheHostIsPrivate(string url, bool expected) =>
        Assert.Equal(expected, LlmEndpointLocality.AllowsCredentials(url));

    [Fact]
    public void NginxRedactor_MasksCredentialsButKeepsProxyTargetsAndVariables()
    {
        const string config = """
            # configuration file /etc/nginx/nginx.conf:
            http {
            server {
                location / {
                    proxy_pass http://backend:8080;
                    proxy_set_header Authorization "Basic dXNlcjpzdXBlcnNlY3JldA==";
                    proxy_set_header X-Api-Key abc123def456;
                    proxy_set_header Authorization $http_authorization;
                    proxy_set_header Host $host;
                    set $api_token "tok-live-1234567890";
                    set $upstream http://app:5000;
                }
                location /legacy { proxy_pass http://admin:hunter2pw@legacy.internal:9000; }
            }
            }
            """;

        var redacted = NginxConfigRedactor.Redact(config);

        Assert.DoesNotContain("dXNlcjpzdXBlcnNlY3JldA", redacted);
        Assert.DoesNotContain("abc123def456", redacted);
        Assert.DoesNotContain("tok-live-1234567890", redacted);
        Assert.DoesNotContain("hunter2pw", redacted);
        Assert.Contains("proxy_pass http://backend:8080;", redacted);
        Assert.Contains("proxy_set_header Authorization $http_authorization;", redacted);
        Assert.Contains("proxy_set_header Host $host;", redacted);
        Assert.Contains("set $upstream http://app:5000;", redacted);
        Assert.Contains("legacy.internal:9000", redacted);
        // The parser still sees the same structure.
        var parsed = NginxCollectorParser.Parse(redacted);
        Assert.Contains(parsed.Servers.SelectMany(server => server.Proxies).Concat(parsed.Servers.SelectMany(server => server.Locations.SelectMany(location => location.Proxies))),
            proxy => proxy.Target == "http://backend:8080");
    }

    [Fact]
    public async Task DockerCollector_MasksSecretsInCmdEntrypointAndLabels()
    {
        const string inspect = """
            [{"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Name":"/api","State":{"Status":"running","Pid":5},
              "Config":{"Image":"app:1","Env":["DB_PASSWORD=env-secret-1"],
                "Cmd":["app","--password=cmd-secret-2","--api-key","cmd-secret-3","--port","8080"],
                "Entrypoint":["/bin/sh","-c","TOKEN=entry-secret-4 run"],
                "Labels":{"traefik.http.middlewares.auth.basicauth.users":"x","app.secret":"label-secret-5","com.docker.compose.project":"stack"}},
              "NetworkSettings":{"Networks":{},"Ports":{}}}]
            """;
        var runner = new InspectRunner(inspect);
        var collector = new DockerCollector();

        var output = await collector.CollectAsync(new ServerContext(
            new Server("h", "docker.example.internal", 22, "deploy", "id", "staging"), runner), CancellationToken.None);

        foreach (var secret in new[] { "env-secret-1", "cmd-secret-2", "cmd-secret-3", "entry-secret-4", "label-secret-5" })
            Assert.DoesNotContain(secret, output.StandardOutput);
        Assert.Contains("--port", output.StandardOutput);
        Assert.Contains("8080", output.StandardOutput);
        Assert.Contains("stack", output.StandardOutput);
        Assert.True(collector.Parse(output).IsSuccess);
    }

    private sealed class InspectRunner(string inspect) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            var output = command switch
            {
                "docker ps -q" => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n",
                var value when value.StartsWith("docker inspect ", StringComparison.Ordinal) => inspect,
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
