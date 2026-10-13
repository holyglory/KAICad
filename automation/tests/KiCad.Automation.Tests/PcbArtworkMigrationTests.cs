using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbArtworkMigrationTests
{
    [TestMethod]
    public void SvgArtworkBuildsOneCheckedCreateTransactionWithExactCheckpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "kicad-pcb-artwork-migration");
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "board.kicad_pcb",
            Project = new() { Name = "artwork", Path = root }
        };
        string processEpoch = Guid.NewGuid().ToString("D");
        var expected = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = processEpoch,
            NativeIdentity = Guid.NewGuid().ToString("D"),
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 4 },
            ProjectSettingsIncluded = true,
            DiskBaselineChecked = true,
            StateSha256 = new string('a', 64)
        };
        string itemId = Guid.NewGuid().ToString("D");
        var shape = new BoardGraphicShape
        {
            Id = new() { Value = itemId },
            Layer = BoardLayer.BlFSilkS,
            Shape = new GraphicShape
            {
                Segment = new()
                {
                    Start = new() { XNm = 1_000, YNm = 2_000 },
                    End = new() { XNm = 3_000, YNm = 4_000 }
                }
            }
        };
        var candidate = new CreateItems { Header = new() { Document = document.Clone() } };
        candidate.Items.Add(Any.Pack(shape));
        string operationId = Guid.NewGuid().ToString("D");

        CheckedPcbItemTransaction request = PcbArtworkTools.BuildCheckedRequest(
            expected, candidate, operationId);

        Assert.AreEqual(operationId, request.OperationId);
        Assert.AreEqual(processEpoch, request.ProcessEpoch);
        Assert.AreEqual(expected, request.ExpectedState);
        Assert.AreEqual(1, request.Operations.Count);
        Assert.AreEqual(PcbItemOperation.ActionOneofCase.Create, request.Operations[0].ActionCase);
        Assert.AreEqual(candidate, request.Operations[0].Create);
        Assert.AreEqual("MCP SVG PCB artwork", request.Description);
        Assert.AreEqual(BoardLayer.BlFSilkS,
            request.Operations[0].Create.Items[0].Unpack<BoardGraphicShape>().Layer);

        string archiveRoot = Directory.CreateTempSubdirectory("pcb-artwork-binding-").FullName;
        try
        {
            byte[] source = System.Text.Encoding.UTF8.GetBytes("svg-source");
            string sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source));
            ArtworkSourceArchive.Receipt archive = ArtworkSourceArchive.Persist(archiveRoot,
                "artwork/source.svg", source, sourceHash, "invalid_archive", "archive_conflict");
            CheckedPcbItemTransaction bound = PcbArtworkTools.BuildCheckedRequest(
                expected, candidate, operationId, archive);
            string binding = ArtworkSourceArchive.TransactionBinding(archive);
            Assert.IsTrue(bound.Description.EndsWith(binding, StringComparison.Ordinal));
            Assert.IsTrue(bound.Description.Contains(archive.ReceiptId, StringComparison.Ordinal));
            Assert.AreEqual(bound.Description,
                PcbArtworkTools.BuildCheckedRequest(expected, candidate, operationId, archive).Description,
                "A retry with the same operation and exact archive receipt must carry the same binding.");
            ArtworkSourceArchive.Receipt changed = archive with { RelativePath = "artwork/other.svg" };
            Assert.AreNotEqual(bound.Description,
                PcbArtworkTools.BuildCheckedRequest(expected, candidate, operationId, changed).Description,
                "Reusing an operation ID with another archive receipt must not replay the original request.");
        }
        finally
        {
            Directory.Delete(archiveRoot, recursive: true);
        }
    }
}
