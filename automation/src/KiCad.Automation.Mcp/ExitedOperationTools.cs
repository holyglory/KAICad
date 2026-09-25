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
     Description("Release the XML synchronization operation that a KiCad process left pending in a design recovery record when that process ended (for example killed or crashed while applying or saving the edit), and continue the design in the KiCad started again for the same instance. Requires the absolute recoveryPath, its exact expectedRevisionToken, the pending operation's operationId (pendingPublication or pendingLayout operationId in kicad_design_recovery_plan; a paused automatic synchronization reports it too) and continuation 'resume' or 'roll-back'. The operation is released only when this server proves that exactly the KiCad process epoch holding it has ended: its own observer saw that process end, or a saved registration written on this same machine, in the same boot and process ID namespace, shows the process is gone. A registration written on another computer never counts, and neither does a KiCad that is only stopped: nothing changes (operation_exit_unproven). The release first keeps a receipt of the whole operation next to the record (its native edit and native state, the save KiCad may have been cut off in, the candidate XML and publication, and which native files that save had already replaced); the baseline, desired XML and completed synchronization receipts stay unchanged. Once KiCad for this instance runs again (kicad_instance_start, or attach a KiCad started with this instance ID), the same call reattaches the record to it and compares every sheet it loaded with the receipt (operationSheets are the sheets the operation changes). Each of the operation's own sheets must be either the last synchronized version or exactly the operation's result (sheetsWithOperationResult); other edits there are refused (released_operation_sheets_edited), since they cannot be told from the partial result, and a result no save of the operation put there is refused too (released_operation_diverged). An operation that had no final XML candidate yet (it was still resolving its native layout) has no result to compare with, so any change on its sheets is refused the same way, as its partial result or other edits, which cannot be told apart. The way out the refusal names: undo those edits in KiCad, or close the schematic without saving and open it again, call again, and make the edits again after the continuation. Sheets the operation does not change may hold other edits made in the KiCad started again (sheetsWithOtherEdits): the continuation leaves them exactly as KiCad holds them, saves them with its sheets and publishes them in its XML, as an ordinary synchronization would, when they are notes, text boxes, drawings, images, tables, or title-block and page-setting changes; components, wires, labels, sheets and other edits that must first be reconciled with the XML design are refused with the same way out (released_operation_edits_not_carried). The sheet files' load-format version is taken from the running KiCad, not compared. The continuation is then journaled as an ordinary pending synchronization of the running KiCad (outcome resume-pending or roll-back-pending) and completes like any other: kicad_design_sync_apply with the returned continuationOperationId and requestedRecoveryRevisionToken, or automatic synchronization, which completes pending work first. 'resume' completes the released operation itself, applying only the part KiCad does not hold yet, so no edit is applied twice, and publishes its XML; it requires the XML file to still be the operation's input (released_operation_input_changed otherwise, which says whether roll-back works instead). 'roll-back' removes the operation's partial result from KiCad (only the sheets that hold it) and publishes the last synchronized design, so KiCad and the XML file return to it; the XML it replaces, including the edit that started the operation, is kept as the previous XML version. When no KiCad runs for the instance yet, only the release happens (outcome released); call again with the returned recoveryRevisionToken after starting KiCad. Until the released operation is continued this way, the record stays on the ended KiCad's session and cannot be attached to another one: kicad_design_recovery_reattach is refused (released_operation_requires_continuation), because the KiCad started again may hold part of the operation's result and the next synchronization would take it for a user edit. The continuation attaches the record and journals the continuation in one step. 'resume' refuses an operation whose XML publication had already started when KiCad ended (released_publication_started; its message says when roll-back works instead), and 'roll-back' refuses an XML file that is not the version the operation started from (design_file_changed), since it would overwrite that version, naming where a kept copy of it is. Every refusal after the release, including one in the same call that released the operation, keeps the release: the error reply carries releasedNow, releasedEpoch, receiptPath and the record's current recoveryRevisionToken to call again with. A repeated call reports the continuation (resume-pending, roll-back-pending, completed, rolled-back or replan) instead of repeating it; while the continuation waits, its operationSheets, sheetsWithOperationResult and sheetsWithOtherEdits are those it was planned from, or null (unknown) once the recovery record's observation of the running KiCad has moved on. The three sheet lists are null when no comparison was made: outcome released (no KiCad runs yet), and a repeated call that reports completed, rolled-back or replan, which does not compare the running KiCad again. An automatic synchronization that owns the record must be stopped first (automatic_sync_ownership_conflict)."),
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
                continuedEpoch = result.ContinuedEpoch, operationSheets = result.OperationSheets,
                sheetsWithOperationResult = result.SheetsWithOperationResult, sheetsWithOtherEdits = result.SheetsWithOtherEdits,
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
        catch (ExitedOperationRefusal refusal)
        {
            // Refused after the release: the operation stays released, and the reply says where its receipt is and which
            // recovery revision token to call again with.
            var data = JsonSerializer.SerializeToElement(new
            {
                instanceId, operationId, errorCode = Code(refusal.Refusal), errorMessage = refusal.Message, releasedNow = refusal.ReleasedNow,
                releasedEpoch = refusal.ReleasedEpoch, receiptPath = refusal.ReceiptPath, recoveryRevisionToken = refusal.RecoveryRevisionToken
            });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            var data = JsonSerializer.SerializeToElement(new { instanceId, operationId, errorCode = Code(error), errorMessage = error.Message });
            return new() { IsError = true, Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
        }
    }

    private static string Code(Exception error) => error switch
    {
        AutomationException known => known.Code, NativeApiException native => "native_status_" + native.Status,
        NngException transport => "transport_status_" + transport.ErrorCode, _ => "design_recovery_io"
    };
}
