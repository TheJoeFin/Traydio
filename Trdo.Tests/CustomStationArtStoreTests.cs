using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;
using Trdo.Services;

namespace Trdo.Tests;

[TestClass]
public class CustomStationArtStoreTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    private string _folder = string.Empty;
    private CustomStationArtStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        // A non-ASCII name with a space: both end up percent-encoded in the file URL.
        _folder = Path.Combine(Path.GetTempPath(), $"trdo art ∞ {Guid.NewGuid():N}");
        _store = new CustomStationArtStore(Path.Combine(_folder, "station-art"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [TestMethod]
    public void ExtensionFor_RecognisesImagesAndRejectsOtherFiles()
    {
        Assert.AreEqual(".png", CustomStationArtStore.ExtensionFor(Png));
        Assert.AreEqual(".jpg", CustomStationArtStore.ExtensionFor([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.AreEqual(".ico", CustomStationArtStore.ExtensionFor([0, 0, 1, 0, 1, 0]));
        Assert.IsNull(CustomStationArtStore.ExtensionFor("<html></html>"u8));
        Assert.IsNull(CustomStationArtStore.ExtensionFor([]));
    }

    [TestMethod]
    public async Task SaveAsync_WritesEachImageToItsOwnFolder()
    {
        string first = await _store.SaveAsync(Png);
        string second = await _store.SaveAsync(Png);

        string firstPath = new Uri(first).LocalPath;
        string secondPath = new Uri(second).LocalPath;
        CollectionAssert.AreEqual(Png, File.ReadAllBytes(firstPath));
        Assert.AreNotEqual(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(secondPath));
        Assert.IsTrue(_store.Owns(first));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsWhatIsNotAnImage()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _store.SaveAsync([1, 2, 3]));
    }

    [TestMethod]
    public async Task DeleteIfUnused_RemovesTheImageAndItsFolder()
    {
        string url = await _store.SaveAsync(Png);
        string path = new Uri(url).LocalPath;

        Assert.IsTrue(_store.DeleteIfUnused(url, [null, "http://example.com/logo.png"]));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [TestMethod]
    public async Task DeleteIfUnused_KeepsAnImageAnotherStationUses()
    {
        string url = await _store.SaveAsync(Png);
        string path = new Uri(url).LocalPath;

        // The same file written as a plain path counts as in use too.
        Assert.IsFalse(_store.DeleteIfUnused(url, [path]));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void DeleteIfUnused_NeverTouchesTheUsersOwnFiles()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "mine.png");
        File.WriteAllBytes(path, Png);
        string url = new Uri(path).AbsoluteUri;

        Assert.IsFalse(_store.Owns(url));
        Assert.IsFalse(_store.DeleteIfUnused(url, []));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void Owns_IsFalseForOtherUrls()
    {
        Assert.IsFalse(_store.Owns(null));
        Assert.IsFalse(_store.Owns(""));
        Assert.IsFalse(_store.Owns("https://example.com/logo.png"));
        Assert.IsFalse(_store.Owns("not a url"));
    }
}
