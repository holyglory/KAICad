using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KiCad.Automation.Distribution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateDownloaderTests
{
    [TestMethod]
    public async Task ActualHttpsTransferAndMidstreamCancellationPreserveInstallation()
    {
        string root = Directory.CreateTempSubdirectory("kicad-update-https-").FullName;
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        byte[] bytes = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "fixture-1", new string('1', 40),
            [new("linux-x64", "tar.gz", "fixture.tar.gz", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        byte[] installed = UpdateManifestCodec.Sign(release, publisher);
        byte[] published = UpdateManifestCodec.Sign(release with { Sequence = 2, Version = "fixture-2" }, publisher);
        int artifactStatus = StatusCodes.Status200OK;
        app.MapGet("/artifacts/fixture.tar.gz", () => Volatile.Read(ref artifactStatus) == StatusCodes.Status200OK
            ? (IResult)Results.Bytes(bytes, "application/octet-stream") : Results.StatusCode(Volatile.Read(ref artifactStatus)));
        app.MapGet("/updates/preview.json", () => Results.Bytes(published, "application/json"));
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                // Exact ephemeral test certificate only; production never uses
                // a permissive TLS callback or this synthetic publisher.
                ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                    peer is not null && peer.RawData.AsSpan().SequenceEqual(certificate.RawData)
            };
            using var downloader = new UpdateDownloader(new Uri(address + "/"), handler);
            string state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
            var check = await new UpdateChecker(downloader, state, publisher.ExportSubjectPublicKeyInfo(), installed,
                "preview", "linux-x64", "tar.gz").CheckAsync();
            Assert.AreEqual(UpdateAvailability.Available, check.Availability);
            var manifest = check.Manifest;
            var downloaded = await downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded.Path));
            var filesBeforeFailure = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
            var directoriesBeforeFailure = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray();
            foreach (int httpStatus in new[] { StatusCodes.Status404NotFound, StatusCodes.Status503ServiceUnavailable,
                                               StatusCodes.Status206PartialContent })
            {
                Volatile.Write(ref artifactStatus, httpStatus);
                var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root));
                StringAssert.Contains(error.Message, $"HTTP {httpStatus}");
                CollectionAssert.AreEqual(filesBeforeFailure,
                    Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray());
                CollectionAssert.AreEqual(directoriesBeforeFailure,
                    Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray());
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded.Path));
            }
            Volatile.Write(ref artifactStatus, StatusCodes.Status200OK);
            var recovered = await downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(recovered.Path));
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(recovered.Path)!, "download.json")));
            using var cancellation = new CancellationTokenSource();
            var progress = new CancellingProgress(cancellation);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root, progress, cancellation.Token));
            Assert.IsTrue(progress.Received > 0, "Cancellation must occur after transfer starts.");
            CollectionAssert.AreEqual(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray());
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded.Path));
            published = installed;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new UpdateChecker(downloader, state, publisher.ExportSubjectPublicKeyInfo(), installed,
                    "preview", "linux-x64", "tar.gz").CheckAsync());
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DownloadsOnlySignedBytesAndDoesNotReplaceExistingFiles()
    {
        string root = Directory.CreateTempSubdirectory("kicad-update-download-").FullName;
        try
        {
            byte[] bytes = "Synthetic update payload for transfer tests."u8.ToArray();
            var manifest = Signed(bytes);
            string existing = Path.Combine(root, "installed.txt");
            await File.WriteAllTextAsync(existing, "Existing installation fixture.");
            var handler = new Handler((request, _) =>
            {
                Assert.AreEqual("https://updates.example.test/releases/artifacts/fixture.tar.gz", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) });
            });
            using var downloader = new UpdateDownloader(new Uri("https://updates.example.test/releases/"), handler);
            var progress = new ProgressRecorder();
            var downloaded = await downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root, progress);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded.Path));
            Assert.AreEqual(manifest.PayloadSha256, downloaded.ManifestSha256);
            Assert.AreEqual(bytes.Length, progress.Values.Last().ReceivedBytes);
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(downloaded.Path)!, "download.json")));
            Assert.AreEqual("Existing installation fixture.", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WrongHashSizeRedirectAndEncodingNeverBecomeVerifiedDownloads()
    {
        byte[] bytes = "Synthetic expected payload."u8.ToArray();
        var manifest = Signed(bytes);
        foreach (string fault in new[] { "hash", "short", "long", "redirect", "encoding", "partial" })
        {
            string root = Directory.CreateTempSubdirectory("kicad-update-failure-").FullName;
            try
            {
                using var downloader = new UpdateDownloader(new Uri("https://updates.example.test/"), new Handler((request, _) =>
                {
                    byte[] actual = fault switch
                    {
                        "hash" => Enumerable.Repeat((byte)'x', bytes.Length).ToArray(),
                        "short" => bytes[..^1],
                        "long" => [.. bytes, 0],
                        _ => bytes
                    };
                    var response = new HttpResponseMessage(fault == "partial" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { RequestMessage = request, Content = new StreamContent(new NonSeekable(actual)) };
                    if (fault == "redirect") response.Headers.Location = new Uri("https://unapproved.example.test/file");
                    if (fault == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                    return Task.FromResult(response);
                }));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root));
                Assert.IsEmpty(Directory.GetFiles(root, "*", SearchOption.AllDirectories), fault);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [TestMethod]
    public async Task PersistenceCollisionsPreserveExistingFilesAndPermitRetry()
    {
        byte[] bytes = "Synthetic collision payload."u8.ToArray();
        foreach (string name in new[] { "package.tar.gz", "download.partial.json", "download.json" })
        {
            string root = Directory.CreateTempSubdirectory("kicad-update-collision-").FullName;
            try
            {
                using var downloader = new UpdateDownloader(new Uri("https://updates.example.test/"), new Handler((request, _) =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { RequestMessage = request, Content = new ByteArrayContent(bytes) })));
                string? existing = null;
                var progress = new CallbackProgress(_ =>
                {
                    existing = Path.Combine(Directory.GetDirectories(root).Single(), name);
                    File.WriteAllText(existing, "Pre-existing fixture must survive.");
                });
                await Assert.ThrowsAsync<IOException>(() =>
                    downloader.DownloadAsync(Signed(bytes), "linux-x64", "tar.gz", root, progress));
                Assert.AreEqual("Pre-existing fixture must survive.", await File.ReadAllTextAsync(existing!));
                CollectionAssert.AreEqual(new[] { existing }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
                var retry = await downloader.DownloadAsync(Signed(bytes), "linux-x64", "tar.gz", root);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(retry.Path));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [TestMethod]
    public async Task CancellationAndMissingTargetDoNotWriteAnUpdate()
    {
        string root = Directory.CreateTempSubdirectory("kicad-update-cancel-").FullName;
        try
        {
            var manifest = Signed("fixture"u8.ToArray());
            int requests = 0;
            using var downloader = new UpdateDownloader(new Uri("https://updates.example.test/"), new Handler(async (request, token) =>
            {
                requests++;
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            }));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                downloader.DownloadAsync(manifest, "osx-arm64", "zip", root));
            Assert.AreEqual(0, requests);
            using var cancelled = new CancellationTokenSource();
            var pending = downloader.DownloadAsync(manifest, "linux-x64", "tar.gz", root, cancellationToken: cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PublisherOriginMustBeExplicitHttps()
    {
        foreach (string uri in new[] { "http://example.test/", "https://user@example.test/", "https://example.test/?q=1",
            "https://example.test/#fragment", "https://example.test/no-slash" })
            Assert.ThrowsExactly<ArgumentException>(() => new UpdateDownloader(new Uri(uri)));
    }

    private static VerifiedUpdateManifest Signed(byte[] bytes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new UpdateRelease(1, "kicad-codex", "preview", 1, "fixture", new string('1', 40),
            [new("linux-x64", "tar.gz", "fixture.tar.gz", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        return UpdateManifestCodec.Verify(UpdateManifestCodec.Sign(release, key), key.ExportSubjectPublicKeyInfo(), "preview");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
    private sealed class ProgressRecorder : IProgress<UpdateTransferProgress>
    {
        public List<UpdateTransferProgress> Values { get; } = [];
        public void Report(UpdateTransferProgress value) => Values.Add(value);
    }
    private sealed class CallbackProgress(Action<UpdateTransferProgress> callback) : IProgress<UpdateTransferProgress>
    {
        public void Report(UpdateTransferProgress value) => callback(value);
    }
    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<UpdateTransferProgress>
    {
        public long Received { get; private set; }
        public void Report(UpdateTransferProgress value) { Received = value.ReceivedBytes; cancellation.Cancel(); }
    }
    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
