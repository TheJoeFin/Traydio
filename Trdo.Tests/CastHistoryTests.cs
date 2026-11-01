using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Trdo.Services.Playback;

namespace Trdo.Tests;

[TestClass]
public sealed class CastHistoryTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    private static CastHistoryDevice Renderer(string name) => new()
    {
        Name = name,
        Kind = "Chromecast",
        RendererType = "chromecast",
    };

    private static CastHistoryDevice Sonos(string uuid, string name = "Kitchen") => new()
    {
        Protocol = CastProtocol.Sonos,
        Uuid = uuid,
        Name = name,
        Kind = "Sonos",
        Address = "192.168.1.20",
        Port = 1400,
    };

    [TestMethod]
    public void NetworkKey_PreservesSsidCaseAndSeparatesSecurityAndInstallations()
    {
        string original = CastHistoryPolicy.NetworkKey("Home", "Wpa2", "Ccmp", "salt");
        Assert.AreEqual(original, CastHistoryPolicy.NetworkKey("Home", "Wpa2", "Ccmp", "salt"));
        Assert.AreNotEqual(original, CastHistoryPolicy.NetworkKey("home", "Wpa2", "Ccmp", "salt"));
        Assert.AreNotEqual(original, CastHistoryPolicy.NetworkKey("Home", "Open", "None", "salt"));
        Assert.AreNotEqual(original, CastHistoryPolicy.NetworkKey("Home", "Wpa2", "Ccmp", "another"));
    }

    [TestMethod]
    public void Confirmation_RejectsUnknownChangedAndReconnectedNetworks()
    {
        CastAttempt attempt = new("home", 3, Renderer("Living Room"));
        Assert.IsTrue(CastHistoryPolicy.CanRemember(attempt, "home", 3));
        Assert.IsFalse(CastHistoryPolicy.CanRemember(attempt, null, 3));
        Assert.IsFalse(CastHistoryPolicy.CanRemember(attempt, "office", 3));
        Assert.IsFalse(CastHistoryPolicy.CanRemember(attempt, "home", 4));
        Assert.IsFalse(CastHistoryPolicy.CanRemember(attempt, "home", 3, isContextCurrent: false));
    }

    [TestMethod]
    public void SuccessfulUse_AndHidePreferenceRemainIsolatedAcrossRestart()
    {
        CastHistoryDocument document = CastHistoryPolicy.CreateDocument();
        Assert.IsFalse(CastHistoryPolicy.ShouldShowShortcut(CastHistoryPolicy.FindNetwork(document, "home")));
        CastHistoryPolicy.RecordSuccess(document, new CastAttempt("home", 1, Sonos("RINCON_HOME")), Earlier);
        CastHistoryPolicy.RecordSuccess(document, new CastAttempt("office", 2, Renderer("Office")), Earlier);
        CastHistoryPolicy.FindNetwork(document, "home")!.ShortcutHidden = true;

        CastHistoryDocument restored = CastHistoryStorageFormat.Parse(CastHistoryStorageFormat.Serialize(document));
        NetworkCastHistory home = CastHistoryPolicy.FindNetwork(restored, "home")!;
        NetworkCastHistory office = CastHistoryPolicy.FindNetwork(restored, "office")!;
        Assert.IsFalse(CastHistoryPolicy.ShouldShowShortcut(home));
        Assert.IsTrue(CastHistoryPolicy.ShouldShowShortcut(office));
        Assert.AreEqual("RINCON_HOME", home.Devices[0].Uuid);
        Assert.AreEqual("192.168.1.20", home.Devices[0].Address);
        Assert.AreEqual(1400, home.Devices[0].Port);
        Assert.AreEqual(Earlier, home.Devices[0].LastUsedUtc);
        Assert.AreEqual(document.Salt, restored.Salt);

        CastHistoryPolicy.RecordSuccess(restored, new CastAttempt("home", 4, Sonos("RINCON_HOME")), Earlier.AddDays(1));
        Assert.IsFalse(CastHistoryPolicy.ShouldShowShortcut(home), "Casting again must not undo an explicit hide.");
        home.ShortcutHidden = false;
        Assert.IsTrue(CastHistoryPolicy.ShouldShowShortcut(home));
    }

    [TestMethod]
    public void SuccessfulUse_UpdatesSonosEndpointAndNameWithoutDuplicatingItsUuid()
    {
        CastHistoryDocument document = CastHistoryPolicy.CreateDocument();
        CastHistoryPolicy.RecordSuccess(document, new CastAttempt("home", 1, Sonos("RINCON_A")), Earlier);
        CastHistoryDevice updated = Sonos("rincon_a", "Kitchen + Dining") with { Address = "192.168.1.30" };
        CastHistoryPolicy.RecordSuccess(document, new CastAttempt("home", 1, updated), Earlier.AddHours(1));

        List<CastHistoryDevice> devices = CastHistoryPolicy.FindNetwork(document, "home")!.Devices;
        Assert.HasCount(1, devices);
        Assert.AreEqual("Kitchen + Dining", devices[0].Name);
        Assert.AreEqual("192.168.1.30", devices[0].Address);
    }

    [TestMethod]
    public void Merge_PromotesRecentDevicesAndKeepsMissingHistory()
    {
        CastHistoryDevice older = Renderer("Bedroom") with { LastUsedUtc = Earlier };
        CastHistoryDevice recent = Sonos("RINCON_A") with { LastUsedUtc = Earlier.AddHours(1) };
        List<CastHistoryRow> rows = CastHistoryPolicy.Merge([older, recent],
            [Renderer("Attic"), Sonos("rincon_a", "Kitchen + Dining")]);

        Assert.HasCount(3, rows);
        Assert.AreEqual("Kitchen + Dining", rows[0].Device.Name);
        Assert.IsTrue(rows[0].IsRemembered && rows[0].IsAvailable);
        Assert.AreEqual(recent.LastUsedUtc, rows[0].Device.LastUsedUtc);
        Assert.AreEqual("Bedroom", rows[1].Device.Name);
        Assert.IsTrue(rows[1].IsRemembered);
        Assert.IsFalse(rows[1].IsAvailable);
        Assert.AreEqual("Attic", rows[2].Device.Name);
        Assert.IsFalse(rows[2].IsRemembered);
    }

    [TestMethod]
    public void Merge_DeduplicatesRendererReannouncementsButNotDifferentProtocols()
    {
        List<CastHistoryRow> rows = CastHistoryPolicy.Merge([Renderer("Kitchen")],
            [Renderer(" kitchen "), Renderer("KITCHEN"), Sonos("RINCON_A")]);
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows[0].IsRemembered && rows[0].IsAvailable);
        Assert.AreEqual(CastProtocol.Sonos, rows[1].Device.Protocol);
    }

    [TestMethod]
    public void Merge_DoesNotSaveDiscoveredDevicesOrDeleteRememberedOnes()
    {
        CastHistoryDocument document = CastHistoryPolicy.CreateDocument();
        CastHistoryPolicy.RecordSuccess(document, new CastAttempt("home", 1, Renderer("Bedroom")), Earlier);
        NetworkCastHistory network = CastHistoryPolicy.FindNetwork(document, "home")!;
        _ = CastHistoryPolicy.Merge(network.Devices, [Renderer("New speaker")]);
        _ = CastHistoryPolicy.Merge(network.Devices, []);
        Assert.HasCount(1, network.Devices);
        Assert.AreEqual("Bedroom", network.Devices[0].Name);
    }

    [TestMethod]
    public void Parse_RejectsMalformedNullAndFutureDocuments()
    {
        Assert.Throws<JsonException>(() => CastHistoryStorageFormat.Parse("{broken"));
        Assert.Throws<FormatException>(() => CastHistoryStorageFormat.Parse("null"));
        Assert.Throws<FormatException>(() => CastHistoryStorageFormat.Parse("""{"Salt":"salt","Networks":null}"""));
        Assert.Throws<NotSupportedException>(() => CastHistoryStorageFormat.Parse("""{"Version":2,"Salt":"salt"}"""));
    }

    [TestMethod]
    public void Serialize_RejectsAmbiguousDuplicatesAndInvalidSonosEndpoints()
    {
        CastHistoryDocument document = CastHistoryPolicy.CreateDocument();
        document.Networks.Add(new NetworkCastHistory
        {
            NetworkKey = "home",
            Devices = [Renderer("Kitchen"), Renderer("kitchen")],
        });
        Assert.Throws<FormatException>(() => CastHistoryStorageFormat.Serialize(document));
        document.Networks[0].Devices = [Sonos("RINCON_A") with { Address = "127.0.0.1" }];
        Assert.Throws<FormatException>(() => CastHistoryStorageFormat.Serialize(document));
    }

    [TestMethod]
    public async Task Storage_PersistsUpdatesAndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trdo-cast-{Guid.NewGuid():N}.json");
        try
        {
            CastHistoryStore store = new(path);
            CastHistoryDocument document = await store.LoadAsync();
            CastHistoryPolicy.RecordSuccess(document, new CastAttempt("home", 1, Sonos("RINCON_A")), Earlier);
            await store.SaveAsync(CastHistoryStorageFormat.Serialize(document));
            document.Networks[0].ShortcutHidden = true;
            await store.SaveAsync(CastHistoryStorageFormat.Serialize(document));

            CastHistoryDocument restored = await new CastHistoryStore(path).LoadAsync();
            Assert.IsTrue(restored.Networks[0].ShortcutHidden);
            Assert.AreEqual("RINCON_A", restored.Networks[0].Devices[0].Uuid);
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [TestMethod]
    public async Task Storage_PreservesAnUnreadableFileInsteadOfOverwritingHistory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trdo-cast-{Guid.NewGuid():N}.json");
        const string original = """{"Version":2,"Salt":"future"}""";
        try
        {
            await File.WriteAllTextAsync(path, original);
            CastHistoryStore store = new(path);
            await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync());
            await Assert.ThrowsAsync<IOException>(() =>
                store.SaveAsync(CastHistoryStorageFormat.Serialize(CastHistoryPolicy.CreateDocument())));
            Assert.AreEqual(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
