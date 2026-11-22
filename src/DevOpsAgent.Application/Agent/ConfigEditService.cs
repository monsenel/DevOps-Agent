using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Agent;

/// <summary>
/// The edit_config tool (T8.8). Before approval the current file is read and the edit turned into the full new content, the
/// current file's hash and a diff to review. On approval: the new content is staged in a private directory by the SSH user,
/// then, as root in one step, the file is checked unchanged, backed up to <c>path.bak-&lt;unix time&gt;</c>, replaced
/// atomically (temporary file beside it, then <c>mv</c>), validated and its service reloaded, restoring the backup if either
/// fails; then health-checked (the server's health checks and its nginx domains answering through the listener), and on a
/// failed check restored and reloaded again. Every step's outcome is reported.
/// </summary>
public sealed partial class ConfigEditService(
    ISshRunner sshRunner,
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs,
    IHealthCheckRepository healthChecks,
    IHealthProbe healthProbe,
    ConfigEditOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Sentinel base hash stored when <see cref="EditConfigArgs.Create"/> creates a brand-new file (T9.2).</summary>
    public const string EmptyFileSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Reads the file and returns the arguments to store with the proposal, or an error for the model. Read-only on the server.
    /// </summary>
    public async Task<(JsonElement? Arguments, string? Error)> PrepareAsync(EditConfigArgs args, CancellationToken cancellationToken)
    {
        if (ConfigEditPolicy.Check(args.Path, options.AllowedPaths) is { } refused) return (null, refused);
        var rule = options.RuleFor(args.Path);
        if (rule is null || string.IsNullOrWhiteSpace(rule.Validate))
            return (null, $"No validation rule matches '{args.Path}'. Configure a ConfigEdit:Services rule with a validation command before editing it.");
        if (args.ServerIds is not [var serverId]) return (null, "edit_config changes one server at a time.");
        var server = await servers.GetAsync(serverId, cancellationToken);
        if (server is null) return (null, "The target server no longer exists.");

        if (args.Create)
        {
            if (args.Content is null) return (null, "Creating a new file requires full content, not a diff.");
            var existing = await ReadAsync(server, args.Path, cancellationToken);
            if (existing.NotFound)
            {
                var created = args.Content;
                if (Encoding.UTF8.GetByteCount(created) > options.MaxContentBytes) return (null, $"The new content is larger than {options.MaxContentBytes} bytes.");
                if (created.Contains('\0')) return (null, "The new content contains NUL bytes.");
                if (created.Length == 0) return (null, "The new file content cannot be empty.");
                var createdPrepared = new EditConfigArgs(args.Path, created, UnifiedDiff.Create(string.Empty, created, args.Path), EmptyFileSha256, [serverId], Create: true);
                return (JsonSerializer.SerializeToElement(createdPrepared, JsonOptions), null);
            }

            return existing.Error is not null
                ? (null, existing.Error)
                : (null, $"{args.Path} already exists on {server.Name}; omit create: true to edit it.");
        }

        var read = await ReadAsync(server, args.Path, cancellationToken);
        if (read.Error is not null) return (null, read.Error);
        var current = read.Content!;
        string updated;
        try { updated = args.Content ?? UnifiedDiff.Apply(current, args.Diff!); }
        catch (ArgumentException exception) { return (null, $"The diff cannot be applied: {exception.Message}"); }
        if (Encoding.UTF8.GetByteCount(updated) > options.MaxContentBytes) return (null, $"The new content is larger than {options.MaxContentBytes} bytes.");
        if (updated.Contains('\0')) return (null, "The new content contains NUL bytes.");
        if (updated == current) return (null, "The new content is the same as the current file; nothing to change.");

        var prepared = new EditConfigArgs(args.Path, updated, UnifiedDiff.Create(current, updated, args.Path), Sha256(current), [serverId]);
        return (JsonSerializer.SerializeToElement(prepared, JsonOptions), null);
    }

    // Read-only: as the SSH user first, then with sudo when the file is not readable and the server allows sudo.
    private async Task<(string? Content, string? Error, bool NotFound)> ReadAsync(Server server, string path, CancellationToken cancellationToken)
    {
        var command = ReadCommand(path, options.MaxContentBytes + 1);
        var result = await sshRunner.RunAsync(server.Id, command, new SshRunOptions(TimeSpan.FromSeconds(30)), cancellationToken);
        if (result.ExitCode != 0 && !result.StandardError.Contains("DVA_NO_FILE", StringComparison.Ordinal)
            && !result.StandardError.Contains("DVA_SYMLINK=", StringComparison.Ordinal) && server.CanUseSudo)
            result = await sshRunner.RunAsync(server.Id, command, new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: true), cancellationToken);
        if (result.StandardError.Contains("DVA_NO_FILE", StringComparison.Ordinal))
            return (null, $"{path} does not exist on {server.Name}; pass create: true to create a new file.", true);
        if (Symlink().Match(result.StandardError) is { Success: true } link)
        {
            // The resolved path comes from the server: it is only repeated when it is a plain, permitted path.
            var resolved = link.Groups[1].Value;
            var hint = ConfigEditPolicy.Check(resolved, ["/**"]) is null ? $" (it resolves to {resolved})" : string.Empty;
            return (null, $"{path} is a symbolic link or lies behind one{hint}; edit_config changes real files only, so give the real path.", false);
        }

        if (result.ExitCode != 0) return (null, $"{path} could not be read on {server.Name} (exit {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).", false);
        if (Encoding.UTF8.GetByteCount(result.StandardOutput) > options.MaxContentBytes)
            return (null, $"{path} is larger than {options.MaxContentBytes} bytes.", false);
        return (result.StandardOutput, null, false);
    }

    /// <summary>Read-only; sent with sudo only when the SSH user cannot read the file.</summary>
    public static string ReadCommand(string path, int maxBytes) =>
        $"test -f {Quote(path)} || {{ printf '%s\\n' 'DVA_NO_FILE' >&2; exit 3; }}; "
        + $"[ \"$(readlink -f -- {Quote(path)})\" = {Quote(path)} ] || {{ printf 'DVA_SYMLINK=%s\\n' \"$(readlink -f -- {Quote(path)})\" >&2; exit 4; }}; "
        + $"head -c {maxBytes.ToString(CultureInfo.InvariantCulture)} -- {Quote(path)}";

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The private staging directory of an action: created by the SSH user (mode 700), removed after the run.</summary>
    public static string StagingDirectory(Guid actionId) => $"/tmp/dva-edit-{actionId:N}";

    public static long Timestamp(ActionRecord action) => action.CreatedAtUtc.ToUnixTimeSeconds();

    /// <summary>The dot-prefixed backup file beside <paramref name="path"/> so globs like <c>sites-enabled/*</c> never include it (T7.R28).</summary>
    public static string BackupPath(string path, long timestamp)
    {
        var slash = path.LastIndexOf('/');
        var dir = slash <= 0 ? "/" : path[..slash];
        var file = slash < 0 ? path : path[(slash + 1)..];
        return $"{(dir == "/" ? string.Empty : dir)}/.{file}.bak-{timestamp.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The root step: check, back up (or create), replace atomically, validate, reload; restores backup or deletes new file on any failure.</summary>
    public static string ApplyCommand(string path, string stagedFile, long timestamp, string baseSha256, ConfigServiceRule? rule, string? newSha256 = null, bool create = false)
    {
        var stagedHashCheck = string.IsNullOrWhiteSpace(newSha256)
            ? string.Empty
            : "if [ \"$(sha256sum < \"$s\" | cut -d ' ' -f 1)\" != " + newSha256 + " ]; then printf '%s\\n' 'DVA_EDIT_FAILED=staged_hash'; exit 14; fi; ";

        if (create)
        {
            var undoCreate = "if rm -f -- \"$p\"; then printf '%s\\n' 'DVA_RESTORED'" + (string.IsNullOrWhiteSpace(rule?.Reload) ? string.Empty
                : "; if " + Group(rule!.Reload) + " >/dev/null 2>&1; then printf '%s\\n' 'DVA_RELOADED_AFTER_RESTORE'; fi")
                + "; else printf '%s\\n' 'DVA_RESTORE_FAILED'; fi";
            return "set -u; p=" + Quote(path) + "; s=" + Quote(stagedFile) + "; d=$(dirname -- \"$p\"); f=$(basename -- \"$p\"); "
                + "if [ -e \"$p\" ] || [ -L \"$p\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=exists'; exit 15; fi; "
                + "if [ ! -f \"$s\" ] || [ -L \"$s\" ] || [ \"$(readlink -f -- \"$s\")\" != \"$s\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=staged_symlink'; exit 13; fi; "
                + stagedHashCheck
                + "if ! mkdir -p -- \"$d\"; then printf '%s\\n' 'DVA_EDIT_FAILED=write'; exit 6; fi; "
                + "if [ \"$(readlink -f -- \"$d\")\" != \"$d\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=symlink'; exit 12; fi; "
                + "t=$(mktemp -- \"$d/.$f.dva-XXXXXX\") || { printf '%s\\n' 'DVA_EDIT_FAILED=write'; exit 6; }; "
                + "if ! cat -- \"$s\" > \"$t\" || ! chmod 0644 -- \"$t\" || ! mv -f -- \"$t\" \"$p\"; then "
                + "rm -f -- \"$t\"; printf '%s\\n' 'DVA_EDIT_FAILED=write'; exit 6; fi; printf '%s\\n' 'DVA_WRITTEN'; "
                + (string.IsNullOrWhiteSpace(rule?.Validate) ? "printf '%s\\n' 'DVA_VALIDATE_SKIPPED'; "
                    : "if ! " + Group(rule!.Validate) + " 2>&1; then printf '%s\\n' 'DVA_EDIT_FAILED=validate'; " + undoCreate + "; exit 7; fi; printf '%s\\n' 'DVA_VALIDATED'; ")
                + (string.IsNullOrWhiteSpace(rule?.Reload) ? "printf '%s\\n' 'DVA_RELOAD_SKIPPED'"
                    : "if ! " + Group(rule!.Reload) + " 2>&1; then printf '%s\\n' 'DVA_EDIT_FAILED=reload'; " + undoCreate + "; exit 8; fi; printf '%s\\n' 'DVA_RELOADED'");
        }

        // "Restored" is only printed when the copy worked; a failed copy says so, and nothing is reloaded from a file that is not
        // the original.
        var restore = "if cp -p -- \"$b\" \"$p\"; then printf '%s\\n' 'DVA_RESTORED'" + (string.IsNullOrWhiteSpace(rule?.Reload) ? string.Empty
            : "; if " + Group(rule!.Reload) + " >/dev/null 2>&1; then printf '%s\\n' 'DVA_RELOADED_AFTER_RESTORE'; fi")
            + "; else printf '%s\\n' 'DVA_RESTORE_FAILED'; fi";
        return "set -u; p=" + Quote(path) + "; s=" + Quote(stagedFile) + "; d=$(dirname -- \"$p\"); f=$(basename -- \"$p\"); b=\"$d/.$f.bak-" + timestamp.ToString(CultureInfo.InvariantCulture) + "\"; "
            + "if [ ! -f \"$p\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=missing'; exit 3; fi; "
            // The allowlist and the deny list judge the text of the path. A link in it (the file or a directory) could lead
            // anywhere, and the final rename would replace a link with a regular file.
            + "if [ \"$(readlink -f -- \"$p\")\" != \"$p\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=symlink'; exit 12; fi; "
            + "if [ ! -f \"$s\" ] || [ -L \"$s\" ] || [ \"$(readlink -f -- \"$s\")\" != \"$s\" ]; then printf '%s\\n' 'DVA_EDIT_FAILED=staged_symlink'; exit 13; fi; "
            + stagedHashCheck
            + "if [ \"$(sha256sum < \"$p\" | cut -d ' ' -f 1)\" != " + baseSha256 + " ]; then printf '%s\\n' 'DVA_EDIT_FAILED=changed'; exit 4; fi; "
            + "if ! cp -p -- \"$p\" \"$b\"; then printf '%s\\n' 'DVA_EDIT_FAILED=backup'; exit 5; fi; printf 'DVA_BACKUP=%s\\n' \"$b\"; "
            + "t=$(mktemp -- \"$d/.$f.dva-XXXXXX\") || { printf '%s\\n' 'DVA_EDIT_FAILED=write'; exit 6; }; "
            + "if ! cat -- \"$s\" > \"$t\" || ! chmod --reference=\"$p\" -- \"$t\" || ! chown --reference=\"$p\" -- \"$t\" || ! mv -f -- \"$t\" \"$p\"; then "
            + "rm -f -- \"$t\"; printf '%s\\n' 'DVA_EDIT_FAILED=write'; exit 6; fi; printf '%s\\n' 'DVA_WRITTEN'; "
            + (string.IsNullOrWhiteSpace(rule?.Validate) ? "printf '%s\\n' 'DVA_VALIDATE_SKIPPED'; "
                : "if ! " + Group(rule!.Validate) + " 2>&1; then printf '%s\\n' 'DVA_EDIT_FAILED=validate'; " + restore + "; exit 7; fi; printf '%s\\n' 'DVA_VALIDATED'; ")
            + (string.IsNullOrWhiteSpace(rule?.Reload) ? "printf '%s\\n' 'DVA_RELOAD_SKIPPED'"
                : "if ! " + Group(rule!.Reload) + " 2>&1; then printf '%s\\n' 'DVA_EDIT_FAILED=reload'; " + restore + "; exit 8; fi; printf '%s\\n' 'DVA_RELOADED'");
    }

    /// <summary>The root step after a failed health check: restore the backup (or remove a newly created file), validate, reload.</summary>
    public static string RollbackCommand(string path, long timestamp, ConfigServiceRule? rule, bool create = false) =>
        create
            ? "set -u; p=" + Quote(path) + "; "
              + "if ! rm -f -- \"$p\"; then printf '%s\\n' 'DVA_ROLLBACK_FAILED=restore'; exit 9; fi; printf '%s\\n' 'DVA_RESTORED'; "
              + (string.IsNullOrWhiteSpace(rule?.Reload) ? "printf '%s\\n' 'DVA_RELOAD_SKIPPED'"
                  : "if ! " + Group(rule!.Reload) + " 2>&1; then printf '%s\\n' 'DVA_ROLLBACK_FAILED=reload'; exit 11; fi; printf '%s\\n' 'DVA_RELOADED'")
            : "set -u; p=" + Quote(path) + "; d=$(dirname -- \"$p\"); f=$(basename -- \"$p\"); b=\"$d/.$f.bak-" + timestamp.ToString(CultureInfo.InvariantCulture) + "\"; "
              + "if ! cp -p -- \"$b\" \"$p\"; then printf '%s\\n' 'DVA_ROLLBACK_FAILED=restore'; exit 9; fi; printf '%s\\n' 'DVA_RESTORED'; "
              + (string.IsNullOrWhiteSpace(rule?.Validate) ? string.Empty
                  : "if ! " + Group(rule!.Validate) + " 2>&1; then printf '%s\\n' 'DVA_ROLLBACK_FAILED=validate'; exit 10; fi; ")
              + (string.IsNullOrWhiteSpace(rule?.Reload) ? "printf '%s\\n' 'DVA_RELOAD_SKIPPED'"
                  : "if ! " + Group(rule!.Reload) + " 2>&1; then printf '%s\\n' 'DVA_ROLLBACK_FAILED=reload'; exit 11; fi; printf '%s\\n' 'DVA_RELOADED'");

    /// <summary>Read-only live hash check used after a lost connection during apply (T7.R28).</summary>
    public static string VerifyHashCommand(string path) =>
        "set -u; p=" + Quote(path) + "; [ -f \"$p\" ] && sha256sum < \"$p\" | cut -d ' ' -f 1";

    private static string StageCommand(string directory, string content) =>
        "umask 077; d=" + Quote(directory) + "; mkdir -m 700 -- \"$d\" && printf '%s' " + Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(content)))
        + " | base64 -d > \"$d/content\" && sha256sum < \"$d/content\" | cut -d ' ' -f 1";

    public async Task<ActionServerResult> ExecuteAsync(ActionRecord action, ToolCommand plan, Server server, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<EditConfigArgs>(action.ArgumentsJson, JsonOptions)
            ?? throw new InvalidOperationException("The edit_config arguments are missing.");
        var log = new StringBuilder();
        var endpoint = $"{server.Host}:{server.Port}";
        ActionServerResult Done(int? exit, string? error) => new(server.Id, server.Name, plan.Command, exit,
            ChatContentRedactor.Redact(log.ToString()), string.Empty, 0, error, endpoint, plan.UseSudo, server.HasSudoPassword);

        if (!args.IsPrepared || args.Content is null) return Done(null, "The edit was not prepared (no base hash); nothing was changed.");
        if (ConfigEditPolicy.Check(args.Path, options.AllowedPaths) is { } refused) return Done(null, refused + " Nothing was changed.");
        var rule = options.RuleFor(args.Path);
        if (rule is null || string.IsNullOrWhiteSpace(rule.Validate))
            return Done(null, $"No validation rule matches '{args.Path}'. Nothing was changed.");
        var timestamp = Timestamp(action);
        var backupPath = BackupPath(args.Path, timestamp);
        var newHash = Sha256(args.Content);
        var directory = StagingDirectory(action.Id);
        var applyUncertain = false;
        try
        {
            // 1. Stage the new content as the SSH user, in a directory only that user can read.
            var staged = await sshRunner.RunAsync(server.Id, StageCommand(directory, args.Content), new SshRunOptions(TimeSpan.FromSeconds(30)), cancellationToken);
            if (staged.ExitCode != 0 || staged.StandardOutput.Trim() != newHash)
            {
                log.AppendLine("Staging the new content failed; the file was not touched.");
                return Done(staged.ExitCode ?? 1, "Staging failed: nothing was changed.");
            }

            log.AppendLine($"Staged the new content in {directory}.");

            // 2. As root: check, back up, write, validate, reload (restores itself on a failed validation or reload).
            RemoteCommandResult applied;
            try
            {
                applied = await sshRunner.RunAsync(server.Id, plan.Command, new SshRunOptions(TimeSpan.FromMinutes(2), UseSudo: true), cancellationToken);
            }
            catch (OperationCanceledException exception) when (exception is SshShutdownException) { throw; }
            catch (Exception exception)
            {
                applyUncertain = true;
                log.AppendLine($"Connection lost or interrupted during apply ({exception.GetType().Name}). Verifying live file state…");
                var recovery = await VerifyAfterLostApplyAsync(server, args.Path, args.BaseSha256!, newHash, timestamp, backupPath, rule, log);
                return Done(null, recovery);
            }

            var output = applied.StandardOutput + applied.StandardError;
            log.AppendLine(Clip(output));
            if (applied.ExitCode is null)
            {
                applyUncertain = true;
                log.AppendLine("Apply returned no exit status. Verifying live file state…");
                var recovery = await VerifyAfterLostApplyAsync(server, args.Path, args.BaseSha256!, newHash, timestamp, backupPath, rule, log);
                return Done(null, recovery);
            }

            if (applied.ExitCode != 0)
            {
                var step = Marker().Match(output) is { Success: true } marker ? marker.Groups[1].Value : $"exit {applied.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
                var reason = step switch
                {
                    "missing" => "the file does not exist",
                    "exists" => "the file already exists",
                    "changed" => "the file changed since the edit was proposed",
                    "backup" => "the backup could not be written",
                    "write" => "the new content could not be written",
                    "symlink" => "the path is a symbolic link or lies behind one",
                    "staged_symlink" => "the staged content path was a symbolic link",
                    "staged_hash" => "the staged content hash did not match the approved edit",
                    "validate" => $"validation ({rule.Validate}) failed",
                    "reload" => $"the reload ({rule.Service}) failed",
                    _ => step
                };
                var restored = output.Contains("DVA_RESTORED", StringComparison.Ordinal) ? " The backup was restored" + (output.Contains("DVA_RELOADED_AFTER_RESTORE", StringComparison.Ordinal) ? " and the service reloaded." : ".")
                    : output.Contains("DVA_RESTORE_FAILED", StringComparison.Ordinal) ? $" The backup could NOT be restored: restore {args.Path} from its {backupPath} copy by hand."
                    : string.Empty;
                return Done(applied.ExitCode, $"Edit failed: {reason}.{restored}");
            }

            // 3. Health check; 4. roll back when it fails. A check that could not be completed (timeout, lost connection,
            // cancellation) counts as failed: the new file is live and nobody has seen it work.
            (bool Healthy, string Report, string? Failure) health;
            try
            {
                health = await CheckHealthAsync(server, args.Path, cancellationToken);
            }
            catch (Exception exception)
            {
                health = (false, $"Health check could not be completed ({exception.GetType().Name}).", $"the health check could not be completed ({exception.GetType().Name})");
            }

            log.AppendLine(health.Report);
            if (health.Healthy) return Done(0, null);

            RemoteCommandResult rollback;
            try
            {
                // Not cancellable: the new file is live, and the rollback is what makes the server safe again (its own timeout bounds it).
                rollback = await sshRunner.RunAsync(server.Id, RollbackCommand(args.Path, timestamp, rule, args.Create),
                    new SshRunOptions(TimeSpan.FromMinutes(2), UseSudo: true), CancellationToken.None);
            }
            catch (Exception exception)
            {
                log.AppendLine($"The rollback could not be run ({exception.GetType().Name}).");
                return Done(null, $"Health check failed after the reload ({health.Failure}) and the rollback could not be run; restore {args.Path} from its {backupPath} copy by hand.");
            }

            log.AppendLine("Rolled back:").AppendLine(Clip(rollback.StandardOutput + rollback.StandardError));
            return Done(rollback.ExitCode == 0 ? 1 : rollback.ExitCode,
                rollback.ExitCode == 0
                    ? $"Health check failed after the reload ({health.Failure}); the backup was restored and {rule.Service} reloaded."
                    : $"Health check failed after the reload ({health.Failure}) and the rollback failed too; restore {args.Path} from its {backupPath} copy by hand.");
        }
        finally
        {
            // When apply's remote state is uncertain (connection dropped mid-step), do not delete the staging dir under a possibly still-running root apply.
            if (!applyUncertain)
            {
                try { await sshRunner.RunAsync(server.Id, "rm -rf -- " + Quote(directory), new SshRunOptions(TimeSpan.FromSeconds(15)), CancellationToken.None); }
                catch (Exception) { /* best effort; the directory is private to the SSH user */ }
            }
        }
    }

    private async Task<string> VerifyAfterLostApplyAsync(
        Server server, string path, string baseHash, string newHash, long timestamp, string backupPath, ConfigServiceRule rule, StringBuilder log)
    {
        try
        {
            var verify = await sshRunner.RunAsync(server.Id, VerifyHashCommand(path), new SshRunOptions(TimeSpan.FromSeconds(20), UseSudo: server.CanUseSudo), CancellationToken.None);
            var liveHash = verify.StandardOutput.Trim();
            if (verify.ExitCode == 0 && string.Equals(liveHash, baseHash, StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine("Verified: the live file still matches the original hash.");
                return $"Apply outcome unknown (connection lost), but verified {path} on {server.Name} is still unchanged.";
            }

            if (verify.ExitCode == 0 && string.Equals(liveHash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine("Verified: the new content is live on disk; running validation…");
                var check = await sshRunner.RunAsync(server.Id, Group(rule.Validate), new SshRunOptions(TimeSpan.FromSeconds(30), UseSudo: true), CancellationToken.None);
                if (check.ExitCode == 0)
                {
                    log.AppendLine("Validation passed after reconnect.");
                    return $"Apply outcome unknown (connection lost after write) — check {server.Name}: the new {path} is on disk and validation ({rule.Validate}) passed, but reload/health check status is unverified.";
                }

                log.AppendLine($"Validation failed after reconnect: {Clip(check.StandardOutput + check.StandardError)}");
                var rollback = await sshRunner.RunAsync(server.Id, RollbackCommand(path, timestamp, rule), new SshRunOptions(TimeSpan.FromMinutes(2), UseSudo: true), CancellationToken.None);
                log.AppendLine("Rolled back after reconnect:").AppendLine(Clip(rollback.StandardOutput + rollback.StandardError));
                return rollback.ExitCode == 0
                    ? $"Apply outcome unknown (connection lost after write): validation failed and the backup was restored — check {server.Name}."
                    : $"Apply outcome unknown (connection lost after write): validation failed and automatic rollback failed too — check {server.Name} and restore {path} from {backupPath} by hand.";
            }
        }
        catch (Exception exception)
        {
            log.AppendLine($"Verification could not complete ({exception.GetType().Name}).");
        }

        return $"Apply outcome unknown (connection lost and live state could not be verified) — check {path} and backup {backupPath} on {server.Name} by hand.";
    }

    private async Task<(bool Healthy, string Report, string? Failure)> CheckHealthAsync(Server server, string path, CancellationToken cancellationToken)
    {
        var report = new StringBuilder("Health check:\n");
        var failures = new List<string>();
        var ran = 0;

        // The server's own configured health checks.
        foreach (var check in (await healthChecks.ListAsync(server.Id, cancellationToken)).Where(item => item.IsEnabled))
        {
            ran++;
            var result = await healthProbe.ProbeAsync(check.Type, check.Target, cancellationToken);
            report.AppendLine($"  {check.Name} ({check.Target}): {(result.IsHealthy ? "ok" : "failed")}{(result.StatusCode is { } code ? $" · {code}" : string.Empty)}");
            if (!result.IsHealthy) failures.Add($"{check.Name} {(result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? result.Error)}");
        }

        // The domains this file serves (nginx), asked through the server's own listener: 5xx or no answer is a failure.
        var targets = await DomainTargetsAsync(server.Id, path, cancellationToken);
        if (targets.Count > 0)
        {
            var command = "sleep 1; command -v curl >/dev/null 2>&1 || { printf '%s\\n' 'DVA_HEALTH_NO_CURL'; exit 0; }; for h in "
                + string.Join(' ', targets.Select(item => Quote($"{item.Domain}:{item.Port}")))
                + "; do code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -H \"Host: ${h%:*}\" \"http://127.0.0.1:${h##*:}/\" || true); "
                + "printf 'DVA_HEALTH %s %s\\n' \"$h\" \"$code\"; done";
            var answer = await sshRunner.RunAsync(server.Id, command, new SshRunOptions(TimeSpan.FromSeconds(60)), cancellationToken);
            if (answer.StandardOutput.Contains("DVA_HEALTH_NO_CURL", StringComparison.Ordinal))
                report.AppendLine("  curl is not installed on the server: the domains could not be checked.");
            foreach (Match match in HealthLine().Matches(answer.StandardOutput))
            {
                ran++;
                var code = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var healthy = code is > 0 and < 500;
                report.AppendLine($"  http://{match.Groups[1].Value}/ via 127.0.0.1: {(code == 0 ? "no answer" : code.ToString(CultureInfo.InvariantCulture))}{(healthy ? " ok" : " failed")}");
                if (!healthy) failures.Add($"{match.Groups[1].Value} answered {(code == 0 ? "nothing" : code.ToString(CultureInfo.InvariantCulture))}");
            }
        }

        if (ran == 0) report.AppendLine("  Health check unverified: no configured checks or domains could be probed (validation and reload succeeded).");
        return (failures.Count == 0, report.ToString().TrimEnd(), failures.Count == 0 ? null : string.Join("; ", failures));
    }

    // Domains whose server blocks live in this file (all of nginx's domains for a file it includes otherwise), with the port
    // the domain is served on.
    private async Task<IReadOnlyList<(string Domain, int Port)>> DomainTargetsAsync(Guid serverId, string path, CancellationToken cancellationToken)
    {
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        var graph = latest is null ? null : await graphs.GetEntitiesAsync(latest.Id, cancellationToken);
        if (graph is null) return [];
        var blocks = graph.Nodes.Where(node => node.ServerId == serverId && node.Key.Contains(":nginx-server:", StringComparison.Ordinal)).ToArray();
        var inFile = blocks.Where(node => node.AttributesJson.Contains($"\"FilePath\":{JsonSerializer.Serialize(path)}", StringComparison.Ordinal)).ToArray();
        var chosen = (inFile.Length > 0 ? inFile : path.StartsWith("/etc/nginx/", StringComparison.Ordinal) ? blocks : []).Select(node => node.Id).ToHashSet();
        var domains = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Domain).ToDictionary(node => node.Id, node => node.Name);
        return graph.Edges.Where(edge => chosen.Contains(edge.TargetNodeId) && domains.ContainsKey(edge.SourceNodeId))
            .Select(edge => (Domain: domains[edge.SourceNodeId], Port: edge.Port ?? 80))
            .Where(item => DomainName().IsMatch(item.Domain) && item.Port is > 0 and < 65536)
            .Distinct().OrderBy(item => item.Domain, StringComparer.Ordinal).Take(20).ToArray();
    }

    private static string Clip(string text) => text.Length > 8000 ? text[..8000] + "…" : text.TrimEnd();

    private static string Group(string command) => "{ " + command + "; }";

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    [GeneratedRegex(@"DVA_EDIT_FAILED=(\w+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^DVA_SYMLINK=(\S*)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Symlink();

    [GeneratedRegex(@"^DVA_HEALTH (\S+) (\d{3})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HealthLine();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DomainName();
}
