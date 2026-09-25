using System.ComponentModel;
using System.Text.Json;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Releases a synchronization operation that a KiCad process left pending when it ended, and
/// continues the design in the KiCad started again for the same instance (decision nd2e75380e7f8aa7f).</summary>
[McpServerToolType]
public sealed class ExitedOperationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_design_recovery_release_exited", ReadOnly = false),
     Description("Release the XML synchronization operation that a KiCad process left pending in a design recovery record when that process ended (for example killed or crashed while applying or saving the edit), and continue the design in the KiCad started again for the same instance. Requires the absolute recoveryPath, its exact expectedRevisionToken, the pending operation's operationId (pendingPublication or pendingLayout operationId in kicad_design_recovery_plan; a paused automatic synchronization reports it too) and continuation 'resume' or 'roll-back'. The operation is released only when this server proves that exactly the KiCad process epoch holding it has ended: its own observer saw that process end, or a saved registration written on this same machine, in the same boot and process ID namespace, shows the process is gone. A registration written on another computer never counts, and neither does a KiCad that is only stopped: nothing changes (operation_exit_unproven). The release first keeps a receipt of the whole operation next to the record (its native edit and native state, the save KiCad may have been cut off in, the candidate XML and publication, and which native files that save had already replaced); the baseline, desired XML and completed synchronization receipts stay unchanged. Once KiCad for this instance runs again (kicad_instance_start, or attach a KiCad started with this instance ID), the same call reattaches the record to it and compares every sheet it loaded with the receipt: each sheet must be either the last synchronized version or exactly the operation's result; anything else is refused (released_operation_diverged) and left untouched, so a user edit is never taken for the operation's result. The sheet files' load-format version is taken from the running KiCad, not compared. The continuation is then journaled as an ordinary pending synchronization of the running KiCad (outcome resume-pending or roll-back-pending) and completes like any other: kicad_design_sync_apply with the returned continuationOperationId and requestedRecoveryRevisionToken, or automatic synchronization, which completes pending work first. 'resume' completes the released operation itself, applying only the part KiCad does not hold yet, so no edit is applied twice, and publishes its XML; it requires the XML file to still be the operation's input (released_operation_input_changed otherwise). 'roll-back' removes the operation's partial result from KiCad (only the sheets that hold it) and publishes the last synchronized design, so KiCad and the XML file return to it; the XML it replaces, including the edit that started the operation, is kept as the previous XML version. When no KiCad runs for the instance yet, only the release happens (outcome released); call again with the returned recoveryRevisionToken after starting KiCad. A repeated call reports the continuation (resume-pending, roll-back-pending, completed or rolled-back) instead of repeating it. An automatic synchronization that owns the record must be stopped first (automatic_sync_ownership_conflict)."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "recovery revision token, pending operation UUID, proven exit of the operation's process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativeCrashReleasesTheExitedOperation", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> ReleaseExited(string instanceId, string recoveryPath, string expectedRevisionToken,
        string operationId, string continuation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(recoveryPath))
                throw new AutomationException("invalid_recovery_path", "Specify the absolute recovery-record path.");
            if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty || operation.ToString("D") != operationId)
                throw new AutomationException("invalid_operation_id", "Specify the pending operation's canonical UUID.");
            var chosen = continuation switch
            {
                "resume" => ExitedOperationContinuation.Resume,
                "roll-back" => ExitedOperationContinuation.RollBack,
                _ => throw new AutomationException("invalid_continuation", "Choose continuation 'resume' or 'roll-back'.")
            };
            var result = await ExitedOperationRelease.ReleaseAsync(new DesignRecoveryStore(recoveryPath), registry, instanceId,
                expectedRevisionToken, operation, chosen, cancellationToken);
            var receipt = result.Receipt;
            var pending = result.Recovery.State.PendingPublication;
            var data = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, continuation, outcome = result.Outcome, releasedNow = result.ReleasedNow,
                releasedEpoch = receipt.ProcessEpoch,
                exit = new
                {
                    epoch = receipt.Exit.Epoch, processId = receipt.Exit.ProcessId, exitCode = receipt.Exit.ExitCode, signal = receipt.Exit.Signal,
                    signalName = receipt.Exit.SignalName, evidence = receipt.Exit.Evidence, description = receipt.Exit.Describe(),
                    observedAt = receipt.Exit.ObservedAt
                },
                receiptPath = result.ReceiptPath, nativeOperationId = receipt.NativeOperationId,
                nativeSaveOperationId = receipt.NativeSave()?.OperationId,
                replacedFiles = receipt.Files.Where(file => file.Replaced).Select(file => file.Path).ToArray(),
                unchangedFiles = receipt.Files.Where(file => !file.Replaced).Select(file => file.Path).ToArray(),
                continuedEpoch = result.ContinuedEpoch, sheetsWithOperationResult = result.SheetsWithOperationResult,
                nativeOperations = result.NativeOperations,
                continuationOperationId = result.ContinuationOperationId,
                continuationPending = pending is not null && pending.OperationId == result.ContinuationOperationId,
                requestedRecoveryRevisionToken = pending is not null && pending.OperationId == result.ContinuationOperationId
                    ? pending.RequestedRecoveryRevisionToken : null,
                recoveryRevisionToken = result.Recovery.RevisionToken,
                baselineAdvanced = false, designFileWritten = false, nextStep = result.NextStep
            });
            return new() { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            string code = error switch
            {
                AutomationException known => known.Code, NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode, _ => "design_recovery_io"
            };
            var data = JsonSerializer.SerializeToElement(new { instanceId, operationId, errorCode = code, errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }
}
