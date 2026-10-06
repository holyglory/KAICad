using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>
/// The identity and receipt boundary for a future checked symbol/footprint
/// library transaction.  This is a managed/native contract only: it does not
/// dispatch an IPC request, acquire a native lock or write a library.
/// </summary>
public enum LibraryTransactionState
{
    Rejected = 0,
    Admitted = 1,
    Executing = 2,
    CommittedPostverified = 3,
    RolledBackVerified = 4,
    Indeterminate = 5,
    RollbackUnknown = 6
}

/// <summary>One process-owned document revision, separate from the process epoch.</summary>
public sealed record LibraryDocumentRevision(string Epoch, ulong Sequence);

/// <summary>
/// A complete file baseline belonging to a library state.  Cursor-only or
/// partially hashed observations are intentionally not admissible.
/// </summary>
public sealed record LibraryFileBaseline(
    string Path,
    bool Exists,
    string Sha256,
    ulong Bytes,
    bool Known = true,
    string Cursor = "");

/// <summary>
/// The complete native observation required before a library edit can be
/// admitted.  FullStateSha256 is the native digest of all provider state;
/// FileBaselines independently prove the complete persisted dependency set.
/// </summary>
public sealed record LibraryLifecycleState(
    DocumentSpecifier Document,
    string ProcessEpoch,
    LibraryDocumentRevision Revision,
    string FullStateSha256,
    IReadOnlyList<LibraryFileBaseline> FileBaselines,
    bool CompleteState = true);

/// <summary>
/// Canonical request identity for one library operation.  OperationPayload is
/// an already-canonical UTF-8 payload owned by the caller; it is hashed as-is
/// and is never interpreted or sent by this contract.
/// </summary>
public sealed record LibraryTransactionRequest(
    string OperationId,
    DocumentSpecifier Document,
    string ProcessEpoch,
    LibraryLifecycleState ExpectedState,
    string Operation,
    string OperationPayload,
    string RequestFingerprintSha256)
{
    public LibraryDocumentRevision ExpectedRevision => ExpectedState.Revision;
    public string ExpectedStateSha256 => ExpectedState.FullStateSha256;
}

/// <summary>
/// Immutable evidence for one library transaction.  The request is retained
/// in full so replay can be accepted only for the exact canonical fingerprint.
/// </summary>
public sealed record LibraryTransactionReceipt(
    LibraryTransactionRequest Request,
    LibraryTransactionState State,
    bool RequestVerified,
    bool NativeWriteStarted,
    bool CancellationObservedAfterWrite,
    LibraryLifecycleState? ObservedBefore,
    LibraryLifecycleState? ObservedAfter,
    LibraryLifecycleState? ObservedRollback,
    bool Postverified,
    bool RollbackAttempted,
    bool RollbackVerified,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public string OperationId => Request.OperationId;
    public DocumentSpecifier Document => Request.Document;
    public string ProcessEpoch => Request.ProcessEpoch;
    public string RequestFingerprintSha256 => Request.RequestFingerprintSha256;
}

/// <summary>
/// Pure validation for library transaction identity, admission and receipts.
/// It is deliberately separate from the current read-only snapshot tool and
/// never makes a native mutation available by itself.
/// </summary>
public static class LibraryTransactionContract
{
    private const string FingerprintDomain = "kicad-library-transaction-v1";

    public static void ValidateRequest(LibraryTransactionRequest request)
    {
        if (request is null)
            throw Invalid("A library transaction request is required.");

        RequireUuid(request.OperationId, "operation ID");
        RequireUuid(request.ProcessEpoch, "process epoch");
        ValidateDocument(request.Document);
        if (request.ExpectedState is null)
            throw Invalid("A library transaction requires one complete expected state.");
        ValidateState(request.ExpectedState);
        if (!SameDocument(request.Document, request.ExpectedState.Document))
            throw Invalid("The request document and expected library state identify different documents.");
        if (!string.Equals(request.ProcessEpoch, request.ExpectedState.ProcessEpoch, StringComparison.Ordinal))
            throw Invalid("The request process epoch and expected library state differ.");
        if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.IndexOf('\0') >= 0)
            throw Invalid("A library transaction operation name is required.");
        if (request.OperationPayload is null || request.OperationPayload.IndexOf('\0') >= 0)
            throw Invalid("A library transaction payload must be canonical text without NUL characters.");
        RequireDigest(request.RequestFingerprintSha256, "request fingerprint");
        string expectedFingerprint = ComputeFingerprint(request);
        if (!string.Equals(request.RequestFingerprintSha256, expectedFingerprint, StringComparison.Ordinal))
            throw Invalid("The request fingerprint does not match the canonical library transaction identity.");
    }

    /// <summary>
    /// Proves that the native observation still is the request's exact
    /// document/process/revision/full-state checkpoint.  A stale revision,
    /// foreign process or partial file cursor is refused before any writer can
    /// be called.
    /// </summary>
    public static void ValidateAdmission(LibraryTransactionRequest request, LibraryLifecycleState observed)
    {
        ValidateRequest(request);
        ValidateState(observed);

        if (!SameDocument(request.Document, observed.Document))
            throw new AutomationException("foreign_library_document",
                "The observed library belongs to another project or library document.");
        if (!string.Equals(request.ProcessEpoch, observed.ProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("foreign_library_process",
                "The observed library belongs to another native process epoch.");
        if (!SameState(request.ExpectedState, observed))
            throw new AutomationException("stale_library_state",
                "The library revision, full-state digest or dependency baselines changed after observation.");
    }

    /// <summary>Validates a retained receipt without dispatching or retrying it.</summary>
    public static void ValidateReceipt(LibraryTransactionRequest request,
        LibraryTransactionReceipt receipt, bool inspect = false)
    {
        ValidateRequest(request);
        if (receipt is null)
            throw Invalid("A library transaction receipt is required.");
        if (!SameRequest(request, receipt.Request))
            throw new AutomationException("library_receipt_mismatch",
                "The retained library receipt does not identify the exact request and fingerprint.");
        if (!Enum.IsDefined(receipt.State))
            throw Invalid("The library receipt has an unknown transaction state.");
        if (!receipt.RequestVerified)
            throw Invalid("A library receipt must verify its exact request identity.");
        if (HasAnyError(receipt) && !HasError(receipt))
            throw Invalid("A library receipt must carry both an error code and message, or neither.");
        if (receipt.CancellationObservedAfterWrite && !receipt.NativeWriteStarted)
            throw Invalid("Cancellation-after-write evidence requires a native write-start observation.");

        if (receipt.ObservedBefore is not null)
        {
            ValidateState(receipt.ObservedBefore);
            if (!SameState(request.ExpectedState, receipt.ObservedBefore))
                throw new AutomationException("stale_library_state",
                    "The receipt before-state does not match the request's complete expected state.");
        }

        if (receipt.ObservedAfter is not null) ValidateState(receipt.ObservedAfter);
        if (receipt.ObservedRollback is not null) ValidateState(receipt.ObservedRollback);

        switch (receipt.State)
        {
            case LibraryTransactionState.Rejected:
                RequireRejected(receipt);
                break;
            case LibraryTransactionState.Admitted:
                RequireAdmitted(receipt);
                break;
            case LibraryTransactionState.Executing:
                RequireExecuting(receipt);
                break;
            case LibraryTransactionState.CommittedPostverified:
                RequireCommitted(request, receipt);
                break;
            case LibraryTransactionState.RolledBackVerified:
                RequireRolledBack(request, receipt);
                break;
            case LibraryTransactionState.Indeterminate:
                RequireIndeterminate(receipt);
                break;
            case LibraryTransactionState.RollbackUnknown:
                RequireRollbackUnknown(receipt);
                break;
            default:
                throw Invalid("The library receipt has an unsupported transaction state.");
        }

        // An unresolved receipt is still a valid receipt.  The caller must
        // inspect it before deciding whether a new operation is safe, but
        // validation itself must preserve the indeterminate evidence.
        _ = inspect;
    }

    /// <summary>
    /// A request ID can be replayed only with its exact canonical fingerprint.
    /// The returned receipt is evidence for inspection; this method never
    /// executes the request again.
    /// </summary>
    public static void ValidateReplay(LibraryTransactionRequest request,
        LibraryTransactionReceipt retainedReceipt)
    {
        if (request is null || retainedReceipt is null)
            throw Invalid("An exact request and retained receipt are required for replay.");

        RequireUuid(request.OperationId, "operation ID");
        if (retainedReceipt.OperationId != request.OperationId)
            throw new AutomationException("operation_id_mismatch",
                "The retained receipt belongs to another operation ID.");
        if (!string.Equals(retainedReceipt.RequestFingerprintSha256,
                request.RequestFingerprintSha256, StringComparison.Ordinal))
            throw new AutomationException("operation_id_conflict",
                "The operation ID is already bound to a different request fingerprint.");

        ValidateReceipt(request, retainedReceipt, inspect: true);
    }

    /// <summary>Computes the canonical identity digest used by request and receipt replay.</summary>
    public static string ComputeFingerprint(LibraryTransactionRequest request)
    {
        if (request is null)
            throw Invalid("A library transaction request is required to compute its fingerprint.");

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FingerprintDomain);
            WriteString(writer, request.OperationId);
            WriteString(writer, request.ProcessEpoch);
            WriteBytes(writer, CanonicalDocumentBytes(request.Document));
            if (request.ExpectedState is null)
                throw Invalid("A library transaction requires one complete expected state.");
            WriteString(writer, request.ExpectedState.Revision.Epoch);
            writer.Write(request.ExpectedState.Revision.Sequence);
            WriteString(writer, request.ExpectedState.FullStateSha256);
            WriteBaselines(writer, request.ExpectedState.FileBaselines);
            WriteString(writer, request.Operation);
            WriteString(writer, request.OperationPayload);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void RequireRejected(LibraryTransactionReceipt receipt)
    {
        if (receipt.NativeWriteStarted || receipt.ObservedAfter is not null || receipt.ObservedRollback is not null
            || receipt.Postverified || receipt.RollbackAttempted || receipt.RollbackVerified
            || receipt.CancellationObservedAfterWrite || !HasError(receipt))
            throw Invalid("A rejected library transaction must prove that no native write started and explain the refusal.");
    }

    private static void RequireAdmitted(LibraryTransactionReceipt receipt)
    {
        if (receipt.NativeWriteStarted || receipt.ObservedAfter is not null || receipt.ObservedRollback is not null
            || receipt.Postverified || receipt.RollbackAttempted || receipt.RollbackVerified
            || receipt.CancellationObservedAfterWrite || HasError(receipt))
            throw Invalid("An admitted library transaction cannot claim native execution or a terminal result.");
    }

    private static void RequireExecuting(LibraryTransactionReceipt receipt)
    {
        if (receipt.ObservedAfter is not null || receipt.ObservedRollback is not null || receipt.Postverified
            || receipt.RollbackAttempted || receipt.RollbackVerified || receipt.CancellationObservedAfterWrite
            || HasError(receipt))
            throw Invalid("An executing library transaction cannot claim terminal, rollback or cancellation evidence.");
    }

    private static void RequireCommitted(LibraryTransactionRequest request, LibraryTransactionReceipt receipt)
    {
        LibraryLifecycleState after = receipt.ObservedAfter
            ?? throw Invalid("A committed library receipt requires a post-state observation.");
        if (!receipt.NativeWriteStarted || receipt.ObservedBefore is null || !receipt.Postverified
            || receipt.ObservedRollback is not null || receipt.RollbackAttempted || receipt.RollbackVerified
            || receipt.CancellationObservedAfterWrite || HasError(receipt)
            || !SameDocument(request.Document, after.Document)
            || !string.Equals(after.ProcessEpoch, request.ProcessEpoch, StringComparison.Ordinal)
            || !string.Equals(after.Revision.Epoch, request.ExpectedRevision.Epoch, StringComparison.Ordinal)
            || after.Revision.Sequence <= request.ExpectedRevision.Sequence)
            throw Invalid("A committed library receipt requires exact before-state and postverified advanced revision evidence.");
    }

    private static void RequireRolledBack(LibraryTransactionRequest request, LibraryTransactionReceipt receipt)
    {
        LibraryLifecycleState rollback = receipt.ObservedRollback
            ?? throw Invalid("A rolled-back library receipt requires a rollback observation.");
        if (!receipt.NativeWriteStarted || receipt.ObservedBefore is null || !receipt.RollbackAttempted
            || !receipt.RollbackVerified || receipt.ObservedAfter is not null || receipt.Postverified
            || receipt.CancellationObservedAfterWrite || !SameState(request.ExpectedState, rollback)
            || !HasError(receipt))
            throw Invalid("A rolled-back library receipt requires verified restoration of the exact before-state.");
    }

    private static void RequireIndeterminate(LibraryTransactionReceipt receipt)
    {
        if (!receipt.NativeWriteStarted || receipt.Postverified || receipt.RollbackVerified
            || !HasError(receipt))
            throw Invalid("An indeterminate library receipt must prove that native execution began and explain the unknown result.");
    }

    private static void RequireRollbackUnknown(LibraryTransactionReceipt receipt)
    {
        if (!receipt.NativeWriteStarted || !receipt.RollbackAttempted || receipt.RollbackVerified
            || receipt.Postverified || !HasError(receipt))
            throw Invalid("A rollback-unknown library receipt must prove a started write, attempted rollback and unresolved cleanup.");
    }

    private static void ValidateState(LibraryLifecycleState state)
    {
        if (state is null || state.Document is null || state.Revision is null)
            throw Invalid("A complete library lifecycle state is required.");
        ValidateDocument(state.Document);
        RequireUuid(state.ProcessEpoch, "process epoch");
        RequireUuid(state.Revision.Epoch, "document revision epoch");
        RequireDigest(state.FullStateSha256, "full-state digest");
        if (!state.CompleteState || state.FileBaselines is null || state.FileBaselines.Count == 0)
            throw new AutomationException("partial_library_state",
                "A library state must include the complete provider dependency set.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryFileBaseline baseline in state.FileBaselines)
        {
            if (baseline is null || !baseline.Known || !string.IsNullOrEmpty(baseline.Cursor))
                throw new AutomationException("partial_library_state",
                    "A cursor-only or unknown library dependency baseline cannot be admitted.");
            string path = CanonicalPath(baseline.Path);
            if (!paths.Add(path))
                throw new AutomationException("partial_library_state",
                    "Library dependency baselines must identify each canonical file exactly once.");
            if (baseline.Exists)
            {
                if (baseline.Sha256 is null || baseline.Sha256.Length != 64
                    || !baseline.Sha256.All(char.IsAsciiHexDigitLower))
                    throw new AutomationException("partial_library_state",
                        "Every existing library dependency needs a complete lowercase SHA-256 digest.");
            }
            else if (!string.IsNullOrEmpty(baseline.Sha256) || baseline.Bytes != 0)
            {
                throw new AutomationException("partial_library_state",
                    "An absent library dependency cannot carry a partial hash or byte count.");
            }
        }
    }

    private static void ValidateDocument(DocumentSpecifier document)
    {
        if (document is null || document.Type is not (DocumentType.DoctypeSymbol or DocumentType.DoctypeFootprint)
            || document.Project is null || document.LibId is null
            || string.IsNullOrWhiteSpace(document.Project.Name) || document.Project.Name.IndexOf('\0') >= 0
            || string.IsNullOrWhiteSpace(document.Project.Path) || document.Project.Path.IndexOf('\0') >= 0
            || string.IsNullOrWhiteSpace(document.LibId.LibraryNickname) || document.LibId.LibraryNickname.IndexOf('\0') >= 0
            || string.IsNullOrWhiteSpace(document.LibId.EntryName) || document.LibId.EntryName.IndexOf('\0') >= 0)
            throw Invalid("A library transaction requires one project-bound symbol or footprint document.");
        _ = CanonicalPath(document.Project.Path);
    }

    private static bool SameRequest(LibraryTransactionRequest left, LibraryTransactionRequest right) =>
        left is not null && right is not null
        && left.OperationId == right.OperationId
        && left.ProcessEpoch == right.ProcessEpoch
        && SameDocument(left.Document, right.Document)
        && SameState(left.ExpectedState, right.ExpectedState)
        && left.Operation == right.Operation
        && left.OperationPayload == right.OperationPayload
        && left.RequestFingerprintSha256 == right.RequestFingerprintSha256;

    private static bool SameState(LibraryLifecycleState left, LibraryLifecycleState right)
    {
        if (left is null || right is null || !SameDocument(left.Document, right.Document)
            || left.ProcessEpoch != right.ProcessEpoch || left.Revision != right.Revision
            || left.FullStateSha256 != right.FullStateSha256 || left.CompleteState != right.CompleteState
            || left.FileBaselines.Count != right.FileBaselines.Count)
            return false;
        var aBaselines = left.FileBaselines.OrderBy(baseline => CanonicalPath(baseline.Path), StringComparer.Ordinal).ToArray();
        var bBaselines = right.FileBaselines.OrderBy(baseline => CanonicalPath(baseline.Path), StringComparer.Ordinal).ToArray();
        for (int index = 0; index < aBaselines.Length; index++)
        {
            LibraryFileBaseline a = aBaselines[index];
            LibraryFileBaseline b = bBaselines[index];
            if (CanonicalPath(a.Path) != CanonicalPath(b.Path) || a.Exists != b.Exists || a.Sha256 != b.Sha256
                || a.Bytes != b.Bytes || a.Known != b.Known || a.Cursor != b.Cursor)
                return false;
        }
        return true;
    }

    private static bool SameDocument(DocumentSpecifier left, DocumentSpecifier right) =>
        left is not null && right is not null
        && CanonicalDocumentBytes(left).AsSpan().SequenceEqual(CanonicalDocumentBytes(right));

    private static byte[] CanonicalDocumentBytes(DocumentSpecifier document)
    {
        ValidateDocument(document);
        var canonical = document.Clone();
        canonical.Project.Path = CanonicalPath(canonical.Project.Path);
        return canonical.ToByteArray();
    }

    private static void WriteBaselines(BinaryWriter writer, IReadOnlyList<LibraryFileBaseline> baselines)
    {
        var ordered = baselines.OrderBy(baseline => CanonicalPath(baseline.Path), StringComparer.Ordinal);
        writer.Write(baselines.Count);
        foreach (LibraryFileBaseline baseline in ordered)
        {
            WriteString(writer, CanonicalPath(baseline.Path));
            writer.Write(baseline.Exists);
            WriteString(writer, baseline.Sha256);
            writer.Write(baseline.Bytes);
            writer.Write(baseline.Known);
            WriteString(writer, baseline.Cursor);
        }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value is null) throw Invalid("Canonical request fields cannot be null.");
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool HasError(LibraryTransactionReceipt receipt) =>
        !string.IsNullOrWhiteSpace(receipt.ErrorCode) && !string.IsNullOrWhiteSpace(receipt.ErrorMessage);

    private static bool HasAnyError(LibraryTransactionReceipt receipt) =>
        !string.IsNullOrWhiteSpace(receipt.ErrorCode) || !string.IsNullOrWhiteSpace(receipt.ErrorMessage);

    private static string CanonicalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(value))
            throw new AutomationException("partial_library_state", "Library dependency paths must be absolute and NUL-free.");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new AutomationException("partial_library_state", "Library dependency paths must be canonical absolute paths."); }
    }

    private static void RequireUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty
            || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
            throw Invalid($"A canonical nonempty {name} is required.");
    }

    private static void RequireDigest(string value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigitLower))
            throw Invalid($"A lowercase SHA-256 {name} is required.");
    }

    private static AutomationException Invalid(string message) => new("invalid_library_transaction", message);
}
