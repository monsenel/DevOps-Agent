using System.Text.Json;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DevOpsAgent.DocsDemo DATABASE_PATH");
    return 2;
}

var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = args[0] }.ToString();
var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
await using var db = new AppDbContext(options);
await db.Database.MigrateAsync();
if (await db.Servers.AnyAsync())
{
    Console.Error.WriteLine("The documentation demo seeder requires a fresh database.");
    return 3;
}

var now = DateTimeOffset.UtcNow;
var servers = new[]
{
    NewServer("edge-01", "192.0.2.10"),
    NewServer("app-01", "192.0.2.20"),
    NewServer("data-01", "192.0.2.30"),
};
db.Servers.AddRange(servers);
await db.SaveChangesAsync();

var snapshot = new Snapshot(servers.Select(server => server.Id).ToArray(), now);
snapshot.Complete(now, servers.Length, 0);
db.Snapshots.Add(snapshot);

GraphNode Node(string name, GraphNodeKind kind, string label, Server? server, Guid? parent, object attributes) =>
    new(snapshot.Id, $"demo:{name}", kind, label, parent, server?.Id, JsonSerializer.Serialize(attributes));

var edgeHost = Node("edge", GraphNodeKind.Host, "edge-01", servers[0], null,
    new { Host = servers[0].Host, Port = 1, User = "demo", Group = "Personal", Hostname = "edge-01", Tags = new[] { "personal" }, Addresses = new[] { "192.0.2.10" } });
var appHost = Node("app", GraphNodeKind.Host, "app-01", servers[1], null,
    new { Host = servers[1].Host, Port = 1, User = "demo", Group = "Personal", Hostname = "app-01", Tags = new[] { "personal" }, Addresses = new[] { "192.0.2.20" } });
var dataHost = Node("data", GraphNodeKind.Host, "data-01", servers[2], null,
    new { Host = servers[2].Host, Port = 1, User = "demo", Group = "Personal", Hostname = "data-01", Tags = new[] { "personal" }, Addresses = new[] { "192.0.2.30" } });
var nginx = Node("nginx", GraphNodeKind.Service, "nginx", servers[0], edgeHost.Id, new { ProcessName = "nginx", ProcessId = 100 });
var proxy = Node("proxy", GraphNodeKind.Service, "server block · api.example.test", servers[0], edgeHost.Id,
    new { Context = "server", ServerNames = new[] { "api.example.test" } });
var api = Node("api", GraphNodeKind.Container, "api", servers[1], appHost.Id,
    new { Image = "example/api:1.0", State = "running", ComposeProject = "personal", ComposeService = "api", PublishedPorts = Array.Empty<object>() });
var postgres = Node("postgres", GraphNodeKind.Service, "postgresql", servers[2], dataHost.Id, new { ProcessName = "postgresql", ProcessId = 300 });
var domain = Node("domain", GraphNodeKind.Domain, "api.example.test", null, null, new { Domain = "api.example.test" });
var nodes = new[] { edgeHost, appHost, dataHost, nginx, proxy, api, postgres, domain };
db.GraphNodes.AddRange(nodes);
var edges = new[]
{
    new GraphEdge(snapshot.Id, domain.Id, proxy.Id, GraphEdgeKind.Declared, 443, "https"),
    new GraphEdge(snapshot.Id, proxy.Id, api.Id, GraphEdgeKind.Declared, 8080, "http"),
    new GraphEdge(snapshot.Id, api.Id, postgres.Id, GraphEdgeKind.Observed, 5432, "tcp"),
};
db.GraphEdges.AddRange(edges);
db.GraphEvidence.AddRange(edges.Select(edge => new GraphEvidence(snapshot.Id, null, edge.Id, "documentation", "Connection evidence from the screenshot capture.")));
db.GraphListeners.AddRange(new[]
{
    new GraphListener(snapshot.Id, nginx.Id, "0.0.0.0", 443, "tcp", "nginx", null),
    new GraphListener(snapshot.Id, api.Id, "0.0.0.0", 8080, "tcp", "api", null),
    new GraphListener(snapshot.Id, postgres.Id, "0.0.0.0", 5432, "tcp", "postgresql", null),
});

foreach (var (server, cpu, load, disk) in new[]
         {
             (servers[0], 18d, .24d, 42d),
             (servers[1], 74d, 1.84d, 92d),
             (servers[2], 31d, .62d, 42d),
         })
{
    db.MetricSamples.AddRange(new[]
    {
        new MetricSample(server.Id, "cpu_percent", cpu, now),
        new MetricSample(server.Id, "load_1m", load, now),
        new MetricSample(server.Id, "cpu_cores", 4, now),
        new MetricSample(server.Id, "memory_percent", 56, now),
        new MetricSample(server.Id, "disk_percent", disk, now),
    });
    db.UpdateScans.Add(new UpdateScan(server.Id, "apt", now, null, now, cacheOnly: true));
}
var packageRows = new[]
{
    (servers[0], "nginx", "1.24.0", "1.24.1", true),
    (servers[1], "openssl", "3.0.2", "3.0.3", true),
    (servers[1], "curl", "7.81.0", "7.88.1", false),
    (servers[2], "postgresql-16", "16.2", "16.3", false),
};
foreach (var (server, name, current, candidate, security) in packageRows)
    db.UpdatePackages.Add(new UpdatePackage(server.Id, name, current, candidate, "Ubuntu archive", security, now));

await db.SaveChangesAsync();
Console.WriteLine("Seeded documentation records.");
return 0;

Server NewServer(string name, string address)
{
    var server = new Server(name, "127.0.0.1", 1, "demo", "/keys/demo", "Personal");
    server.RecordCapabilities(false, "Linux", now);
    return server;
}
