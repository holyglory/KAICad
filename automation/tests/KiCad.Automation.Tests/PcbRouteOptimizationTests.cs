using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbRouteOptimizationTests
{
    [TestMethod]
    public void MergesOnlyDegreeTwoCollinearTrackChains()
    {
        DocumentSpecifier document = Document();
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000001", 0, 0, 10, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000002", 10, 0, 20, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000003", 20, 0, 30, 0)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000004", 10, 0, 10, 10)));
        var plan = PcbRouteOptimizationTools.BuildPlan(candidate, new() { Nets = ["N"], MaxMerges = 8 });

        Assert.AreEqual(1, plan.MergedCount, "The branch at (10,0) must prevent a merge there; the straight chain at (20,0) is safe.");
        CollectionAssert.AreEqual(new[] { "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000003" }, plan.RemovedItemIds.ToArray());
        Track merged = plan.Create.Items.Single(item => item.Is(Track.Descriptor)).Unpack<Track>();
        Assert.AreEqual(10_000, merged.Start.XNm);
        Assert.AreEqual(30_000, merged.End.XNm);
        Assert.AreEqual(3, plan.PreservedItemCount);
    }

    [TestMethod]
    public void LeavesDifferentNetsAndWidthsUntouched()
    {
        var candidate = new CreateItems { Header = new() { Document = Document() } };
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000011", 0, 0, 10, 0, "A", 100)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000012", 10, 0, 20, 0, "A", 200)));
        candidate.Items.Add(Any.Pack(MakeTrack("00000000-0000-0000-0000-000000000013", 10, 0, 20, 0, "B", 100)));
        var plan = PcbRouteOptimizationTools.BuildPlan(candidate, new() { Nets = ["A"], MaxMerges = 8 });
        Assert.AreEqual(0, plan.MergedCount);
        Assert.AreEqual(0, plan.Delete.ItemIds.Count);
        Assert.AreEqual(3, plan.PreservedItemCount);
    }

    [TestMethod]
    public void RouteTuningMeasuresPhysicalSegmentLength()
    {
        var points = new[]
        {
            new Vector2 { XNm = 0, YNm = 0 },
            new Vector2 { XNm = 3, YNm = 4 }
        };

        Assert.AreEqual(5, PcbRouteTuningTools.PathLength(points));
    }

    [TestMethod]
    public void RouteTuningAcceptsAxisAlignedCenterlineAndMeasuresItsLength()
    {
        var points = new[]
        {
            new Vector2 { XNm = 0, YNm = 0 },
            new Vector2 { XNm = 10, YNm = 0 },
            new Vector2 { XNm = 10, YNm = 20 },
            new Vector2 { XNm = 30, YNm = 20 }
        };

        PcbRouteTuningTools.ValidateCenterline(points, points[0], points[^1], -5, -5, 35, 25);
        Assert.AreEqual(50, PcbRouteTuningTools.PathLength(points));
    }

    [TestMethod]
    public void RouteTuningRejectsDiagonalOrMismatchedCenterline()
    {
        var diagonal = new[]
        {
            new Vector2 { XNm = 0, YNm = 0 },
            new Vector2 { XNm = 10, YNm = 10 }
        };
        Assert.ThrowsExactly<AutomationException>(() => PcbRouteTuningTools.ValidateCenterline(
            diagonal, diagonal[0], diagonal[^1], -5, -5, 15, 15));

        var axisAligned = new[]
        {
            new Vector2 { XNm = 0, YNm = 0 },
            new Vector2 { XNm = 10, YNm = 0 }
        };
        Assert.ThrowsExactly<AutomationException>(() => PcbRouteTuningTools.ValidateCenterline(
            axisAligned, new Vector2 { XNm = 1, YNm = 0 }, axisAligned[^1], -5, -5, 15, 15));
    }

    [TestMethod]
    public void DrcReplacementBuildsExactCheckedRouteRequestForReplay()
    {
        string root = Directory.CreateTempSubdirectory("pcb-route-checked-").FullName;
        try
        {
            DocumentSpecifier document = Document();
            document.Project.Path = root;
            string identity = Guid.NewGuid().ToString("D");
            string epoch = Guid.NewGuid().ToString("D");
            var expected = new DocumentLifecycleState
            {
                Document = document,
                ProcessEpoch = epoch,
                NativeIdentity = identity,
                Scope = DocumentLifecycleScope.DlsPcb,
                ProjectSettingsIncluded = true,
                DiskBaselineChecked = true,
                StateSha256 = new string('a', 64),
                Revision = new() { Epoch = identity, Sequence = 4 }
            };
            string board = Path.Combine(root, document.BoardFilename);
            string project = Path.Combine(root, "optimization.kicad_pro");
            foreach (string path in new[] { board, project })
            {
                expected.NativeFiles.Add(path);
                expected.FileBaselines.Add(new NativeFileBaselineState
                {
                    Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                    BaselineExists = false, CurrentExists = false,
                    Status = NativeFileBaselineStatus.NfbsUnchanged
                });
            }
            var replacement = MakeTrack(Guid.NewGuid().ToString("D"), 0, 0, 20, 0);
            var create = new CreateItems { Header = new() { Document = document }, Items = { Any.Pack(replacement) } };
            var delete = new DeleteItems { Header = new() { Document = document }, ItemIds = { new KIID { Value = Guid.NewGuid().ToString("D") } } };
            var request = PcbRouteOptimizationTools.BuildCheckedRequest(
                create, delete, expected, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));

            CheckedPcbRouteContract.ValidateRequest(request, epoch);
            var after = expected.Clone();
            after.Revision.Sequence++;
            after.StateSha256 = new string('b', 64);
            var receipt = new CheckedPcbRouteReceipt
            {
                Document = document,
                ProcessEpoch = epoch,
                OperationId = request.OperationId,
                State = CheckedPcbRouteReceipt.Types.State.Completed,
                RequestVerified = true,
                Before = expected,
                After = after,
                Created = new CreateItemsResponse { Status = ItemRequestStatus.IrsOk }
            };
            receipt.Created.CreatedItems.Add(new ItemCreationResult
            {
                Status = new() { Code = ItemStatusCode.IscOk }, Item = Any.Pack(replacement)
            });
            receipt.Removals = delete.Clone();
            receipt.RemovedResult = new DeleteItemsResponse
            {
                Status = ItemRequestStatus.IrsOk,
                DeletedItems = { new ItemDeletionResult { Id = delete.ItemIds[0].Clone(), Status = ItemDeletionStatus.IdsOk } }
            };
            CheckedPcbRouteContract.ValidateResult(request, receipt, inspect: false);
            receipt.Replayed = true;
            CheckedPcbRouteContract.ValidateResult(request, receipt, inspect: false);

            var emptyDelete = new DeleteItems { Header = delete.Header.Clone() };
            var additive = PcbRouteOptimizationTools.BuildCheckedRequest(
                create, emptyDelete, expected, request.DrcJobId, request.OperationId);
            Assert.IsNull(additive.Removals, "An additive DRC candidate must omit native removals.");
            CheckedPcbRouteContract.ValidateRequest(additive, epoch);
            var additiveReceipt = receipt.Clone();
            additiveReceipt.Removals = null;
            additiveReceipt.RemovedResult = null;
            CheckedPcbRouteContract.ValidateResult(additive, additiveReceipt, inspect: false);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CornerReplacementBuildsOrderedCheckedDeleteCreateRequest()
    {
        DocumentSpecifier document = Document();
        string identity = Guid.NewGuid().ToString("D");
        string epoch = Guid.NewGuid().ToString("D");
        var expected = new DocumentLifecycleState
        {
            Document = document,
            ProcessEpoch = epoch,
            NativeIdentity = identity,
            Scope = DocumentLifecycleScope.DlsPcb,
            ProjectSettingsIncluded = true,
            StateSha256 = new string('a', 64),
            Revision = new() { Epoch = identity, Sequence = 4 }
        };
        var replacement = MakeTrack(Guid.NewGuid().ToString("D"), 0, 0, 20, 0);
        var create = new CreateItems { Header = new() { Document = document }, Items = { Any.Pack(replacement) } };
        var delete = new DeleteItems
        {
            Header = new() { Document = document },
            ItemIds = { new KIID { Value = Guid.NewGuid().ToString("D") } }
        };
        var request = PcbRouteOptimizationTools.BuildCornerCheckedRequest(
            create, delete, expected, Guid.NewGuid().ToString("D"));

        Assert.AreEqual(2, request.Operations.Count);
        Assert.AreEqual(PcbItemOperation.ActionOneofCase.Delete, request.Operations[0].ActionCase);
        Assert.AreEqual(PcbItemOperation.ActionOneofCase.Create, request.Operations[1].ActionCase);
        Assert.AreEqual(delete, request.Operations[0].Delete);
        Assert.AreEqual(create, request.Operations[1].Create);
        CheckedPcbItemContract.ValidateRequest(request, epoch, inspect: true);
    }

    private static Track MakeTrack(string id, long x1, long y1, long x2, long y2, string net = "N", long width = 100)
        => new()
        {
            Id = new() { Value = id }, Start = new() { XNm = x1 * 1000, YNm = y1 * 1000 },
            End = new() { XNm = x2 * 1000, YNm = y2 * 1000 },
            Width = new() { ValueNm = width * 1000 }, Layer = BoardLayer.BlFCu, Net = new() { Name = net }
        };

    private static DocumentSpecifier Document() => new()
    {
        Type = DocumentType.DoctypePcb, BoardFilename = "optimization.kicad_pcb",
        Project = new() { Name = "optimization", Path = "/tmp/kicad-optimization" }
    };
}
