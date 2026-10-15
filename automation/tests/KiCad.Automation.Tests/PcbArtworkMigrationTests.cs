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

    [TestMethod]
    public void CompensatingRollbackBindsDeleteToCommittedAfterStateAndStableOperation()
    {
        DocumentSpecifier document = new()
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "board.kicad_pcb",
            Project = new ProjectSpecifier { Name = "artwork", Path = "/tmp/artwork" }
        };
        string epoch = Guid.NewGuid().ToString("D");
        string operation = Guid.NewGuid().ToString("D");
        string firstId = Guid.NewGuid().ToString("D");
        string secondId = Guid.NewGuid().ToString("D");
        var after = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = epoch,
            NativeIdentity = Guid.NewGuid().ToString("D"),
            Revision = new() { Epoch = Guid.NewGuid().ToString("D"), Sequence = 8 },
            ProjectSettingsIncluded = true,
            DiskBaselineChecked = true,
            StateSha256 = new string('b', 64)
        };

        string rollbackId = PcbArtworkTools.CompensatingRollbackOperationId(
            operation, [secondId, firstId]);
        CheckedPcbItemTransaction request = PcbArtworkTools.BuildCompensatingDeleteRequest(
            after, [secondId, firstId], rollbackId);

        Assert.AreEqual(rollbackId, PcbArtworkTools.CompensatingRollbackOperationId(
            operation, [firstId, secondId]));
        Assert.AreEqual(rollbackId, request.OperationId);
        Assert.AreEqual(request.ExpectedState, after);
        Assert.AreEqual(epoch, request.ProcessEpoch);
        Assert.AreEqual(PcbItemOperation.ActionOneofCase.Delete, request.Operations[0].ActionCase);
        CollectionAssert.AreEquivalent(new[] { firstId, secondId },
            request.Operations[0].Delete.ItemIds.Select(id => id.Value).ToArray());
        Assert.AreEqual(document, request.Operations[0].Delete.Header.Document);
    }

    [TestMethod]
    public void CompensatingRollbackRequiresExactDeletedIdsAndRestoredCheckpoint()
    {
        DocumentSpecifier document = new()
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "board.kicad_pcb",
            Project = new ProjectSpecifier { Name = "artwork", Path = "/tmp/artwork" }
        };
        string epoch = Guid.NewGuid().ToString("D");
        string identity = Guid.NewGuid().ToString("D");
        string revisionEpoch = Guid.NewGuid().ToString("D");
        string firstId = Guid.NewGuid().ToString("D");
        string secondId = Guid.NewGuid().ToString("D");
        var before = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = epoch,
            NativeIdentity = identity,
            Revision = new() { Epoch = revisionEpoch, Sequence = 4 },
            ProjectSettingsIncluded = true,
            DiskBaselineChecked = true,
            StateSha256 = new string('a', 64)
        };
        var committedAfter = before.Clone();
        committedAfter.Revision.Sequence = 5;
        committedAfter.StateSha256 = new string('b', 64);
        string rollbackId = PcbArtworkTools.CompensatingRollbackOperationId(
            Guid.NewGuid().ToString("D"), [firstId, secondId]);
        CheckedPcbItemTransaction request = PcbArtworkTools.BuildCompensatingDeleteRequest(
            committedAfter, [firstId, secondId], rollbackId);
        var receipt = new CheckedPcbItemTransactionReceipt
        {
            Document = document.Clone(),
            ProcessEpoch = epoch,
            OperationId = rollbackId,
            State = CheckedPcbItemTransactionState.CpbtsCompleted,
            RequestVerified = true,
            ObservedBefore = committedAfter.Clone(),
            ObservedAfter = before.Clone()
        };
        receipt.Results.Add(new PcbItemOperationResult
        {
            Delete = new DeleteItemsResponse
            {
                Status = ItemRequestStatus.IrsOk,
                DeletedItems =
                {
                    new ItemDeletionResult { Id = new() { Value = secondId }, Status = ItemDeletionStatus.IdsOk },
                    new ItemDeletionResult { Id = new() { Value = firstId }, Status = ItemDeletionStatus.IdsOk }
                }
            }
        });
        var outcome = new PcbCheckedItemDispatch.Outcome("instance", request, receipt,
            NativeReceiptAvailable: true);

        Assert.IsTrue(PcbArtworkTools.CompensatingRollbackConfirmed(before, outcome,
            [firstId, secondId]));
        Assert.IsFalse(PcbArtworkTools.CompensatingRollbackConfirmed(before, outcome,
            [firstId]));

        receipt.Results[0].Delete.DeletedItems[0].Status =
            (ItemDeletionStatus)((int)ItemDeletionStatus.IdsOk + 1);
        Assert.IsFalse(PcbArtworkTools.CompensatingRollbackConfirmed(before, outcome,
            [firstId, secondId]),
            "A matching item ID with a failed delete status must not prove rollback.");
        receipt.Results[0].Delete.DeletedItems[0].Status = ItemDeletionStatus.IdsOk;
        receipt.ObservedAfter.StateSha256 = new string('c', 64);
        Assert.IsFalse(PcbArtworkTools.CompensatingRollbackConfirmed(before, outcome,
            [firstId, secondId]));
    }
}
