using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevOpsAgent.Application.Agent;

public enum ToolAccess
{
    ReadOnly,
    Mutating
}

public sealed record ToolDescriptor(string Name, string Description, ToolAccess Access, Type ArgumentsType, bool RequiresServer = true)
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
            Define<ListServersArgs>("list_servers", "List inventoried servers and their groups.", ToolAccess.ReadOnly, requiresServer: false),
            Define<QueryGraphArgs>("query_graph", "Query known infrastructure nodes and connections.", ToolAccess.ReadOnly, requiresServer: false),
            Define<RunDiscoveryArgs>("run_discovery", "Run read-only discovery and save a snapshot.", ToolAccess.ReadOnly, requiresServer: false),
            Define<ServiceStatusArgs>("service_status", "Read the state of a systemd unit.", ToolAccess.ReadOnly),
            Define<RestartServiceArgs>("restart_service",
                "Restart a systemd unit. Critical units (ssh, sshd, docker, containerd) are refused unless confirmCritical is true after explicit owner confirmation.",
                ToolAccess.Mutating),
            Define<RestartContainerArgs>("restart_container", "Restart a Docker container. Always requires explicit approval.", ToolAccess.Mutating),
            Define<DockerPsArgs>("docker_ps", "List Docker containers.", ToolAccess.ReadOnly),
            Define<DockerDiskUsageArgs>("docker_disk_usage", "Read Docker image, container, and volume disk usage.", ToolAccess.ReadOnly),
            Define<DockerLogsArgs>("docker_logs", "Read recent Docker container logs.", ToolAccess.ReadOnly),
            Define<TailLogArgs>("tail_log", "Read the end of a log file.", ToolAccess.ReadOnly),
            Define<ReadFileArgs>("read_file", "Read a text file from a server.", ToolAccess.ReadOnly),
            Define<DiskUsageArgs>("disk_usage", "Read filesystem usage and optionally inspect a path.", ToolAccess.ReadOnly),
            Define<ListUpdatesArgs>("list_updates", "List available operating system updates.", ToolAccess.ReadOnly),
            Define<ApplyUpdatesArgs>("apply_updates", "Install selected operating system updates.", ToolAccess.Mutating),
            Define<BlockIpArgs>("block_ip", "Block an IP address using the supported host firewall.", ToolAccess.Mutating),
            Define<UnblockIpArgs>("unblock_ip", "Remove an IP address firewall block.", ToolAccess.Mutating),
            Define<ListBansArgs>("list_bans", "List addresses currently blocked by host security tools.", ToolAccess.ReadOnly),
            Define<SecurityAuditArgs>("security_audit", "Run a read-only security hardening audit (SSH daemon config, host firewall, fail2ban, unattended updates) on one or more servers.", ToolAccess.ReadOnly),
            Define<RunShellArgs>("run_shell", "Run a shell command. Always requires explicit approval.", ToolAccess.Mutating),
            Define<EditConfigArgs>("edit_config", "Create (with create: true) or update a configuration file on one server "
                + "(allowlisted paths: /etc/nginx/**, /etc/systemd/system/*.service, /opt/**/compose.yml, /opt/**/docker-compose.yml, "
                + "/srv/**/compose.yml, /srv/**/docker-compose.yml). Give the full new content or a unified diff against the current file. "
                + "Always requires approval; the file is backed up (or removed on rollback when newly created), validated (nginx -t, "
                + "systemd-analyze verify, or docker compose config -q), reloaded where applicable, health-checked, and rolled back on any failure.", ToolAccess.Mutating)
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

        object value;
        try
        {
            value = JsonSerializer.Deserialize(arguments, descriptor.ArgumentsType, JsonOptions)
                ?? throw new ArgumentException("Tool arguments could not be read.", nameof(arguments));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Tool arguments do not match the registered parameter type.", nameof(arguments), exception);
        }
        var context = new ValidationContext(value);
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, context, errors, validateAllProperties: true))
        {
            throw new ArgumentException(string.Join(" ", errors.Select(error => error.ErrorMessage)), nameof(arguments));
        }

        return value;
    }

    private static ToolDescriptor Define<T>(string name, string description, ToolAccess access, bool requiresServer = true) where T : class =>
        new(name, description, access, typeof(T), requiresServer);
}

public sealed record EmptyToolArgs;
public sealed record ListServersArgs;

public interface IServerTargetArgs
{
    IReadOnlyList<Guid>? ServerIds { get; }
}

public sealed record QueryGraphArgs(
    [property: StringLength(300)] string? Query = null,
    [property: RegularExpression("\\A(service|network)\\z", ErrorMessage = "View must be service or network.")] string? View = null);

public sealed record RunDiscoveryArgs(IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

public sealed record ServiceStatusArgs(
    [property: Required, StringLength(200, MinimumLength = 1), RegularExpression("\\A[A-Za-z0-9][A-Za-z0-9@_.:-]*\\z")] string Unit,
    [property: MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

public sealed record RestartServiceArgs(
    [property: Required, StringLength(200, MinimumLength = 1), RegularExpression("\\A[A-Za-z0-9][A-Za-z0-9@_.:-]*\\z")] string Unit,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null,
    bool ConfirmCritical = false) : IServerTargetArgs;

public sealed record DockerPsArgs([property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;
public sealed record DockerDiskUsageArgs([property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

public sealed record DockerLogsArgs(
    [property: Required, StringLength(128, MinimumLength = 1), RegularExpression("\\A[A-Za-z0-9][A-Za-z0-9_.-]*\\z")] string Container,
    [property: Range(1, 2000)] int Tail = 200,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

public sealed record TailLogArgs(
    [property: Required, StringLength(1024, MinimumLength = 1)] string Path,
    [property: Range(1, 2000)] int Lines = 100,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IValidatableObject, IServerTargetArgs
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SensitivePathPolicy.Check(Path) is { } error) yield return new ValidationResult(error, [nameof(Path)]);
    }
}

public sealed record ReadFileArgs(
    [property: Required, StringLength(1024, MinimumLength = 1)] string Path,
    [property: Range(1, 1048576)] int MaxBytes = 65536,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IValidatableObject, IServerTargetArgs
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SensitivePathPolicy.Check(Path) is { } error) yield return new ValidationResult(error, [nameof(Path)]);
    }
}

public sealed record DiskUsageArgs([property: StringLength(1024)] string? Path = null,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;
public sealed record RestartContainerArgs(
    [property: Required, StringLength(128, MinimumLength = 1), RegularExpression("\\A[A-Za-z0-9][A-Za-z0-9_.-]*\\z")] string Container,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;
public sealed record ListUpdatesArgs([property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

public sealed record ApplyUpdatesArgs(
    [property: Required, MinLength(1), MaxLength(100)] IReadOnlyList<string> Packages,
    [property: MinLength(1), MaxLength(1)] IReadOnlyList<Guid>? ServerIds = null) : IValidatableObject, IServerTargetArgs
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Packages is null || Packages.Any(package => string.IsNullOrWhiteSpace(package)
                || package.Length > 200
                || !char.IsAsciiLetterOrDigit(package[0])
                || package.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '+' or ':' or '@' or '_' or '-'))))
        {
            yield return new ValidationResult("Package names contain unsupported characters.", [nameof(Packages)]);
        }
    }
}

public sealed record BlockIpArgs(
    [property: Required, StringLength(64, MinimumLength = 3)] string Address,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IValidatableObject, IServerTargetArgs
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(Address, out _))
        {
            yield return new ValidationResult("Address must be a valid IPv4 or IPv6 address.", [nameof(Address)]);
        }
    }
}

public sealed record UnblockIpArgs(
    [property: Required, StringLength(64, MinimumLength = 3)] string Address,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IValidatableObject, IServerTargetArgs
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(Address, out _))
        {
            yield return new ValidationResult("Address must be a valid IPv4 or IPv6 address.", [nameof(Address)]);
        }
    }
}

public sealed record ListBansArgs([property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;
public sealed record SecurityAuditArgs([property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;

/// <summary>
/// edit_config (T8.8). Proposed with <see cref="Content"/> or <see cref="Diff"/>; before approval the app reads the file and
/// stores the full new <see cref="Content"/>, the current file's <see cref="BaseSha256"/> and the <see cref="Diff"/> to review.
/// </summary>
public sealed record EditConfigArgs(
    [property: Required, StringLength(1024, MinimumLength = 2)] string Path,
    [property: StringLength(1_048_576)] string? Content = null,
    [property: StringLength(1_048_576)] string? Diff = null,
    [property: RegularExpression("\\A[0-9a-f]{64}\\z")] string? BaseSha256 = null,
    [property: MinLength(1), MaxLength(1)] IReadOnlyList<Guid>? ServerIds = null,
    bool Create = false) : IValidatableObject, IServerTargetArgs
{
    public bool IsPrepared => BaseSha256 is not null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The allowlist is checked with the configured patterns when the edit is prepared and again when it runs.
        if (SensitivePathPolicy.Check(Path) is { } sensitive) yield return new ValidationResult(sensitive, [nameof(Path)]);
        else if (ConfigEditPolicy.Check(Path, ["/**"]) is { } shape) yield return new ValidationResult(shape, [nameof(Path)]);
        if (!IsPrepared && Create && (Content is null || Diff is not null))
            yield return new ValidationResult("Creating a new file requires full content, not a diff.", [nameof(Content), nameof(Diff)]);
        else if (!IsPrepared && (Content is null) == (Diff is null))
            yield return new ValidationResult("Give either the full new content or a unified diff, not both.", [nameof(Content), nameof(Diff)]);
        if (IsPrepared && Content is null)
            yield return new ValidationResult("A prepared edit carries the full new content.", [nameof(Content)]);
    }
}

public sealed record RunShellArgs(
    [property: Required, StringLength(10000, MinimumLength = 1)] string Command,
    [property: MinLength(1), MaxLength(50)] IReadOnlyList<Guid>? ServerIds = null) : IServerTargetArgs;
