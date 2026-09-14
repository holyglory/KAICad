using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace KiCad.Automation.Distribution;

public sealed record UpdateTransferProgress(long ReceivedBytes, long ExpectedBytes);
public sealed class DownloadedUpdate
{
    public string Path { get; }
    public UpdateArtifact Artifact { get; }
    public string ManifestSha256 { get; }
    internal DownloadedUpdate(string path, UpdateArtifact artifact, string manifestSha256)
    { Path = path; Artifact = artifact; ManifestSha256 = manifestSha256; }
}

/// <summary>Downloads authenticated bytes only. This does not install, launch,
/// validate native signing, or mark an update ready for user activation.</summary>
public sealed class UpdateDownloader : IDisposable
{
    private readonly Uri origin;
    private readonly HttpClient client;

    public UpdateDownloader(Uri origin) : this(origin, new SocketsHttpHandler { AllowAutoRedirect = false }) { }

    internal UpdateDownloader(Uri origin, HttpMessageHandler handler)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0
            || origin.Query.Length != 0 || origin.Fragment.Length != 0 || !origin.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("Use an absolute HTTPS publisher base URL without credentials, query or fragment.", nameof(origin));
        this.origin = origin;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DownloadedUpdate> DownloadAsync(VerifiedUpdateManifest manifest, string platform,
        string format, string stagingRoot, IProgress<UpdateTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = manifest.ForInstallation(platform, format)
            ?? throw new InvalidDataException("No update exists for this exact installation target.");
        if (!Path.IsPathFullyQualified(stagingRoot) || !Directory.Exists(stagingRoot))
            throw new ArgumentException("Use an existing dedicated update staging directory.", nameof(stagingRoot));
        cancellationToken.ThrowIfCancellationRequested();
        using var response = await OpenAsync("artifacts/" + Uri.EscapeDataString(artifact.FileName), cancellationToken);
        if (response.Content.Headers.ContentLength is long length && length != artifact.Bytes)
            throw new InvalidDataException("Update download size does not match signed metadata.");

        string directory = Directory.CreateDirectory(System.IO.Path.Combine(stagingRoot,
            "update-" + Guid.NewGuid().ToString("N"))).FullName;
        string partial = System.IO.Path.Combine(directory, "payload.partial");
        string complete = System.IO.Path.Combine(directory, "package." + artifact.Format);
        string receipt = System.IO.Path.Combine(directory, "download.json");
        string pendingReceipt = System.IO.Path.Combine(directory, "download.partial.json");
        long received = 0;
        bool ownsPartial = false, ownsComplete = false, ownsPendingReceipt = false, ownsReceipt = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsPartial = true;
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (count > artifact.Bytes - received)
                        throw new InvalidDataException("Update download exceeds its signed size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    progress?.Report(new(received, artifact.Bytes));
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (received != artifact.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != artifact.Sha256)
                throw new InvalidDataException("Update download failed signed size or checksum verification.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, complete, overwrite: false);
            ownsPartial = false;
            ownsComplete = true;
            await using (var receiptFile = new FileStream(pendingReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPendingReceipt = true;
                await JsonSerializer.SerializeAsync(receiptFile, new
                {
                    schemaVersion = 1, manifestSha256 = manifest.PayloadSha256,
                    publisherKeySha256 = manifest.PublisherKeySha256,
                    release = manifest.Release.Version, commit = manifest.Release.Commit,
                    artifact, receivedBytes = received, status = "download_verified", installationReady = false
                }, cancellationToken: cancellationToken);
                await receiptFile.FlushAsync(cancellationToken);
                receiptFile.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pendingReceipt, receipt, overwrite: false);
            ownsPendingReceipt = false;
            ownsReceipt = true;
            return new(complete, artifact, manifest.PayloadSha256);
        }
        catch
        {
            // Only this invocation's newly created staging files are removed.
            // The caller supplies a dedicated staging root; existing files are
            // never replaced and no activation or process restart is performed.
            if (ownsPartial) File.Delete(partial);
            if (ownsComplete) File.Delete(complete);
            if (ownsPendingReceipt) File.Delete(pendingReceipt);
            if (ownsReceipt) File.Delete(receipt);
            throw;
        }
    }

    internal async Task<byte[]> FetchManifestAsync(string channel, CancellationToken cancellationToken)
    {
        if (channel is not ("preview" or "stable"))
            throw new ArgumentException("Unknown update channel.", nameof(channel));
        using var response = await OpenAsync("updates/" + channel + ".json", cancellationToken);
        if (response.Content.Headers.ContentLength is long length
            && (length < 1 || length > UpdateManifestCodec.MaximumEnvelopeBytes))
            throw new InvalidDataException("Update envelope size is invalid.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadEnvelopeAsync(input, cancellationToken);
    }

    internal static async Task<byte[]> ReadEnvelopeAsync(Stream input, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (count > UpdateManifestCodec.MaximumEnvelopeBytes - output.Length)
                throw new InvalidDataException("Update envelope exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> OpenAsync(string relativePath, CancellationToken cancellationToken)
    {
        Uri uri = new(origin, relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"Update download returned HTTP {(int)response.StatusCode}; no package was accepted.");
            if (response.RequestMessage?.RequestUri != uri || response.Headers.Location is not null)
                throw new InvalidDataException("Update download changed the requested publisher location.");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("Update download applied an undeclared content encoding.");
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    public void Dispose() => client.Dispose();
}
