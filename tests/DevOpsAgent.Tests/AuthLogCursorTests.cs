using System.Diagnostics;
using DevOpsAgent.Infrastructure.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class AuthLogCursorTests
{
    [Theory]
    [InlineData("+03:30", 210)]
    [InlineData("-05:00", -300)]
    [InlineData("+00:00", 0)]
    public void ParseUtcOffset_ReadsDateOutput(string text, int expectedMinutes) =>
        Assert.Equal(expectedMinutes, AuthLogScanner.ParseUtcOffset(text)!.Value.TotalMinutes);

    [Theory]
    [InlineData("")]
    [InlineData("+3:30")]
    [InlineData("UTC")]
    [InlineData("+25:00")]
    public void ParseUtcOffset_RejectsOtherText(string text) =>
        Assert.Null(AuthLogScanner.ParseUtcOffset(text));

    [Fact]
    public void AuthLogLinesAreReadInTheServersOffsetNotTheAppsOffset()
    {
        // The app runs in UTC; the server's clock is UTC-05:00. 09:00 on the server is 14:00 UTC.
        var now = DateTimeOffset.Parse("2026-11-18T14:05:00+00:00");
        const string log = "Nov 18 09:00:00 srv sshd[1]: Accepted publickey for deploy from 192.0.2.10 port 22 ssh2\n";

        var events = AuthLogParser.Parse(log, now, TimeSpan.FromHours(-5));

        var login = Assert.Single(events);
        Assert.Equal(DateTimeOffset.Parse("2026-11-18T14:00:00+00:00"), login.Timestamp.ToUniversalTime());
    }

    [Fact]
    public void WithoutAServerOffsetTheAppOffsetIsStillUsed()
    {
        var now = DateTimeOffset.Parse("2026-11-18T14:05:00+00:00");
        const string log = "Nov 18 09:00:00 srv sshd[1]: Accepted publickey for deploy from 192.0.2.10 port 22 ssh2\n";

        var login = Assert.Single(AuthLogParser.Parse(log, now));

        Assert.Equal(DateTimeOffset.Parse("2026-11-18T09:00:00+00:00"), login.Timestamp);
    }

    [Fact]
    public void FailedJournalQueryWithoutAuthLogReportsTheErrorMarker()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory("dva-journal-");
        try
        {
            WriteFakeJournal(dir, exitCode: 1);
            // The command reads /var/log/auth.log when it is readable; point it at a path that does not exist.
            var command = AuthLogScanner.BuildCommand(DateTimeOffset.UtcNow.AddMinutes(-5))
                .Replace("/var/log/auth.log", Path.Combine(dir.FullName, "missing-auth.log"), StringComparison.Ordinal);

            var output = RunShell(command, PathWith(dir));

            Assert.Contains(AuthLogScanner.JournalErrorMarker, output);
            Assert.Contains(AuthLogScanner.TimezoneMarker, output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FailedJournalQueryFallsBackToAReadableAuthLogWithoutTheErrorMarker()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory("dva-journal-");
        try
        {
            WriteFakeJournal(dir, exitCode: 1);
            var authLog = Path.Combine(dir.FullName, "auth.log");
            File.WriteAllText(authLog, "Nov 18 09:00:00 srv sshd[1]: Failed password for root from 192.0.2.1 port 22 ssh2\n");
            var command = AuthLogScanner.BuildCommand(DateTimeOffset.UtcNow.AddMinutes(-5))
                .Replace("/var/log/auth.log", authLog, StringComparison.Ordinal);

            var output = RunShell(command, PathWith(dir));

            Assert.DoesNotContain(AuthLogScanner.JournalErrorMarker, output);
            Assert.Contains("Failed password for root from 192.0.2.1", output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SuccessfulJournalQueryDoesNotReportAnError()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Directory.CreateTempSubdirectory("dva-journal-");
        try
        {
            WriteFakeJournal(dir, exitCode: 0);
            var command = AuthLogScanner.BuildCommand(DateTimeOffset.UtcNow.AddMinutes(-5))
                .Replace("/var/log/auth.log", Path.Combine(dir.FullName, "missing-auth.log"), StringComparison.Ordinal);

            var output = RunShell(command, PathWith(dir));

            Assert.DoesNotContain(AuthLogScanner.JournalErrorMarker, output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void WriteFakeJournal(DirectoryInfo dir, int exitCode)
    {
        var fake = Path.Combine(dir.FullName, "journalctl");
        File.WriteAllText(fake, $"#!/bin/sh\necho 'fake journal' >&2\nexit {exitCode}\n");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string PathWith(DirectoryInfo dir) => $"{dir.FullName}:{Environment.GetEnvironmentVariable("PATH")}";

    private static string RunShell(string command, string path)
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment["PATH"] = path;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "The scan command did not finish.");
        return output;
    }
}
