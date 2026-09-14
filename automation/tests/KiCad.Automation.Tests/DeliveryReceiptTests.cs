using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Distribution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DeliveryReceiptTests
{
    [TestMethod, TestCategory("DeliveryReceipts"), TestCategory("ExternalIntegration")]
    public async Task ExportActualArchiveAndOwnedWebObservations()
    {
        string Required(string key) => Environment.GetEnvironmentVariable(key)
            ?? throw new AssertFailedException("Missing delivery observation input: " + key);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".devcoordinator.toml")))
            directory = directory.Parent;
        string root = directory?.FullName ?? throw new AssertFailedException("Repository root not found.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string output = Directory.CreateDirectory(Required("KICAD_DELIVERY_EVIDENCE")).FullName;
        var origin = new Uri(Required("KICAD_PUBLIC_DOWNLOAD_URL"));
        Assert.AreEqual("https", origin.Scheme);
        string digest = (await Command(Required("KICAD_COORDINATOR_EXECUTOR"), "source-digest", "--worktree", root))
            .GetProperty("sha256").GetString()!;
        Assert.AreEqual(64, digest.Length);
        byte[] key = await File.ReadAllBytesAsync(Required("KICAD_UPDATE_PUBLISHER_SPKI_FILE"), deadline.Token);
        using var source = new UpdateDownloader(origin);
        byte[] envelope = await source.FetchManifestAsync("preview", deadline.Token);
        var verified = UpdateManifestCodec.Verify(envelope, key, "preview");
        Assert.AreEqual(Required("KICAD_DELIVERY_COMMIT"), verified.Release.Commit);
        var artifact = verified.ForInstallation("linux-x64", "tar.gz");
        Assert.IsNotNull(artifact);
        string scratch = Directory.CreateTempSubdirectory("kicad-delivery-proof-").FullName;
        try
        {
            var downloaded = await source.DownloadAsync(verified, "linux-x64", "tar.gz", scratch,
                cancellationToken: deadline.Token);
            string archive = Path.Combine(output, artifact.FileName);
            File.Copy(downloaded.Path, archive, overwrite: true);
            await using (var stream = File.OpenRead(archive))
            {
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, deadline.Token));
                Assert.AreEqual(artifact.Sha256, hash);
                Assert.AreEqual(artifact.Bytes, stream.Length);
                await Write("linux.verification.json", new
                {
                    version = 1, kind = "artifact", target = "linux-x64", source_sha256 = digest,
                    file = artifact.FileName, observed_sha256 = hash,
                    checked_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    access = new Uri(origin, "artifacts/" + Uri.EscapeDataString(artifact.FileName)).AbsoluteUri,
                    observation = "download_matched"
                });
            }
            string deploymentId = Required("KICAD_DELIVERY_DEPLOYMENT");
            var before = await Deployment();
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, origin);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await http.SendAsync(request, deadline.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
            byte[] html = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            Assert.IsTrue(System.Text.Encoding.UTF8.GetString(html).Contains(artifact.FileName, StringComparison.Ordinal));
            await File.WriteAllBytesAsync(Path.Combine(output, "index.html"), html, deadline.Token);
            var after = await Deployment();
            Assert.AreEqual(before.GetProperty("current_generation").GetInt32(), after.GetProperty("current_generation").GetInt32());
            await Write("web.verification.json", new
            {
                version = 1, kind = "web-deployment", target = "downloads-web", source_sha256 = digest,
                file = "index.html", observed_sha256 = Convert.ToHexStringLower(SHA256.HashData(html)),
                checked_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), access = origin.AbsoluteUri,
                observation = "web_route_passed", deployment = new
                {
                    deployment_id = deploymentId, generation_number = after.GetProperty("current_generation").GetInt32(),
                    http_status = (int)response.StatusCode, content_type = response.Content.Headers.ContentType!.ToString()
                }
            });
            Assert.AreEqual(digest, (await Command(Required("KICAD_COORDINATOR_EXECUTOR"),
                "source-digest", "--worktree", root)).GetProperty("sha256").GetString());

            async Task<JsonElement> Deployment()
            {
                var result = await Command("devcoordinator2", "deployment", "status", "--name", "downloads", "--format", "json");
                Assert.IsTrue(result.GetProperty("ok").GetBoolean());
                var state = result.GetProperty("data");
                Assert.AreEqual(deploymentId, state.GetProperty("deployment_id").GetString());
                Assert.AreEqual("running", state.GetProperty("state").GetString());
                Assert.IsTrue(state.GetProperty("readiness").GetProperty("ready").GetBoolean());
                return state.Clone();
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }

        Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(output, name),
            JsonSerializer.Serialize(value), deadline.Token);
        async Task<JsonElement> Command(string executable, params string[] arguments)
        {
            var result = await WindowsLauncherTests.Invoke(executable, arguments, root, deadline.Token, input: null);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.Clone();
        }
    }
}
