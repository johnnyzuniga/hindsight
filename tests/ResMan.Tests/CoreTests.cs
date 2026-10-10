using ResMan.Core;

namespace ResMan.Tests;

public class RuleValidatorTests
{
    [Theory]
    [InlineData("chrome", "set_priority", "below_normal", "gaming", true)]
    [InlineData("onedrive", "efficiency_mode", "on", "any", true)]
    [InlineData("discord", "cpu_cap", "30", "focus", true)]
    [InlineData("discord", "cpu_cap", "99", "focus", false)]
    [InlineData("chrome", "set_priority", "above_normal", "any", false)]
    [InlineData("chrome", "format_disk", "now", "any", false)]
    [InlineData("chrome", "set_priority", "idle", "weekends", false)]
    [InlineData("chrome; del *", "set_priority", "idle", "any", false)]
    public void Validates_actions_values_and_profiles(string proc, string action, string value, string profile, bool ok) =>
        Assert.Equal(ok, RuleValidator.Validate(new RuleProposal(proc, action, value, profile, "test")).Ok);

    [Theory]
    [InlineData("lsass")]
    [InlineData("csrss.exe")]
    [InlineData("MsMpEng")]
    [InlineData("EasyAntiCheat")]
    [InlineData("svchost.exe")]
    [InlineData("ResMan.Service")]
    public void Never_allows_protected_processes(string proc)
    {
        var r = RuleValidator.Validate(new RuleProposal(proc, RuleActions.SetPriority, "idle", Profiles.Any, "test"));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("protected"));
    }
}

public class EventClassifierTests
{
    [Theory]
    [InlineData("Application", "Application Hang", 1002, HealthKind.AppHang)]
    [InlineData("Application", "Application Error", 1000, HealthKind.AppCrash)]
    [InlineData("System", "Microsoft-Windows-Kernel-Power", 41, HealthKind.UnexpectedShutdown)]
    [InlineData("System", "Display", 4101, HealthKind.DisplayDriverReset)]
    [InlineData("System", "disk", 7, HealthKind.DiskError)]
    [InlineData("System", "Microsoft-Windows-DistributedCOM", 10016, HealthKind.Noise)]
    [InlineData("System", "Display", 9999, HealthKind.Other)]
    public void Classifies_known_sources(string log, string provider, int id, string expected) =>
        Assert.Equal(expected, EventClassifier.Classify(log, provider, id));

    [Fact]
    public void Builds_xpath_per_channel_with_lookback()
    {
        var x = EventClassifier.XPathFor("System", 1000);
        Assert.Contains("Provider[@Name='Display'] and (EventID=4101)", x);
        Assert.Contains("timediff(@SystemTime) <= 1000", x);
        Assert.DoesNotContain("Application Hang", x);
    }

    [Fact]
    public void Maps_change_sources()
    {
        Assert.Equal(ChangeKind.WindowsUpdate, EventClassifier.ChangeKindFor("System", "Microsoft-Windows-WindowsUpdateClient", 19));
        Assert.Equal(ChangeKind.AppInstalled, EventClassifier.ChangeKindFor("Application", "MsiInstaller", 11707));
        Assert.Null(EventClassifier.ChangeKindFor("System", "Display", 4101));
    }

    [Fact]
    public void Routine_antivirus_updates_are_marked_routine()
    {
        Assert.True(ChangeKind.IsRoutine(new SystemChange(0, DateTime.UtcNow, ChangeKind.WindowsUpdate,
            "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", null, "t", "k")));
        Assert.False(ChangeKind.IsRoutine(new SystemChange(0, DateTime.UtcNow, ChangeKind.DriverInstalled, "NVIDIA driver", null, "t", "k")));
    }
}
