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
        Assert.AreEqual("0837b533060887d38b4685c505aa34622ca175cfb28741d5643729e42506fe10", hash);

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
        Assert.AreEqual(2, contours.Count(contour => !contour.GetProperty("hole").GetBoolean()), first.GetRawText());
        Assert.AreEqual(1, contours.Count(contour => contour.GetProperty("hole").GetBoolean()), first.GetRawText());
        Assert.IsTrue(contours.Any(contour => !contour.GetProperty("hole").GetBoolean()
            && contour.GetProperty("parentId").GetString() is { Length: > 0 }), first.GetRawText());
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

        var tightBudget = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 3 }
        });
        Assert.IsFalse(tightBudget.GetProperty("isError").GetBoolean(), tightBudget.GetRawText());
        Assert.IsFalse(tightBudget.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), tightBudget.GetRawText());
        Assert.AreEqual("trace_complexity", tightBudget.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());

        var tightTolerance = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 64_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsFalse(tightTolerance.GetProperty("isError").GetBoolean(), tightTolerance.GetRawText());
        Assert.IsFalse(tightTolerance.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), tightTolerance.GetRawText());
        Assert.AreEqual("curve_tolerance_too_small", tightTolerance.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());

        var hugeCoordinates = await mcp.Tool("kicad_artwork_raster_trace", new
        {
            instanceId,
            input = new { sourcePath = source, expectedSourceSha256 = hash, physicalWidthNm = 3_000_000_000L,
                threshold = 128, invert = false, turdSize = 0, curveToleranceNm = 1_000L, maxContours = 4096, maxVertices = 1_000_000 }
        });
        Assert.IsFalse(hugeCoordinates.GetProperty("isError").GetBoolean(), hugeCoordinates.GetRawText());
        Assert.IsFalse(hugeCoordinates.GetProperty("structuredContent").GetProperty("trace").GetProperty("accepted").GetBoolean(), hugeCoordinates.GetRawText());
        Assert.AreEqual("coordinate_range", hugeCoordinates.GetProperty("structuredContent").GetProperty("trace").GetProperty("rejectionCode").GetString());
    }
}
