using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Protocol;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// Full lifecycle observations are copied into the native request.  A digest or
// revision alone is insufficient admission evidence for a retained operation.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DocumentLifecycleInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private NativeDocumentRevisionInput? revision;
    private string? nativeIdentity;
    private string? stateSha256;
    private DocumentLifecycleScopeInput scope;
    private bool nativeContentDirty;
    private bool projectSettingsIncluded;
    private bool completeChangeTracking;
    private bool diskBaselineChecked;
    private List<string>? nativeFiles;
    private List<NativeFileBaselineInput?>? fileBaselines;
    private string? processEpoch;
    private string? cleanCheckpointSha256;
    private string? saveStableStateSha256;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public NativeDocumentRevisionInput? Revision { get => revision; set => revision = BindOnce(value); }
    public string? NativeIdentity { get => nativeIdentity; set => nativeIdentity = BindOnce(value); }
    public string? StateSha256 { get => stateSha256; set => stateSha256 = BindOnce(value); }
    public DocumentLifecycleScopeInput Scope { get => scope; set => scope = BindOnce(value); }
    public bool NativeContentDirty { get => nativeContentDirty; set => nativeContentDirty = BindOnce(value); }
    public bool ProjectSettingsIncluded { get => projectSettingsIncluded; set => projectSettingsIncluded = BindOnce(value); }
    public bool CompleteChangeTracking { get => completeChangeTracking; set => completeChangeTracking = BindOnce(value); }
    public bool DiskBaselineChecked { get => diskBaselineChecked; set => diskBaselineChecked = BindOnce(value); }
    public List<string>? NativeFiles { get => nativeFiles; set => nativeFiles = BindOnce(value); }
    public List<NativeFileBaselineInput?>? FileBaselines { get => fileBaselines; set => fileBaselines = BindOnce(value); }
    public string? ProcessEpoch { get => processEpoch; set => processEpoch = BindOnce(value); }
    public string? CleanCheckpointSha256 { get => cleanCheckpointSha256; set => cleanCheckpointSha256 = BindOnce(value); }
    public string? SaveStableStateSha256 { get => saveStableStateSha256; set => saveStableStateSha256 = BindOnce(value); }

    internal static DocumentLifecycleState ToNative(DocumentLifecycleInput? input)
    {
        if (input?.Document is null || input.Revision is null || string.IsNullOrWhiteSpace(input.ProcessEpoch)
            || !Guid.TryParseExact(input.ProcessEpoch, "D", out var process) || process == Guid.Empty)
            throw Invalid("A complete lifecycle observation with a process epoch and revision is required.");
        DocumentSpecifier document = NativeDocumentInput.ToNative(input.Document);
        if (!Guid.TryParseExact(input.Revision.Epoch, "D", out var revisionEpoch) || revisionEpoch == Guid.Empty)
            throw Invalid("The lifecycle revision must carry a nonempty native document epoch.");
        if (!Guid.TryParseExact(input.NativeIdentity, "D", out var identity) || identity == Guid.Empty)
            throw Invalid("A nonempty native identity UUID is required.");
        ValidateDigest(input.StateSha256, "stateSha256", required: true);
        ValidateDigest(input.CleanCheckpointSha256, "cleanCheckpointSha256", required: false);
        ValidateDigest(input.SaveStableStateSha256, "saveStableStateSha256", required: false);

        if (!Enum.IsDefined(input.Scope) || input.Scope == DocumentLifecycleScopeInput.Unknown)
            throw Invalid("A defined lifecycle scope is required.");
        var expectedScope = document.Type == DocumentType.DoctypeSchematic
            ? DocumentLifecycleScopeInput.SchematicHierarchy : DocumentLifecycleScopeInput.Pcb;
        if (input.Scope != expectedScope)
            throw Invalid("The lifecycle scope does not match the document type.");
        if (input.NativeFiles is null || input.FileBaselines is null
            || input.NativeFiles.Count == 0 || input.NativeFiles.Count != input.FileBaselines.Count
            || input.NativeFiles.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || input.NativeFiles.Count(p => string.Equals(p, path, StringComparison.Ordinal)) != 1))
            throw Invalid("Native files and file baselines must be complete, absolute and unique.");

        var result = new DocumentLifecycleState
        {
            Document = document,
            Revision = new DocumentRevision { Epoch = input.Revision.Epoch, Sequence = input.Revision.Sequence },
            NativeIdentity = input.NativeIdentity,
            StateSha256 = input.StateSha256,
            Scope = (DocumentLifecycleScope)input.Scope,
            NativeContentDirty = input.NativeContentDirty,
            ProjectSettingsIncluded = input.ProjectSettingsIncluded,
            CompleteChangeTracking = input.CompleteChangeTracking,
            DiskBaselineChecked = input.DiskBaselineChecked,
            ProcessEpoch = input.ProcessEpoch,
            CleanCheckpointSha256 = input.CleanCheckpointSha256 ?? "",
            SaveStableStateSha256 = input.SaveStableStateSha256 ?? ""
        };
        result.NativeFiles.Add(input.NativeFiles);
        foreach (NativeFileBaselineInput? file in input.FileBaselines)
            result.FileBaselines.Add(NativeFileBaselineInput.ToNative(file));
        return result;
    }

    private static void ValidateDigest(string? value, string name, bool required)
    {
        if (string.IsNullOrEmpty(value) && !required) return;
        if (value is null || value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw Invalid($"{name} must be a lowercase SHA-256 digest.");
    }

    private static AutomationException Invalid(string message) => new("invalid_lifecycle_state", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<DocumentLifecycleScopeInput>))]
public enum DocumentLifecycleScopeInput
{
    [JsonStringEnumMemberName("DLS_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("DLS_SCHEMATIC_HIERARCHY")] SchematicHierarchy = 1,
    [JsonStringEnumMemberName("DLS_PCB")] Pcb = 2
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeDocumentRevisionInput : NativeDocumentInputObject
{
    private string? epoch;
    private ulong sequence;
    public string? Epoch { get => epoch; set => epoch = BindOnce(value); }
    public ulong Sequence { get => sequence; set => sequence = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeFileBaselineInput : NativeDocumentInputObject
{
    private string? path;
    private string? baselinePath;
    private bool baselineKnown;
    private bool baselineExists;
    private string? baselineSha256;
    private ulong baselineBytes;
    private bool currentKnown;
    private bool currentExists;
    private string? currentSha256;
    private ulong currentBytes;
    private NativeFileBaselineStatusInput status;

    public string? Path { get => path; set => path = BindOnce(value); }
    public string? BaselinePath { get => baselinePath; set => baselinePath = BindOnce(value); }
    public bool BaselineKnown { get => baselineKnown; set => baselineKnown = BindOnce(value); }
    public bool BaselineExists { get => baselineExists; set => baselineExists = BindOnce(value); }
    public string? BaselineSha256 { get => baselineSha256; set => baselineSha256 = BindOnce(value); }
    public ulong BaselineBytes { get => baselineBytes; set => baselineBytes = BindOnce(value); }
    public bool CurrentKnown { get => currentKnown; set => currentKnown = BindOnce(value); }
    public bool CurrentExists { get => currentExists; set => currentExists = BindOnce(value); }
    public string? CurrentSha256 { get => currentSha256; set => currentSha256 = BindOnce(value); }
    public ulong CurrentBytes { get => currentBytes; set => currentBytes = BindOnce(value); }
    public NativeFileBaselineStatusInput Status { get => status; set => status = BindOnce(value); }

    internal static NativeFileBaselineState ToNative(NativeFileBaselineInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Path) || !Path.IsPathFullyQualified(input.Path)
            || !Enum.IsDefined(input.Status))
            throw new AutomationException("invalid_lifecycle_state", "Every file baseline requires absolute paths and a defined status.");
        if (!string.IsNullOrEmpty(input.BaselinePath) && !Path.IsPathFullyQualified(input.BaselinePath))
            throw new AutomationException("invalid_lifecycle_state", "A supplied baseline path must be absolute.");
        ValidateOptionalDigest(input.BaselineSha256, "baselineSha256");
        ValidateOptionalDigest(input.CurrentSha256, "currentSha256");
        return new NativeFileBaselineState
        {
            Path = input.Path,
            BaselinePath = input.BaselinePath,
            BaselineKnown = input.BaselineKnown,
            BaselineExists = input.BaselineExists,
            BaselineSha256 = input.BaselineSha256 ?? "",
            BaselineBytes = input.BaselineBytes,
            CurrentKnown = input.CurrentKnown,
            CurrentExists = input.CurrentExists,
            CurrentSha256 = input.CurrentSha256 ?? "",
            CurrentBytes = input.CurrentBytes,
            Status = (NativeFileBaselineStatus)input.Status
        };
    }

    private static void ValidateOptionalDigest(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new AutomationException("invalid_lifecycle_state", $"{name} must be a lowercase SHA-256 digest when supplied.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeFileBaselineStatusInput>))]
public enum NativeFileBaselineStatusInput
{
    [JsonStringEnumMemberName("NFBS_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("NFBS_UNCHANGED")] Unchanged = 1,
    [JsonStringEnumMemberName("NFBS_CHANGED")] Changed = 2,
    [JsonStringEnumMemberName("NFBS_WRONG_PATH")] WrongPath = 3,
    [JsonStringEnumMemberName("NFBS_UNREADABLE")] Unreadable = 4
}
