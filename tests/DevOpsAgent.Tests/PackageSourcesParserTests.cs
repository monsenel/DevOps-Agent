using DevOpsAgent.Infrastructure.Parsers;

namespace DevOpsAgent.Tests;

/// <summary>T8.6: package sources and per-package origins from read-only command output.</summary>
public sealed class PackageSourcesParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void AptSources_OneLineAndDeb822_WithSignedByAndDisabledStanzas()
    {
        var sources = PackageSourcesParser.ParseApt(Fixture("apt-sources.txt"));

        Assert.Equal(7, sources.Count); // 3 one-line, 1 docker, 1 pgdg, 2 (deb + deb-src) disabled trixie
        var docker = Assert.Single(sources, item => item.Uri.Contains("docker", StringComparison.Ordinal));
        Assert.Equal(("deb", "jammy", "stable", "/etc/apt/keyrings/docker.gpg", "/etc/apt/sources.list.d/docker.list"),
            (docker.Type, docker.Suite, string.Join(' ', docker.Components), docker.SignedBy, docker.File));
        var updates = Assert.Single(sources, item => item.Suite == "jammy-updates");
        Assert.Equal(["main", "restricted", "universe"], updates.Components);
        Assert.Null(updates.SignedBy);
        Assert.DoesNotContain(sources, item => item.Type == "deb-src" && item.Enabled); // the commented deb-src line is not a source
        var pgdg = Assert.Single(sources, item => item.Suite == "jammy-pgdg");
        Assert.Equal("(inline key)", pgdg.SignedBy); // the key block itself is not shown
        var trixie = sources.Where(item => item.Suite == "trixie").ToArray();
        Assert.Equal(["deb", "deb-src"], trixie.Select(item => item.Type));
        Assert.All(trixie, item => Assert.False(item.Enabled));
        Assert.Equal("/usr/share/keyrings/debian-archive-keyring.gpg", trixie[0].SignedBy);
    }

    [Fact]
    public void DnfRepolist_IdNameAndBaseUrlOrMirrors()
    {
        var sources = PackageSourcesParser.ParseDnf(Fixture("dnf-repolist-v.txt"));

        Assert.Equal(2, sources.Count);
        Assert.Equal(("appstream", "Rocky Linux 9 - AppStream", "https://mirrors.rockylinux.org/mirrorlist?arch=x86_64&repo=AppStream-9"),
            (sources[0].Suite, sources[0].File, sources[0].Uri));
        Assert.Equal(("docker-ce-stable", "https://download.docker.com/linux/centos/9/x86_64/stable"), (sources[1].Suite, sources[1].Uri));
    }

    [Fact]
    public void AptOrigins_TellDockerFromUbuntu_ForTheCandidateVersion()
    {
        var origins = PackageSourcesParser.ParseAptOrigins(Fixture("apt-cache-policy.txt"));

        Assert.Equal("Docker CE · jammy", origins["docker-ce"]);
        Assert.Equal("Ubuntu · jammy-security", origins["openssl"]); // the candidate's source, not the older jammy one
        Assert.Equal("mirror.example.org · jammy", origins["mystery"]); // no release line: the host
        Assert.Empty(PackageSourcesParser.ParseAptOrigins("no marker here"));
    }

    [Fact]
    public void PolicyCommand_QuotesPackageNames_AndStaysReadOnly()
    {
        var command = PackageSourcesParser.AptPolicyCommand(["docker-ce", "x';rm -rf /;'"]);

        Assert.Contains("apt-cache policy -- 'docker-ce' 'x'\\'';rm -rf /;'\\'''", command, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo", command + PackageSourcesParser.AptSourcesCommand + PackageSourcesParser.DnfSourcesCommand, StringComparison.Ordinal);
        Assert.Contains("dnf -C ", PackageSourcesParser.DnfSourcesCommand, StringComparison.Ordinal);
    }
}
