using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class DriveSpaceTests
{
    // Medic reads sizes in GiB, so a 16 TB drive is about 14,901 of them.
    private const double SixteenTb = 14901;

    [Theory]
    [InlineData(140, "Improve")]
    [InlineData(200, null)]
    [InlineData(49, "Problem")]
    [InlineData(50, "Improve")] // the full limit itself is not full
    [InlineData(150, null)] // nor is the nearly full one nearly full
    public void A_big_drive_is_judged_by_gigabytes_not_by_a_share_of_its_size(double freeGb, string? expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.SpaceVerdict(freeGb, SixteenTb, "Films"));
    }

    [Theory]
    [InlineData(8, 931, "Problem")]
    [InlineData(20, 931, "Improve")]
    [InlineData(100, 931, null)]
    [InlineData(4.5, 300, "Problem")]
    [InlineData(5, 300, "Improve")]
    [InlineData(20, 300, "Improve")]
    [InlineData(39, 4000, "Problem")]
    [InlineData(110, 4000, "Improve")]
    [InlineData(130, 4000, null)]
    public void Drives_up_to_about_5_TB_keep_the_same_limits(double freeGb, double totalGb, string? expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.SpaceVerdict(freeGb, totalGb, "Films"));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(12, 16)]
    public void A_drive_holding_only_transcodes_is_never_full(double freeGb, double totalGb)
    {
        Assert.Null(DiagnosticsEngine.SpaceVerdict(freeGb, totalGb, "transcodes"));
    }

    [Fact]
    public void A_drive_sharing_transcodes_with_a_library_is_still_checked()
    {
        Assert.Equal("Problem", DiagnosticsEngine.SpaceVerdict(4, 1000, "transcodes, Films"));
    }

    [Fact]
    public void A_drive_holding_only_transcodes_never_will_be_full()
    {
        // One reading taken during a transcode looks like the drive filling up.
        var drive = new DriveStatus { Key = "/transcode", MountPoint = "/transcode", Holds = "transcodes", FreeGb = 1, TotalGb = 4, GbPerDay = 1 };

        Assert.Empty(DiagnosticsEngine.DriveSpaceFindings(new[] { drive }));
    }

    [Fact]
    public void A_library_drive_filling_up_is_still_reported()
    {
        var drive = new DriveStatus { Key = "/mnt/films", MountPoint = "/mnt/films", Holds = "Films", FreeGb = 300, TotalGb = 931, GbPerDay = 20 };

        var finding = Assert.Single(DiagnosticsEngine.DriveSpaceFindings(new[] { drive }));
        Assert.Equal("The drive for Films will be full in about 2 weeks", finding.Title);
    }

    [Theory]
    [InlineData(0.3, "about 1 day")]
    [InlineData(1, "about 1 day")]
    [InlineData(3, "about 3 days")]
    [InlineData(21, "about 3 weeks")]
    public void Time_to_full_reads_properly(double days, string expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.Weeks(days));
    }

    [Fact]
    public void Two_folders_on_drives_Medic_cant_name_count_as_different_drives()
    {
        Assert.False(DiagnosticsEngine.SameDrive(null, null));
        Assert.False(DiagnosticsEngine.SameDrive("/", null));
    }

    [Fact]
    public void Two_folders_on_one_named_drive_are_the_same_drive()
    {
        Assert.True(DiagnosticsEngine.SameDrive("/", "/"));
        Assert.False(DiagnosticsEngine.SameDrive("/", "/mnt/cache"));
    }

    [Fact]
    public void Without_mount_points_the_cache_on_another_volume_is_another_drive()
    {
        var roots = new Lazy<List<string>>(() => new List<string> { @"C:\", @"D:\" });
        string? cache = StorageWatch.DriveOf(@"D:\Jellyfin\cache", roots, _ => null);
        string? data = StorageWatch.DriveOf(@"C:\ProgramData\Jellyfin\Server", roots, _ => null);

        Assert.Equal(@"D:\", cache);
        Assert.False(DiagnosticsEngine.SameDrive(cache, data));
    }

    [Fact]
    public void A_missing_or_empty_library_folder_is_not_counted_as_its_parents_drive()
    {
        string parent = Path.GetTempPath();
        var drives = StorageWatch.Drives(new[] { ("Films", Path.Combine(parent, "medic-not-mounted")) }, _ => false);

        Assert.Empty(drives);
    }

    [Fact]
    public void Jellyfins_own_folders_are_read_through_their_parent_when_not_made_yet()
    {
        string parent = Path.GetTempPath();
        var drive = Assert.Single(StorageWatch.Drives(new[] { ("transcodes", Path.Combine(parent, "medic-no-transcodes-yet")) }, _ => false));

        Assert.Equal("transcodes", drive.Holds);
    }
}
