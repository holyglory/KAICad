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
    }
}
