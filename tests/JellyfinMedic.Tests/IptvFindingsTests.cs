using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class IptvFindingsTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);

    private static IptvReport Report(DateTime generatedUtc) => new()
    {
        GeneratedUtc = generatedUtc,
        Channels = new ChannelReport { ExtraCopies = 40, DuplicateGroups = 12, Examples = { "BBC One" } },
        Libraries =
        {
            new IptvLibrary { Name = "IPTV Films", Genres = { new IptvGroup { Name = "Bollywood", Items = 900, Watched = 0 } } }
        }
    };

    [Fact]
    public void An_analysis_from_today_gives_its_findings()
    {
        var findings = IptvAnalyzer.Findings(Report(Now.AddHours(-3)), Now);

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void An_analysis_older_than_a_day_gives_no_findings()
    {
        Assert.Empty(IptvAnalyzer.Findings(Report(Now.AddHours(-25)), Now));
    }

    [Fact]
    public void No_analysis_gives_no_findings()
    {
        Assert.Empty(IptvAnalyzer.Findings(null, Now));
    }
}
