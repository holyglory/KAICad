using System.Security.Cryptography;
using System.Text.Json;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeRasterTrace(NativeClient client, string evidence,
        string instanceId, CancellationToken token)
    {
        string source = Path.Combine(RepositoryRoot(), "automation", "tests", "fixtures", "raster_trace",
            "outer-hole-diagonal-64.png");
        byte[] bytes = await File.ReadAllBytesAsync(source, token);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.AreEqual("0f80242e277737fc37707a9158598dc33c4374b672cc2c893e295bd8612847f9", hash);

        string stateDirectory = Path.Combine(evidence, instanceId + "-raster-trace-mcp");
        await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
            stateDirectory, Path.Combine(evidence, instanceId + "-raster-trace-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new
            { endpoint = client.Endpoint, expectedInstanceId = instanceId }));

        var input = new
        {
            sourcePath = source,
            expectedSourceSha256 = hash,
            physicalWidthNm = 64_000_000L,
            threshold = 128,
            invert = false,
            turdSize = 0,
            curveToleranceNm = 1_000L,
            maxContours = 4096,
            maxVertices = 1_000_000,
            sourceOrigin = "deterministic-fixture",
            hostReceipt = "fixture-manifest"
        };
        var first = await mcp.Tool("kicad_artwork_raster_trace", new { instanceId, input });
        RequireToolSuccess(first);
        var data = first.GetProperty("structuredContent");
        var trace = data.GetProperty("trace");
        Assert.IsTrue(trace.GetProperty("accepted").GetBoolean(), first.GetRawText());
        Assert.AreEqual(64, trace.GetProperty("decodedWidth").GetInt32());
        Assert.AreEqual(64, trace.GetProperty("decodedHeight").GetInt32());
        Assert.AreEqual(hash, trace.GetProperty("sourceSha256").GetString());
        Assert.IsGreaterThan(0, trace.GetProperty("contourCount").GetInt32());
        Assert.IsGreaterThan(0, trace.GetProperty("filledPieceCount").GetInt32());
        var contours = trace.GetProperty("contours").EnumerateArray().ToArray();
        Assert.AreEqual(1, contours.Count(contour => !contour.GetProperty("hole").GetBoolean()), first.GetRawText());
        Assert.AreEqual(1, contours.Count(contour => contour.GetProperty("hole").GetBoolean()), first.GetRawText());
        Assert.IsFalse(data.GetProperty("nativeDocumentMutated").GetBoolean());
        Assert.IsFalse(data.GetProperty("cadProjectionCommitted").GetBoolean());
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-raster-trace.json"), first.GetRawText(), token);

        var replay = await mcp.Tool("kicad_artwork_raster_trace", new { instanceId, input });
        RequireToolSuccess(replay);
        Assert.AreEqual(first.GetProperty("structuredContent").GetRawText(), replay.GetProperty("structuredContent").GetRawText());

        var mismatch = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = new string('0', 64), physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsTrue(mismatch.GetProperty("isError").GetBoolean(), mismatch.GetRawText());
        Assert.AreEqual("source_hash_mismatch", mismatch.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }
}
