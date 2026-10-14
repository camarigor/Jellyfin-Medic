using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class DiskHealthTests : IDisposable
{
    private readonly string _ini = Path.GetTempFileName();

    [Fact]
    public void An_NVMe_drive_at_60_degrees_is_fine_but_a_spinning_disk_at_56_is_hot()
    {
        Write(Disk("disk1", rotational: "1", temp: 56), Disk("cache", rotational: "0", temp: 60));

        var finding = Assert.Single(DiskHealth.CheckFile(_ini));
        Assert.Equal("A disk is running hot", finding.Title);
        Assert.Equal("disk1: 56°C", finding.Current);
    }

    [Fact]
    public void An_NVMe_drive_over_its_own_limit_is_hot()
    {
        Write(Disk("cache", rotational: "0", temp: 72));

        Assert.Equal("cache: 72°C", Assert.Single(DiskHealth.CheckFile(_ini)).Current);
    }

    [Fact]
    public void Errors_are_reported_as_failing_and_heat_on_its_own()
    {
        Write(Disk("disk2", rotational: "1", temp: 58, errors: 3));

        var findings = DiskHealth.CheckFile(_ini);
        Assert.Contains(findings, f => f.Title == "A disk may be failing" && f.Current == "disk2: 3 read/write errors");
        Assert.Contains(findings, f => f.Title == "A disk is running hot" && f.Current == "disk2: 58°C");
    }

    public void Dispose() => File.Delete(_ini);

    private static string Disk(string name, string rotational, int temp, int errors = 0) =>
        $"[\"{name}\"]\nname=\"{name}\"\nrotational=\"{rotational}\"\ntemp=\"{temp}\"\nnumErrors=\"{errors}\"\nstatus=\"DISK_OK\"\n";

    private void Write(params string[] disks) => File.WriteAllText(_ini, string.Concat(disks));
}
