using Kiapi.Common.Commands;
using Kiapi.Common.Project;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class CheckedProjectNetClassesContractTests
{
    [TestMethod]
    public void DigestIsCanonicalAcrossInputOrder()
    {
        NetClassesResponse first = Classes("HighSpeed", "Default");
        NetClassesResponse second = Classes("Default", "HighSpeed");

        Assert.AreEqual(CheckedProjectNetClassesContract.NetClassesDigest(first),
            CheckedProjectNetClassesContract.NetClassesDigest(second));
    }

    [TestMethod]
    public void AdmissionBindsProjectDocumentEpochAndDigest()
    {
        (CheckedProjectNetClassesTransaction request, NetClassesResponse before) = Request();
        CheckedProjectNetClassesContract.ValidateRequest(request, request.ProcessEpoch, inspect: true);

        var staleDigest = request.Clone();
        staleDigest.ExpectedNetClassesSha256 = new string('b', 64);
        CheckedProjectNetClassesContract.ValidateRequest(staleDigest, staleDigest.ProcessEpoch, inspect: true);

        var wrongDocument = request.Clone();
        wrongDocument.Document.BoardFilename = "other.kicad_pcb";
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedProjectNetClassesContract.ValidateRequest(wrongDocument, request.ProcessEpoch, inspect: true));

        var wrongEpoch = request.Clone();
        wrongEpoch.ProcessEpoch = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedProjectNetClassesContract.ValidateRequest(wrongEpoch, wrongEpoch.ProcessEpoch, inspect: true));
        Assert.AreEqual(request.ExpectedNetClassesSha256,
            CheckedProjectNetClassesContract.NetClassesDigest(before));
    }

    [TestMethod]
    public void UnavailableReceiptCannotClaimMutationOrUndoEvidence()
    {
        (CheckedProjectNetClassesTransaction request, _) = Request();
        var unavailable = new CheckedProjectNetClassesReceipt
        {
            Project = request.Project.Clone(), Document = request.Document.Clone(),
            ProcessEpoch = request.ProcessEpoch, OperationId = request.OperationId,
            State = CheckedProjectNetClassesState.CpcsUnavailable,
            ErrorCode = "project_settings_checked_unavailable",
            ErrorMessage = "The native owner is unavailable."
        };
        CheckedProjectNetClassesContract.ValidateResult(request, unavailable, inspect: false);

        var forged = unavailable.Clone();
        forged.UndoRedoSupported = true;
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedProjectNetClassesContract.ValidateResult(request, forged, inspect: false));
    }

    [TestMethod]
    public void CompletedReceiptRequiresUnsavedUndoableBeforeAfterEvidence()
    {
        (CheckedProjectNetClassesTransaction request, NetClassesResponse before) = Request();
        var after = Classes("Default", "HighSpeed", "Differential");
        var afterState = request.ExpectedState.Clone();
        afterState.Revision.Sequence++;
        afterState.StateSha256 = new string('b', 64);
        var receipt = new CheckedProjectNetClassesReceipt
        {
            Project = request.Project.Clone(), Document = request.Document.Clone(),
            ProcessEpoch = request.ProcessEpoch, OperationId = request.OperationId,
            State = CheckedProjectNetClassesState.CpcsCompleted, RequestVerified = true,
            Phase = CheckedProjectNetClassesPhase.CpcpComplete,
            RollbackState = CheckedProjectNetClassesRollbackState.CpcrsNotAttempted,
            PostCommitState = CheckedProjectNetClassesPostCommitState.CpcpsConfirmed,
            BeforeNetClasses = before, AfterNetClasses = after,
            BeforeNetClassesSha256 = CheckedProjectNetClassesContract.NetClassesDigest(before),
            AfterNetClassesSha256 = CheckedProjectNetClassesContract.NetClassesDigest(after),
            ObservedBefore = request.ExpectedState.Clone(), ObservedAfter = afterState,
            UndoRedoSupported = true
        };
        CheckedProjectNetClassesContract.ValidateResult(request, receipt, inspect: false);

        receipt.UndoRedoSupported = false;
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedProjectNetClassesContract.ValidateResult(request, receipt, inspect: false));
    }

    [TestMethod]
    public void RollbackReceiptRequiresBeforeSnapshotAndMatchingLifecycleObservation()
    {
        (CheckedProjectNetClassesTransaction request, NetClassesResponse before) = Request();
        var receipt = new CheckedProjectNetClassesReceipt
        {
            Project = request.Project.Clone(), Document = request.Document.Clone(),
            ProcessEpoch = request.ProcessEpoch, OperationId = request.OperationId,
            State = CheckedProjectNetClassesState.CpcsRolledBack, RequestVerified = true,
            Phase = CheckedProjectNetClassesPhase.CpcpRollbackConfirmed,
            RollbackState = CheckedProjectNetClassesRollbackState.CpcrsConfirmed,
            PostCommitState = CheckedProjectNetClassesPostCommitState.CpcpsNotAttempted,
            BeforeNetClasses = before,
            BeforeNetClassesSha256 = CheckedProjectNetClassesContract.NetClassesDigest(before),
            AfterNetClassesSha256 = CheckedProjectNetClassesContract.NetClassesDigest(before),
            ObservedBefore = request.ExpectedState.Clone(),
            ObservedRollback = request.ExpectedState.Clone(),
            ErrorCode = "project_net_classes_rejected", ErrorMessage = "rollback confirmed"
        };
        CheckedProjectNetClassesContract.ValidateResult(request, receipt, inspect: false);

        receipt.ObservedRollback.Revision.Sequence++;
        Assert.ThrowsExactly<AutomationException>(() =>
            CheckedProjectNetClassesContract.ValidateResult(request, receipt, inspect: false));
    }

    private static (CheckedProjectNetClassesTransaction Request, NetClassesResponse Before) Request()
    {
        string root = Path.Combine(Path.GetTempPath(), "kicad-project-netclasses-contract");
        var project = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeProject,
            Project = new() { Name = "fixture", Path = root }
        };
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb, BoardFilename = "fixture.kicad_pcb",
            Project = project.Project.Clone()
        };
        string epoch = Guid.NewGuid().ToString("D");
        string identity = Guid.NewGuid().ToString("D");
        var state = new DocumentLifecycleState
        {
            Document = document.Clone(), ProcessEpoch = epoch, NativeIdentity = identity,
            Scope = DocumentLifecycleScope.DlsPcb, ProjectSettingsIncluded = true,
            DiskBaselineChecked = true, StateSha256 = new string('a', 64),
            Revision = new() { Epoch = identity, Sequence = 4 }
        };
        foreach (string path in new[] { Path.Combine(root, "fixture.kicad_pcb"), Path.Combine(root, "fixture.kicad_pro") })
        {
            state.NativeFiles.Add(path);
            state.FileBaselines.Add(new NativeFileBaselineState
            {
                Path = path, BaselinePath = path, BaselineKnown = true, CurrentKnown = true,
                Status = NativeFileBaselineStatus.NfbsUnchanged, BaselineExists = false, CurrentExists = false
            });
        }
        NetClassesResponse before = Classes("Default", "HighSpeed");
        var request = new CheckedProjectNetClassesTransaction
        {
            Project = project, Document = document, ExpectedState = state,
            ProcessEpoch = epoch, OperationId = Guid.NewGuid().ToString("D"),
            ExpectedNetClassesSha256 = CheckedProjectNetClassesContract.NetClassesDigest(before),
            MergeMode = MapMergeMode.MmmReplace, MaximumResultBytes = 128 * 1024,
            Description = "checked project netclasses"
        };
        request.NetClasses.Add(before.NetClasses.Select(item => item.Clone()));
        return (request, before);
    }

    private static NetClassesResponse Classes(params string[] names)
    {
        var response = new NetClassesResponse();
        foreach (string name in names)
            response.NetClasses.Add(new NetClass { Name = name, Type = NetClassType.NctExplicit });
        return response;
    }
}
