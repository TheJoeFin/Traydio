using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services.Playback;

namespace Trdo.Tests;

/// <summary>
/// Covers the probe that works out why a stream will not play. The point of this class is
/// to turn "Couldn't play this station" into something the user can act on, so each test
/// pins a real-world failure to the specific conclusion it should produce.
/// </summary>
[TestClass]
public sealed class StreamDiagnosticsTests
{
    private const string StreamUrl = "http://stream.riverwestradio.com:8000/riverwestradio";

    /// <summary>Returns a canned response, or throws a canned exception, for any request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;

        public StubHandler(Func<HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpRequestMessage captured = request;
            LastRequest = captured;
            return Task.FromResult(_factory());
        }

        public HttpRequestMessage? LastRequest { get; private set; }
    }

    private static HttpClient CreateClient(StubHandler handler) => new(handler);

    private static HttpResponseMessage Respond(
        HttpStatusCode status,
        string body,
        string? contentType = null,
        params (string Name, string Value)[] headers)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };

        if (contentType is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static async Task<StreamDiagnosis> ProbeAsync(Func<HttpResponseMessage> factory, string url = StreamUrl)
    {
        using StubHandler handler = new(factory);
        using HttpClient client = CreateClient(handler);
        return await StreamDiagnostics.ProbeAsync(url, client, TimeSpan.FromSeconds(5));
    }

    private static async Task<StreamDiagnosis> ProbeThrowingAsync(Exception exception)
    {
        using StubHandler handler = new(() => throw exception);
        using HttpClient client = CreateClient(handler);
        return await StreamDiagnostics.ProbeAsync(StreamUrl, client, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task HealthyIcecastStream_IsReportedAsReachable()
    {
        // A real Icecast MP3 stream: audio content type, ICY headers, binary body.
        StreamDiagnosis diagnosis = await ProbeAsync(() => Respond(
            HttpStatusCode.OK,
            "ÿûdbinary mp3 frames",
            "audio/mpeg",
            ("icy-name", "Riverwest Radio"),
            ("icy-br", "128")));

        Assert.AreEqual(StreamProbeResult.Reachable, diagnosis.Result);
        Assert.IsTrue(diagnosis.ServerLooksHealthy);
        Assert.AreEqual("Riverwest Radio", diagnosis.StationName);
        StringAssert.Contains(diagnosis.Detail, "icy-name=Riverwest Radio");
    }

    [TestMethod]
    public async Task Probe_RequestsIcyMetadataSoTheServerIdentifiesItself()
    {
        using StubHandler handler = new(() => Respond(HttpStatusCode.OK, "audio", "audio/mpeg"));
        using HttpClient client = CreateClient(handler);

        await StreamDiagnostics.ProbeAsync(StreamUrl, client, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(handler.LastRequest);
        Assert.IsTrue(handler.LastRequest.Headers.TryGetValues("Icy-MetaData", out _));
    }

    [TestMethod]
    public async Task NotFound_SaysTheStreamAddressNoLongerExists()
    {
        StreamDiagnosis diagnosis = await ProbeAsync(() => Respond(HttpStatusCode.NotFound, "not found"));

        Assert.AreEqual(StreamProbeResult.HttpError, diagnosis.Result);
        Assert.AreEqual(404, diagnosis.StatusCode);
        Assert.IsFalse(diagnosis.ServerLooksHealthy);
        StringAssert.Contains(diagnosis.Summary, "no longer exists");
    }

    [TestMethod]
    public async Task Forbidden_SaysAccessWasRefused()
    {
        StreamDiagnosis diagnosis = await ProbeAsync(() => Respond(HttpStatusCode.Forbidden, string.Empty));

        Assert.AreEqual(StreamProbeResult.HttpError, diagnosis.Result);
        StringAssert.Contains(diagnosis.Summary, "refused access");
    }

    [TestMethod]
    public async Task ServerError_IsDistinguishedFromAMissingStream()
    {
        StreamDiagnosis diagnosis = await ProbeAsync(() => Respond(HttpStatusCode.ServiceUnavailable, string.Empty));

        Assert.AreEqual(StreamProbeResult.HttpError, diagnosis.Result);
        StringAssert.Contains(diagnosis.Summary, "internal error");
    }

    /// <summary>
    /// A .pls address is one of the most common reasons a station "won't play", and the fix
    /// is for the user to use the URL inside it — so the probe has to extract that URL.
    /// </summary>
    [TestMethod]
    public async Task PlsPlaylist_IsIdentifiedAndItsFirstEntryExtracted()
    {
        const string pls = "[playlist]\nNumberOfEntries=1\nFile1=http://stream.example.com:8000/live\nTitle1=Example\n";

        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, pls, "audio/x-scpls"),
            "http://example.com/listen.pls");

        Assert.AreEqual(StreamProbeResult.PlaylistFile, diagnosis.Result);
        Assert.AreEqual("http://stream.example.com:8000/live", diagnosis.PlaylistEntryUrl);
    }

    [TestMethod]
    public async Task M3uPlaylist_IsIdentifiedFromItsBody()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1,Example\nhttp://stream.example.com:8000/live\n";

        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, m3u, "audio/x-mpegurl"),
            "http://example.com/listen.m3u");

        Assert.AreEqual(StreamProbeResult.PlaylistFile, diagnosis.Result);
        Assert.AreEqual("http://stream.example.com:8000/live", diagnosis.PlaylistEntryUrl);
    }

    [TestMethod]
    public async Task AsxPlaylist_IsIdentifiedAndItsHrefExtracted()
    {
        const string asx = "<asx version=\"3.0\">\n<entry>\n<ref href=\"http://stream.example.com/live\" />\n</entry>\n</asx>";

        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, asx, "video/x-ms-asf"),
            "http://example.com/listen.asx");

        Assert.AreEqual(StreamProbeResult.PlaylistFile, diagnosis.Result);
        Assert.AreEqual("http://stream.example.com/live", diagnosis.PlaylistEntryUrl);
    }

    /// <summary>
    /// An HLS playlist shares a content type with a plain .m3u but is a stream both engines
    /// handle natively, so it must not be flagged as the user pointing at the wrong file.
    /// </summary>
    [TestMethod]
    public async Task HlsPlaylist_IsNotTreatedAsAMisconfiguredPlaylist()
    {
        const string hls = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\nsegment0.ts\n";

        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, hls, "application/vnd.apple.mpegurl"),
            "http://example.com/live/hls.m3u8");

        Assert.AreNotEqual(StreamProbeResult.PlaylistFile, diagnosis.Result);
    }

    [TestMethod]
    public async Task HtmlErrorPage_IsReportedAsNotAStream()
    {
        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Station offline</body></html>", "text/html"));

        Assert.AreEqual(StreamProbeResult.UnsupportedContent, diagnosis.Result);
        StringAssert.Contains(diagnosis.Summary, "web page");
    }

    [TestMethod]
    public async Task EmptyBody_IsReportedAsNoAudio()
    {
        StreamDiagnosis diagnosis = await ProbeAsync(
            () => Respond(HttpStatusCode.OK, string.Empty, "audio/mpeg"));

        Assert.AreEqual(StreamProbeResult.EmptyStream, diagnosis.Result);
    }

    [TestMethod]
    public async Task DnsFailure_IsReportedAsAnAddressThatCouldNotBeFound()
    {
        StreamDiagnosis diagnosis = await ProbeThrowingAsync(
            new HttpRequestException("name resolution failed", new SocketException((int)SocketError.HostNotFound)));

        Assert.AreEqual(StreamProbeResult.Unresolvable, diagnosis.Result);
        StringAssert.Contains(diagnosis.Summary, "couldn't be found");
    }

    [TestMethod]
    public async Task ConnectionRefused_IsReportedAsTheServerBeingOffline()
    {
        StreamDiagnosis diagnosis = await ProbeThrowingAsync(
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));

        Assert.AreEqual(StreamProbeResult.ConnectionRefused, diagnosis.Result);
        StringAssert.Contains(diagnosis.Summary, "offline");
    }

    [TestMethod]
    public async Task UnreachableNetwork_IsReportedAsATimeout()
    {
        StreamDiagnosis diagnosis = await ProbeThrowingAsync(
            new HttpRequestException("unreachable", new SocketException((int)SocketError.HostUnreachable)));

        Assert.AreEqual(StreamProbeResult.Timeout, diagnosis.Result);
    }

    [TestMethod]
    public async Task TlsFailure_IsDistinguishedFromAnUnreachableServer()
    {
        StreamDiagnosis diagnosis = await ProbeThrowingAsync(
            new HttpRequestException(
                "handshake failed",
                new System.Security.Authentication.AuthenticationException("certificate expired")));

        Assert.AreEqual(StreamProbeResult.TlsFailure, diagnosis.Result);
    }

    [TestMethod]
    public async Task UnclassifiedTransportFailure_StillProducesAUsableAnswer()
    {
        StreamDiagnosis diagnosis = await ProbeThrowingAsync(new HttpRequestException("something odd"));

        Assert.AreEqual(StreamProbeResult.Unknown, diagnosis.Result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnosis.Summary));
    }

    [TestMethod]
    public async Task MalformedUrl_IsReportedWithoutMakingARequest()
    {
        using StubHandler handler = new(() => throw new InvalidOperationException("should not be called"));
        using HttpClient client = CreateClient(handler);

        StreamDiagnosis diagnosis = await StreamDiagnostics.ProbeAsync("not a url", client);

        Assert.AreEqual(StreamProbeResult.Unknown, diagnosis.Result);
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesRatherThanBeingReportedAsATimeout()
    {
        using StubHandler handler = new(() => throw new OperationCanceledException());
        using HttpClient client = CreateClient(handler);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Assert.ThrowsException matches the exact type, and the runtime may surface either
        // OperationCanceledException or its TaskCanceledException subclass here.
        try
        {
            await StreamDiagnostics.ProbeAsync(StreamUrl, client, TimeSpan.FromSeconds(5), cts.Token);
            Assert.Fail("Expected the caller's cancellation to propagate.");
        }
        catch (OperationCanceledException)
        {
            // Expected: caller cancellation must not be reported as a stream timeout.
        }
    }

    [TestMethod]
    [DataRow("http://example.com/listen.pls", true)]
    [DataRow("http://example.com/listen.PLS?x=1", true)]
    [DataRow("http://example.com/listen.m3u", true)]
    [DataRow("http://example.com/listen.asx", true)]
    [DataRow("http://example.com/live/hls.m3u8", false)]
    [DataRow("http://example.com:8000/live", false)]
    [DataRow("not a url", false)]
    public void HasPlaylistExtension_MatchesOnlyPlaylistFiles(string url, bool expected)
    {
        Assert.AreEqual(expected, StreamDiagnostics.HasPlaylistExtension(url));
    }

    [TestMethod]
    public void ExtractFirstPlaylistEntry_SkipsCommentsAndNonUrlLines()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1,Some Title\n\nnot-a-url\nhttp://stream.example.com/live\n";

        Assert.AreEqual("http://stream.example.com/live", StreamDiagnostics.ExtractFirstPlaylistEntry(m3u));
    }

    [TestMethod]
    public void ExtractFirstPlaylistEntry_WithNoUrls_ReturnsNull()
    {
        Assert.IsNull(StreamDiagnostics.ExtractFirstPlaylistEntry("[playlist]\nNumberOfEntries=0\n"));
    }
}
