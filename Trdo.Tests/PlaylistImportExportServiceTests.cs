using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

[TestClass]
public class PlaylistImportExportServiceTests
{
    private static RadioStation Station(string name, string url) =>
        new() { Name = name, StreamUrl = url };

    [TestMethod]
    [DataRow("a.pls", true)]
    [DataRow("a.PLS", true)]
    [DataRow("a.m3u", true)]
    [DataRow("a.m3u8", true)]
    [DataRow("a.txt", false)]
    [DataRow("a", false)]
    public void IsSupportedFile_MatchesPlaylistExtensions(string path, bool expected)
    {
        Assert.AreEqual(expected, PlaylistImportExportService.IsSupportedFile(path));
    }

    [TestMethod]
    public void IsDuplicate_MatchesIdenticalStation()
    {
        RadioStation[] existing = [Station("A", "http://a/live")];

        Assert.IsTrue(PlaylistImportExportService.IsDuplicate(existing, Station("A", "http://a/live")));
    }

    [TestMethod]
    public void IsDuplicate_RenamedOrRepointedStationIsNew()
    {
        RadioStation[] existing = [Station("A", "http://a/live")];

        Assert.IsFalse(PlaylistImportExportService.IsDuplicate(existing, Station("B", "http://a/live")));
        Assert.IsFalse(PlaylistImportExportService.IsDuplicate(existing, Station("A", "http://a/other")));
    }

    [TestMethod]
    public void Export_Pls_RoundTripsThroughImport()
    {
        RadioStation[] stations = [Station("One", "http://one/live"), Station("Two", "http://two/live")];

        string pls = PlaylistImportExportService.Export(stations, asPls: true);
        List<RadioStation> back = PlaylistImportExportService.ImportFromFile("x.pls", pls);

        Assert.AreEqual(2, back.Count);
        Assert.AreEqual("Two", back[1].Name);
        Assert.AreEqual("http://two/live", back[1].StreamUrl);
    }

    [TestMethod]
    public void Export_M3u_RoundTripsThroughImport()
    {
        RadioStation[] stations = [Station("One", "http://one/live")];

        string m3u = PlaylistImportExportService.Export(stations, asPls: false);
        List<RadioStation> back = PlaylistImportExportService.ImportFromFile("x.m3u", m3u);

        Assert.AreEqual(1, back.Count);
        Assert.AreEqual("One", back[0].Name);
    }
}
