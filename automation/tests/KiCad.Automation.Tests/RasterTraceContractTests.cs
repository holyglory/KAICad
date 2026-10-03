using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Managed contract coverage for the native raster proposal envelope. The native
// adapter harness separately exercises Potrace and the retained hole topology.
[TestClass]
public sealed class RasterTraceContractTests
{
    [TestMethod]
    public void TraceJsonRetainsAcceptanceDecisionAndRejectionCode()
    {
        JsonElement accepted = RasterTraceTools.FormatTraceResult(new RasterTraceResult { Accepted = true });
        Assert.IsTrue(accepted.GetProperty("accepted").GetBoolean());

        JsonElement rejected = RasterTraceTools.FormatTraceResult(new RasterTraceResult
        {
            RejectionCode = "trace_complexity"
        });
        Assert.IsFalse(rejected.GetProperty("accepted").GetBoolean());
        Assert.AreEqual("trace_complexity", rejected.GetProperty("rejectionCode").GetString());
    }

    [TestMethod]
    public void EmbeddedSourceCodecCreatesNativeZstdEnvelope()
    {
        byte[] source = Encoding.UTF8.GetBytes("raster-trace-embedded-source");
        EmbeddedFile file = EmbeddedFileCodec.FromBytes("source.png", source);

        Assert.AreEqual("source.png", file.Name);
        Assert.AreEqual(EmbeddedFileType.EftOther, file.Type);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(source)), file.DataHash);

        byte[] compressed = Convert.FromBase64String(file.Data.ToStringUtf8());
        Assert.IsGreaterThan(4, compressed.Length);
        CollectionAssert.AreEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, compressed[..4]);
    }

    [TestMethod]
    public async Task StdioTraceOutputRoundTripsIntoProjectionAndRejectsUnknownProposalFields()
    {
        string root = Directory.CreateTempSubdirectory("raster-trace-stdio-").FullName;
        string source = Path.Combine(FindAutomationRoot(), "tests", "fixtures", "raster_trace",
            "outer-hole-diagonal-64.png");
        byte[] bytes = await File.ReadAllBytesAsync(source);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.ProductionStartInfo(),
                Path.Combine(root, "state"), Path.Combine(root, "mcp.stderr.log"), timeout.Token);
            var proposal = new RasterTraceResult
            {
                Accepted = true, SourceSha256 = sourceSha256, SourceBytes = (ulong)bytes.Length,
                DecodedWidth = 64, DecodedHeight = 64, DecodedFormat = "png", PhysicalWidthNm = 64_000_000,
                PhysicalHeightNm = 64_000_000, ScaleNumerator = 64_000_000, ScaleDenominator = 64,
                CoordinateOrientation = "image_x_right_y_down", Rounding = "nearest_integer", Threshold = 128,
                CurveToleranceNm = 1_000, MaxContours = 4096, MaxVertices = 1_000_000,
                GeometrySha256 = new string('a', 64), ContourCount = 1, VertexCount = 3, FilledPieceCount = 1,
                ProposalOnly = true, PotraceOptTolerancePx = 0.2, ContributorSemantics = "whole-trace"
            };
            proposal.Contours.Add(new RasterTraceContour
            {
                Id = "c0", Orientation = 1,
                Points = { new RasterTracePoint { XNm = 0, YNm = 0 }, new RasterTracePoint { XNm = 1_000, YNm = 0 },
                    new RasterTracePoint { XNm = 0, YNm = 1_000 } }
            });
            proposal.FilledPieces.Add(new RasterTraceFilledPiece
            {
                Id = "p0", SourceContoursAreWholeTrace = true, SourceContourIds = { "c0" },
                Points = { new RasterTracePoint { XNm = 0, YNm = 0 }, new RasterTracePoint { XNm = 1_000, YNm = 0 },
                    new RasterTracePoint { XNm = 0, YNm = 1_000 } }
            });
            JsonElement proposalJson = RasterTraceTools.FormatTraceResult(proposal);
            object Input(JsonElement value) => new
            {
                operationId = Guid.NewGuid().ToString("D"), targetKind = "SYMBOL", sourcePath = source,
                sourceKind = "generated", sourceOrigin = "stdio-contract", expectedProcessEpoch = Guid.NewGuid().ToString("D"),
                proposal = value,
                symbol = new { libraryNickname = "RebindFixture", symbolName = "ReplacedProbe", expectedModifyHash = "1" }
            };

            var replay = await mcp.Tool("kicad_artwork_raster_project", new
            {
                instanceId = Guid.NewGuid().ToString("D"), input = Input(proposalJson)
            });
            Assert.IsTrue(replay.GetProperty("isError").GetBoolean(), replay.GetRawText());
            Assert.AreEqual("symbol_library_read_failed",
                replay.GetProperty("structuredContent").GetProperty("errorCode").GetString(), replay.GetRawText());

            JsonObject malformedProposal = JsonNode.Parse(proposalJson.GetRawText())!.AsObject();
            malformedProposal["unexpectedProposalField"] = true;
            bool rejected = false;
            try
            {
                JsonElement malformedJson = JsonSerializer.Deserialize<JsonElement>(malformedProposal.ToJsonString());
                var malformed = await mcp.Tool("kicad_artwork_raster_project", new
                {
                    instanceId = Guid.NewGuid().ToString("D"), input = Input(malformedJson)
                });
                rejected = malformed.TryGetProperty("isError", out var isError) && isError.GetBoolean();
            }
            catch (IOException)
            {
                // A strict MCP argument binder may surface the JSON exception as
                // a JSON-RPC error instead of a tool result; either is rejection.
                rejected = true;
            }
            Assert.IsTrue(rejected, "Unknown proposal fields must be rejected at the public MCP boundary.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindAutomationRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "KiCad.Automation.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run this source test from the automation checkout.");
    }
}
