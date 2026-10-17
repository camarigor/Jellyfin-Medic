using Jellyfin.Plugin.MedicProfiles.Arr;

namespace MedicProfiles.Tests;

public class ProfileAdvisorTests
{
    private static ProfileAdvisor.AppData Data(params ProfileSummary[] profiles) => new()
    {
        Profiles = profiles.ToList(),
        CustomFormats = new List<string> { "x265 (HD)", "DTS" }
    };

    private static ProfileSummary Profile(int id, string name, params string[] allowed) =>
        new() { Id = id, Name = name, Allowed = allowed.ToList(), InUse = 1 };

    private static PlaybackEvidence Evidence(int transcoded, params (string Key, int Count)[] signals)
    {
        var e = new PlaybackEvidence { FromMedic = true, TranscodedFiles = transcoded };
        foreach (var (key, count) in signals)
        {
            e.Signals[key] = count;
        }

        return e;
    }

    private static ProfileAdvisor.AppData WithHevcPenalised()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p"), Profile(2, "UHD", "Bluray-2160p"));
        data.Scores[1] = new Dictionary<string, int> { ["x265 (HD)"] = -10000 };
        data.Scores[2] = new Dictionary<string, int> { ["x265 (HD)"] = -10000 };
        return data;
    }

    [Fact]
    public void An_empty_transcode_log_is_no_proof_that_hevc_plays_fine()
    {
        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, WithHevcPenalised(), Evidence(0, ("libFiles", 200), ("libHevc", 100)));

        Assert.DoesNotContain(advice, s => s.Title.StartsWith("HEVC plays fine", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hevc_tip_needs_transcodes_and_leaves_4K_profiles_out()
    {
        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, WithHevcPenalised(),
            Evidence(ProfileAdvisor.MinTranscodesForTip, ("libFiles", 200), ("libHevc", 100)));

        var tip = Assert.Single(advice, s => s.Title.StartsWith("HEVC plays fine", StringComparison.Ordinal));
        Assert.Equal(new[] { "HD" }, tip.Profiles);
    }

    [Fact]
    public void Dts_advice_skips_profiles_that_only_allow_remux()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p", "Remux-1080p"), Profile(2, "Remux 4K", "Remux-2160p"));

        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("dts", 5)));

        var dts = Assert.Single(advice, s => s.Title.Contains("DTS", StringComparison.Ordinal));
        Assert.Equal(new[] { "HD" }, dts.Profiles);
    }

    [Fact]
    public void A_bitrate_cap_on_small_files_doesnt_say_to_untick_remux()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p", "Remux-1080p"));

        var capped = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("bitrate", 8)));
        var remux = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("bitrate", 8), ("remux", 4)));

        Assert.DoesNotContain(capped, s => s.Title.StartsWith("Remux", StringComparison.Ordinal));
        Assert.Contains(remux, s => s.Title.StartsWith("Remux", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_remux_sized_file_counts_as_a_remux_transcode()
    {
        var signals = new Dictionary<string, int>();
        var reasons = new HashSet<string> { "ContainerBitrateExceedsLimit" };

        ProfileAdvisor.Count(signals, reasons, new ProfileAdvisor.FileFacts { Bitrate = 8_000_000 });
        ProfileAdvisor.Count(signals, reasons, new ProfileAdvisor.FileFacts { Bitrate = 40_000_000 });
        ProfileAdvisor.Count(signals, reasons, null);

        Assert.Equal(3, signals["bitrate"]);
        Assert.Equal(1, signals["remux"]);
    }
}
