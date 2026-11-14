using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevOpsAgent.Application.Agent;

public enum ToolAccess
{
    ReadOnly,
    Mutating
}

public sealed record ToolDescriptor(string Name, string Description, ToolAccess Access, Type ArgumentsType)
{
    public bool RequiresApproval => Access == ToolAccess.Mutating || Name == "run_shell";
}

public sealed class ToolRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IReadOnlyDictionary<string, ToolDescriptor> _tools;

    public ToolRegistry()
    {
        ToolDescriptor[] tools =
        [
            Define<ListServersArgs>("list_servers", "List inventoried servers and their groups.", ToolAccess.ReadOnly),
            Define<QueryGraphArgs>("query_graph", "Query known infrastructure nodes and connections.", ToolAccess.ReadOnly),
            Define<RunDiscoveryArgs>("run_discovery", "Run read-only discovery and save a snapshot.", ToolAccess.ReadOnly),
            Define<ServiceStatusArgs>("service_status", "Read the state of a systemd unit.", ToolAccess.ReadOnly),
            Define<RestartServiceArgs>("restart_service", "Restart a systemd unit.", ToolAccess.Mutating),
            Define<DockerPsArgs>("docker_ps", "List Docker containers.", ToolAccess.ReadOnly),
            Define<DockerLogsArgs>("docker_logs", "Read recent Docker container logs.", ToolAccess.ReadOnly),
            Define<TailLogArgs>("tail_log", "Read the end of a log file.", ToolAccess.ReadOnly),
            Define<ReadFileArgs>("read_file", "Read a text file from a server.", ToolAccess.ReadOnly),
            Define<DiskUsageArgs>("disk_usage", "Read filesystem usage and optionally inspect a path.", ToolAccess.ReadOnly),
            Define<ListUpdatesArgs>("list_updates", "List available operating system updates.", ToolAccess.ReadOnly),
            Define<ApplyUpdatesArgs>("apply_updates", "Install selected operating system updates.", ToolAccess.Mutating),
            Define<BlockIpArgs>("block_ip", "Block an IP address using the supported host firewall.", ToolAccess.Mutating),
            Define<UnblockIpArgs>("unblock_ip", "Remove an IP address firewall block.", ToolAccess.Mutating),
            Define<ListBansArgs>("list_bans", "List addresses currently blocked by host security tools.", ToolAccess.ReadOnly),
            Define<RunShellArgs>("run_shell", "Run a shell command. Always requires explicit approval.", ToolAccess.Mutating)
        ];
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ToolDescriptor> Tools => _tools.Values.ToArray();

    public bool TryGet(string name, out ToolDescriptor descriptor) => _tools.TryGetValue(name, out descriptor!);

    public object ValidateArguments(string name, JsonElement arguments)
    {
        if (!_tools.TryGetValue(name, out var descriptor))
        {
            throw new ArgumentException($"Unknown tool '{name}'.", nameof(name));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        var value = JsonSerializer.Deserialize(arguments, descriptor.ArgumentsType, JsonOptions)
            ?? throw new ArgumentException("Tool arguments could not be read.", nameof(arguments));
        var context = new ValidationContext(value);
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, context, errors, validateAllProperties: true))
        {
            throw new ArgumentException(string.Join(" ", errors.Select(error => error.ErrorMessage)), nameof(arguments));
        }

        return value;
    }

    private static ToolDescriptor Define<T>(string name, string description, ToolAccess access) where T : class =>
        new(name, description, access, typeof(T));
}

public sealed record EmptyToolArgs;
public sealed record ListServersArgs;

public sealed record QueryGraphArgs(
    [property: StringLength(300)] string? Query = null,
    [property: RegularExpression("^(service|network)$", ErrorMessage = "View must be service or network.")] string? View = null);

public sealed record RunDiscoveryArgs(IReadOnlyList<Guid>? ServerIds = null);

public sealed record ServiceStatusArgs(
    [property: Required, StringLength(200, MinimumLength = 1)] string Unit);

public sealed record RestartServiceArgs(
    [property: Required, StringLength(200, MinimumLength = 1)] string Unit);

public sealed record DockerPsArgs;

public sealed record DockerLogsArgs(
    [property: Required, StringLength(128, MinimumLength = 1)] string Container,
    [property: Range(1, 2000)] int Tail = 200);

public sealed record TailLogArgs(
    [property: Required, StringLength(1024, MinimumLength = 1)] string Path,
    [property: Range(1, 2000)] int Lines = 100);

public sealed record ReadFileArgs(
    [property: Required, StringLength(1024, MinimumLength = 1)] string Path,
    [property: Range(1, 1048576)] int MaxBytes = 65536);

public sealed record DiskUsageArgs([property: StringLength(1024)] string? Path = null);
public sealed record ListUpdatesArgs;

public sealed record ApplyUpdatesArgs(
    [property: MinLength(1), MaxLength(100)] IReadOnlyList<string> Packages);

public sealed record BlockIpArgs(
    [property: Required, StringLength(64, MinimumLength = 3)] string Address);

public sealed record UnblockIpArgs(
    [property: Required, StringLength(64, MinimumLength = 3)] string Address);

public sealed record ListBansArgs;

public sealed record RunShellArgs(
    [property: Required, StringLength(10000, MinimumLength = 1)] string Command);
