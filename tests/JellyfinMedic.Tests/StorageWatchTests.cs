using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class StorageWatchTests
{
    [Fact]
    public void Two_drives_holding_the_same_library_keep_different_names()
    {
        var first = new DriveStatus { Key = "/mnt/disk1", MountPoint = "/mnt/disk1", Holds = "Films" };
        var second = new DriveStatus { Key = "/mnt/disk2", MountPoint = "/mnt/disk2", Holds = "Films" };

        Assert.NotEqual(first.StableName, second.StableName);
    }

    [Fact]
    public void A_drive_without_a_mount_point_is_named_by_what_it_holds()
    {
        // Without a mount point, Key is built from the size and free space, which change from day to day.
        var drive = new DriveStatus { Key = "931.5/120.3", Holds = "Films" };

        Assert.Equal("Films", drive.StableName);
    }

    [Fact]
    public void A_drive_found_from_a_folder_carries_its_mount_point()
    {
        var drive = Assert.Single(StorageWatch.Drives(new[] { ("Films", Path.GetTempPath()) }));

        Assert.False(string.IsNullOrEmpty(drive.MountPoint));
        Assert.Equal(drive.MountPoint, drive.StableName);
    }

    [Fact]
    public void Library_folders_bind_mounted_from_one_disk_are_one_drive()
    {
        // Inside a container each -v host/sub:/container/path is its own mount, all of them on the same device.
        var mounts = new Dictionary<string, (string?, string?)>
        {
            ["/data/tvshows/Series"] = ("/data/tvshows", "253:0"),
            ["/data/movies/Films"] = ("/data/movies", "253:0"),
            ["/data/music"] = ("/data/music", "8:17"),
        };

        var drives = StorageWatch.Drives(
            new[] { ("TV", "/data/tvshows/Series"), ("Movies", "/data/movies/Films"), ("Music", "/data/music") },
            _ => true,
            _ => (120.5, 14900.0),
            path => mounts[path]);

        Assert.Equal(2, drives.Count);
        var shared = drives.Single(d => d.Holds == "TV, Movies");
        Assert.Equal("/data/movies", shared.Key);
        Assert.Equal("/data/movies", shared.StableName);
        Assert.Equal("/data/movies/Films", shared.ExamplePath);
        Assert.Equal(new[] { "/data/tvshows" }, shared.OtherMountPoints);
        Assert.Equal("/data/music", drives.Single(d => d.Holds == "Music").Key);
    }

    [Fact]
    public void The_device_of_a_mount_comes_from_mountinfo()
    {
        string[] mountInfo =
        {
            "612 540 0:52 / / rw,relatime master:1 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/A",
            "640 612 253:0 /media/movies /data/movies rw,relatime - ext4 /dev/mapper/ibex-media rw",
            "641 612 253:0 /media/tv\\040shows /data/tv\\040shows rw,relatime - ext4 /dev/mapper/ibex-media rw",
            "642 612 8:17 /old /data/movies rw,relatime - ext4 /dev/sdb1 rw",
        };

        Assert.Equal("253:0", StorageWatch.DeviceOf("/data/tv shows", mountInfo));
        // A later mount on the same point hides the earlier one.
        Assert.Equal("8:17", StorageWatch.DeviceOf("/data/movies", mountInfo));
        Assert.Null(StorageWatch.DeviceOf("/data/music", mountInfo));
    }

    [Fact]
    public void Readings_kept_under_a_second_mount_of_the_same_disk_are_dropped()
    {
        string dir = Path.Combine(Path.GetTempPath(), "medic-disks-" + Guid.NewGuid().ToString("N"));
        string day = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string file = Path.Combine(dir, "JellyfinMedic", "disk_history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""
            [
              { "Key": "/data/movies", "Day": "{{day}}", "FreeGb": 130, "Holds": "Movies", "Path": "/data/movies/Films" },
              { "Key": "/data/tvshows", "Day": "{{day}}", "FreeGb": 130, "Holds": "TV", "Path": "/data/tvshows/Series" },
              { "Key": "/data/music", "Day": "{{day}}", "FreeGb": 40, "Holds": "Music", "Path": "/data/music" }
            ]
            """);
        try
        {
            var drive = new DriveStatus { Key = "/data/movies", MountPoint = "/data/movies", Holds = "TV, Movies", ExamplePath = "/data/movies/Films", FreeGb = 120 };
            drive.OtherMountPoints.Add("/data/tvshows");

            StorageWatch.Record(dir, new List<DriveStatus> { drive });

            var keys = StorageWatch.History(dir).Select(h => h.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(new[] { "/data/movies", "/data/music" }, keys);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
