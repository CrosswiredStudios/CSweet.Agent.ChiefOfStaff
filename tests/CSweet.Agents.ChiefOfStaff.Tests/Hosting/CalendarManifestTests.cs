using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using Xunit;

namespace CSweet.Agents.ChiefOfStaff.Tests.Hosting;

public sealed class CalendarManifestTests
{
    [Fact]
    public void CalendarPermissionsAndReminderSubscriptionAreRequested()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "csweet-plugin.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "csweet-plugin.json")));
        var required = doc.RootElement.GetProperty("requires").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
        foreach (var capability in CalendarCapabilities.All) Assert.Contains(capability, required);
        Assert.Contains(CalendarEvents.ReminderDue, doc.RootElement.GetProperty("events").GetProperty("subscribes").EnumerateArray().Select(x => x.GetString()));
        var events = doc.RootElement.GetProperty("events").GetProperty("subscribes").EnumerateArray().Select(x => x.GetString()).ToArray();
        // These notifications had no workflow handler. Do not promise durable handling
        // until authenticated intake and persisted follow-through exist.
        Assert.DoesNotContain("com.csweet.communication.message.mentioned.v1", events);
        Assert.DoesNotContain("com.csweet.management.status.reported.v1", events);
        Assert.DoesNotContain("com.csweet.management.resource-need.reported.v1", events);
    }
}
