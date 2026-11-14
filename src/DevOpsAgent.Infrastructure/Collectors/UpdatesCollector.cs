using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Infrastructure.Parsers;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class UpdatesCollector : ICollector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    public string Name => "updates";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id,
            "command -v apt-get >/dev/null 2>&1 || command -v dnf >/dev/null 2>&1",
            new SshRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var apt = await context.SshRunner.RunAsync(context.Server.Id, "command -v apt-get >/dev/null 2>&1",
            new SshRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        if (apt.ExitCode == 0)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id,
                "apt-get update -qq && apt list --upgradable 2>/dev/null",
                new SshRunOptions(Timeout, UseSudo: true), cancellationToken);
            return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput(result.StandardOutput, "", "",
                result.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(result.StandardError) ? "apt-get update or apt list failed." : result.StandardError)),
                result.StandardError, result.ExitCode, result.Duration);
        }

        var dnfResult = await context.SshRunner.RunAsync(context.Server.Id,
            "dnf check-update; result=$?; if [ \"$result\" -ne 0 ] && [ \"$result\" -ne 100 ]; then exit \"$result\"; fi",
            new SshRunOptions(Timeout), cancellationToken);
        var security = await context.SshRunner.RunAsync(context.Server.Id, "dnf updateinfo list security",
            new SshRunOptions(Timeout), cancellationToken);
        var error = dnfResult.ExitCode == 0 || dnfResult.ExitCode == 100 ? null
            : string.IsNullOrWhiteSpace(dnfResult.StandardError) ? "dnf check-update failed." : dnfResult.StandardError;
        return new CollectorOutput(JsonSerializer.Serialize(new UpdateCollectionOutput("", dnfResult.StandardOutput,
            security.StandardOutput, error)), dnfResult.StandardError + security.StandardError,
            error is null ? 0 : dnfResult.ExitCode, dnfResult.Duration + security.Duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        var data = JsonSerializer.Deserialize<UpdateCollectionOutput>(output.StandardOutput);
        if (data is null) return new CollectorResult(Error: "Update command produced no parseable output.");
        if (data.Error is not null) return new CollectorResult(Error: data.Error);
        var packages = data.AptOutput.Length > 0 ? UpdatesParser.ParseApt(data.AptOutput)
            : UpdatesParser.ParseDnf(data.DnfOutput, data.SecurityOutput);
        return new CollectorResult(JsonSerializer.SerializeToElement(packages));
    }
}
