using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicErcExecutionContractTests
{
    [TestMethod]
    public void CompletedReceiptCanBeReplayedOnlyForItsExactOperationAndRevision()
    {
        var fixture = Fixture();
        Assert.AreNotEqual(fixture.State.NativeIdentity, fixture.State.Revision.Epoch,
            "Native document identity and revision epoch are independent native identities.");
        SchematicErcExecutionContract.ValidateRequest(fixture.Request);
        SchematicErcExecutionContract.ValidateReceipt(fixture.Request, fixture.Receipt);

        // A reconnect/retry reads the immutable native receipt and does not run ERC again.
        SchematicErcExecutionContract.ValidateReceipt(fixture.Receipt.Clone(), fixture.Document,
            fixture.State, fixture.OperationId, fixture.ProcessEpoch);

        var wrongOperation = fixture.Receipt.Clone();
        wrongOperation.LegacyResult.OperationId = "55555555-5555-5555-5555-555555555555";
        RejectReceipt(wrongOperation, fixture, "operation");

        var staleRequest = fixture.Request.Clone();
        staleRequest.ExpectedState.Revision.Sequence++;
        RejectReceipt(staleRequest, fixture.Receipt, "revision");
    }

    [TestMethod]
    public void LegacyRunAcceptsReceiptWithoutTypedAdmissionButTypedExecutionRejectsIt()
    {
        var fixture = Fixture();
        var legacyRequest = new NativeCapabilitySchematicRunErc
        {
            Document = fixture.Document.Clone(),
            OperationId = fixture.OperationId,
            ProcessEpoch = fixture.ProcessEpoch,
            ExpectedRevision = fixture.State.Revision.Clone(),
            ShowAllErrors = true
        };
        var legacyReceipt = fixture.Receipt.Clone();
        legacyReceipt.AdmissionVerified = false;

        SchematicErcExecutionContract.ValidateReceipt(legacyRequest, legacyReceipt);
        SchematicErcExecutionContract.RequireSuccessful(legacyReceipt);

        var typedError = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.ValidateReceipt(fixture.Request, legacyReceipt));
        Assert.AreEqual("invalid_schematic_erc_receipt", typedError.Code);
    }

    [TestMethod]
    public void LegacyRunRejectsReceiptForAnotherRevision()
    {
        var fixture = Fixture();
        var legacyRequest = new NativeCapabilitySchematicRunErc
        {
            Document = fixture.Document.Clone(),
            OperationId = fixture.OperationId,
            ProcessEpoch = fixture.ProcessEpoch,
            ExpectedRevision = fixture.State.Revision.Clone()
        };
        legacyRequest.ExpectedRevision.Sequence++;

        var error = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.ValidateReceipt(legacyRequest, fixture.Receipt));
        Assert.AreEqual("invalid_schematic_erc_receipt", error.Code);
    }

    [TestMethod]
    public void FailedAndIndeterminateTerminalsRemainRecoverableButNeverSuccessful()
    {
        var fixture = Fixture();

        var failed = fixture.Receipt.Clone();
        failed.State = NativeErcOperationState.NeosFailed;
        failed.ExecutionStarted = false;
        failed.MarkersCaptured = false;
        failed.CapturedMarkers = null;
        failed.AfterState = null;
        failed.LegacyResult.ObservedRevision = null;
        failed.LegacyResult.Completed = false;
        failed.LegacyResult.ErrorCode = "erc_library_unavailable";
        failed.LegacyResult.ErrorMessage = "fixture failure";
        SchematicErcExecutionContract.ValidateReceipt(fixture.Request, failed);
        var failedError = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.RequireSuccessful(failed));
        Assert.AreEqual("erc_execution_failed", failedError.Code);

        var indeterminate = fixture.Receipt.Clone();
        indeterminate.State = NativeErcOperationState.NeosIndeterminate;
        indeterminate.ExecutionStarted = true;
        indeterminate.MarkersCaptured = false;
        indeterminate.CapturedMarkers = null;
        indeterminate.AfterState = null;
        indeterminate.LegacyResult.Completed = false;
        indeterminate.LegacyResult.ErrorCode = "erc_execution_indeterminate";
        indeterminate.LegacyResult.ErrorMessage = "the editor stopped responding";
        SchematicErcExecutionContract.ValidateReceipt(fixture.Request, indeterminate);
        var indeterminateError = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.RequireSuccessful(indeterminate));
        Assert.AreEqual("erc_execution_indeterminate", indeterminateError.Code);
    }

    [TestMethod]
    public void PreExecutionAdmittedOrFailedReceiptCannotClaimAnAfterState()
    {
        var fixture = Fixture();

        var admitted = fixture.Receipt.Clone();
        admitted.State = NativeErcOperationState.NeosAdmitted;
        admitted.ExecutionStarted = false;
        admitted.MarkersCaptured = false;
        admitted.CapturedMarkers = null;
        admitted.AfterState = fixture.State.Clone();
        admitted.LegacyResult.ObservedRevision = fixture.State.Revision.Clone();
        admitted.LegacyResult.Completed = false;
        admitted.LegacyResult.ErrorCode = "";
        admitted.LegacyResult.ErrorMessage = "";
        RejectReceipt(fixture.Request, admitted, "admitted after-state");

        var failed = admitted.Clone();
        failed.State = NativeErcOperationState.NeosFailed;
        failed.LegacyResult.ErrorCode = "erc_library_unavailable";
        failed.LegacyResult.ErrorMessage = "fixture failure";
        RejectReceipt(fixture.Request, failed, "failed after-state");
    }

    [TestMethod]
    public async Task CancellationBeforeDispatchIsPropagatedWithoutAFalseReceipt()
    {
        var tool = new SchematicErcTools(null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.Execute("unused", null!, cancellation.Token));
    }

    [TestMethod]
    public void ReceiptRejectsChangedNativeEpochAndMalformedTerminalFlags()
    {
        var fixture = Fixture();

        var staleEpoch = fixture.Receipt.Clone();
        staleEpoch.LegacyResult.ProcessEpoch = "66666666-6666-6666-6666-666666666666";
        RejectReceipt(staleEpoch, fixture, "epoch");

        var foreignRevision = fixture.Receipt.Clone();
        foreignRevision.BeforeState.Revision.Epoch = "66666666-6666-6666-6666-666666666666";
        foreignRevision.AfterState.Revision.Epoch = foreignRevision.BeforeState.Revision.Epoch;
        foreignRevision.LegacyResult.CheckedRevision = foreignRevision.BeforeState.Revision.Clone();
        foreignRevision.LegacyResult.ObservedRevision = foreignRevision.AfterState.Revision.Clone();
        var revisionError = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.ValidateReceipt(foreignRevision, fixture.Document,
                fixture.State, fixture.OperationId, fixture.ProcessEpoch));
        Assert.AreEqual("invalid_schematic_erc_receipt", revisionError.Code);

        var missingMarkers = fixture.Receipt.Clone();
        missingMarkers.MarkersCaptured = false;
        RejectReceipt(missingMarkers, fixture, "marker");
    }

    private static void RejectReceipt(NativeErcOperationReceipt receipt,
        (DocumentSpecifier Document, DocumentLifecycleState State, string ProcessEpoch,
            string OperationId, NativeCapabilitySchematicExecuteErc Request,
            NativeErcOperationReceipt Receipt) fixture, string reason)
    {
        var error = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.ValidateReceipt(receipt, fixture.Document,
                fixture.State, fixture.OperationId, fixture.ProcessEpoch));
        Assert.AreEqual("invalid_schematic_erc_receipt", error.Code, reason);
    }

    private static void RejectReceipt(NativeCapabilitySchematicExecuteErc request,
        NativeErcOperationReceipt receipt, string reason)
    {
        var error = Assert.ThrowsExactly<AutomationException>(() =>
            SchematicErcExecutionContract.ValidateReceipt(request, receipt));
        Assert.AreEqual("invalid_schematic_erc_receipt", error.Code, reason);
    }

    private static (DocumentSpecifier Document, DocumentLifecycleState State,
        string ProcessEpoch, string OperationId, NativeCapabilitySchematicExecuteErc Request,
        NativeErcOperationReceipt Receipt) Fixture()
    {
        const string processEpoch = "22222222-2222-2222-2222-222222222222";
        const string operationId = "33333333-3333-3333-3333-333333333333";
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypeSchematic,
            Project = new() { Name = "erc-contract", Path = "/tmp/kicad-erc-contract" },
            SheetPath = new()
        };
        document.SheetPath.Path.Add(new KIID { Value = "11111111-1111-1111-1111-111111111111" });
        var state = new DocumentLifecycleState
        {
            Document = document.Clone(),
            Scope = DocumentLifecycleScope.DlsSchematicHierarchy,
            ProcessEpoch = processEpoch,
            NativeIdentity = "44444444-4444-4444-4444-444444444444",
            Revision = new() { Epoch = "55555555-5555-5555-5555-555555555555", Sequence = 7 },
            StateSha256 = new string('a', 64)
        };
        var request = new NativeCapabilitySchematicExecuteErc
        {
            OperationId = operationId,
            ExpectedState = state.Clone(),
            ShowAllErrors = true
        };
        var legacy = new NativeCapabilitySchematicRunErcResponse
        {
            Document = document.Clone(), OperationId = operationId, ProcessEpoch = processEpoch,
            CheckedRevision = state.Revision.Clone(), ObservedRevision = state.Revision.Clone(),
            Completed = true, MarkerCount = 0
        };
        var receipt = new NativeErcOperationReceipt
        {
            LegacyResult = legacy,
            BeforeState = state.Clone(),
            AfterState = state.Clone(),
            CapturedMarkers = new SchematicTrackingErcMarkers(),
            State = NativeErcOperationState.NeosCompleted,
            AdmissionVerified = true,
            ExecutionStarted = true,
            MarkersCaptured = true
        };
        return (document, state, processEpoch, operationId, request, receipt);
    }
}
