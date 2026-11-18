using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class NginxRelativeIncludeTests
{
    [Fact]
    public void RelativeIncludeIsResolvedAgainstTheNginxPrefix()
    {
        const string output = """
            # configuration file /etc/nginx/nginx.conf:
            user www-data;
            http {
                include conf.d/*.conf;
                include ./sites/app.conf;
            }
            # configuration file /etc/nginx/conf.d/web.conf:
            server {
                listen 80;
                server_name web.example.test;
                location / { proxy_pass http://web_backend:8080; }
            }
            # configuration file /etc/nginx/sites/app.conf:
            server {
                listen 81;
                server_name app.example.test;
                location / { proxy_pass http://app_backend:9090; }
            }
            """;

        var result = NginxCollectorParser.Parse(output);

        Assert.Equal(2, result.Servers.Count);
        Assert.Contains(result.Servers, server => server.ServerNames.Any(name => name.Value == "web.example.test")
            && server.Context == "http");
        Assert.Contains(result.Servers, server => server.ServerNames.Any(name => name.Value == "app.example.test")
            && server.Context == "http");
    }

    [Fact]
    public void RelativeIncludeFollowsTheMainFileDirectory()
    {
        const string output = """
            # configuration file /opt/proxy/conf/nginx.conf:
            http {
                include vhosts/*.conf;
            }
            # configuration file /opt/proxy/conf/vhosts/a.conf:
            server {
                listen 80;
                server_name a.example.test;
                location / { proxy_pass http://a_backend:1; }
            }
            """;

        var result = NginxCollectorParser.Parse(output);

        Assert.Contains(result.Servers, server => server.ServerNames.Any(name => name.Value == "a.example.test"));
    }

    [Fact]
    public void AbsoluteIncludeStillMatches()
    {
        const string output = """
            # configuration file /etc/nginx/nginx.conf:
            http {
                include /etc/nginx/sites/*.conf;
            }
            # configuration file /etc/nginx/sites/x.conf:
            server {
                listen 80;
                server_name x.example.test;
                location / { proxy_pass http://x_backend:1; }
            }
            """;

        var result = NginxCollectorParser.Parse(output);

        Assert.Contains(result.Servers, server => server.ServerNames.Any(name => name.Value == "x.example.test"));
    }
}
