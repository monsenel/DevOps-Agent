using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Tests;

public sealed class UpdateApplyReportTests
{
    [Fact]
    public void ReportReadsRestartedServicesAndRebootFlagFromApplyOutput()
    {
        const string output = "Reading package lists...\n1 upgraded, 0 newly installed\nRESTARTED_SERVICES_BEGIN\nnginx.service\nssh.service\nRESTARTED_SERVICES_END\nREBOOT_REQUIRED=true\n";

        var report = UpdateApplyReport.Parse("srv-edge", 0, output, null);

        Assert.Equal(new[] { "nginx.service", "ssh.service" }, report.RestartedServices);
        Assert.True(report.RebootRequired);
        Assert.Equal("srv-edge: updated (exit 0). Restarted services: nginx.service, ssh.service. Reboot required: yes.", report.Describe());
    }

    [Fact]
    public void ReportHandlesNoRestartsMissingMarkersAndFailures()
    {
        Assert.Equal("srv-edge: updated (exit 0). Restarted services: none. Reboot required: no.",
            UpdateApplyReport.Parse("srv-edge", 0, "RESTARTED_SERVICES_BEGIN\nRESTARTED_SERVICES_END\nREBOOT_REQUIRED=false\n", null).Describe());
        Assert.Equal("srv-edge: updated (exit 0). Restarted services: none. Reboot required: unknown.",
            UpdateApplyReport.Parse("srv-edge", 0, "no markers", null).Describe());
        Assert.Equal("srv-db: update failed (exit 100).", UpdateApplyReport.Parse("srv-db", 100, string.Empty, null).Describe());
        Assert.Equal("srv-db: update failed (connection refused).", UpdateApplyReport.Parse("srv-db", null, string.Empty, "connection refused").Describe());
    }
}
