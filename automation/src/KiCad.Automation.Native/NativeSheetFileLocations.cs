using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Validates runtime file provenance without expanding variables in the companion.</summary>
internal static class NativeSheetFileLocations
{
    internal const string ObservationChanged = "native_observation_changed";

    internal static async Task<SchematicFileLocations?> CaptureAsync(NativeClient client,
        SchematicHierarchyDataSnapshot snapshot, CancellationToken token, AutomationSession? session = null)
    {
        bool supported = session is null ? client.LastHandshakeHandles(ReadSchematicFileLocations.Descriptor.FullName)
            : session.HandledRequests.Contains(ReadSchematicFileLocations.Descriptor.FullName);
        if (!supported) return null;
        SchematicFileLocations files;
        try
        {
            files = await client.InvokeAsync<ReadSchematicFileLocations, SchematicFileLocations>(new()
                { Document = snapshot.Data.Document.Clone(), ExpectedRevision = snapshot.Revision.Clone() }, token);
        }
        catch (NativeApiException error) when (error.Status == 3)
        {
            // A second read may race a real native edit. Prove revision advancement
            // before treating a refusal as transient; other bad requests still fail.
            var current = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new()
                { Document = snapshot.Data.Document.Clone() }, token);
            if (current.Data?.Document is { } document && document.Equals(snapshot.Data.Document)
                && current.Revision is { } revision && revision.Epoch == snapshot.Revision.Epoch
                && revision.Sequence > snapshot.Revision.Sequence)
                throw new AutomationException(ObservationChanged,
                    "KiCad changed while its hierarchy and file locations were being observed; read a fresh observation.");
            throw;
        }
        Validate(files, snapshot, client.Epoch);
        return files;
    }

    internal static void Validate(SchematicFileLocations files, SchematicHierarchyDataSnapshot snapshot, string? processEpoch = null)
    {
        if (files.Document is null || !files.Document.Equals(snapshot.Data.Document)
            || files.Revision is null || !files.Revision.Equals(snapshot.Revision)
            || string.IsNullOrEmpty(files.ProcessEpoch) || processEpoch is not null && files.ProcessEpoch != processEpoch)
            throw Error("The native file locations do not belong to this exact document revision and process.");
        var screens = snapshot.Data.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var physical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Locations)
        {
            if (file.Path is null || file.ScreenId is null)
                throw Error("A native file observation requires an exact path and screen identity.");
            string path = string.Join('/', file.Path.Path.Select(p => p.Value));
            if (!seen.Add(path) || !screens.TryGetValue(path, out var screen) || !Equals(file.ScreenId, screen.Metadata.ScreenId)
                || !Path.IsPathFullyQualified(file.LoadedFilename) || file.LoadedFilename.Contains('\0'))
                throw Error("Each loaded sheet must have exactly one matching screen identity and absolute native filename.");
            if (physical.TryGetValue(file.ScreenId.Value, out string? previous) && previous != file.LoadedFilename)
                throw Error("Repeated instances of one physical screen report different loaded files.");
            physical[file.ScreenId.Value] = file.LoadedFilename;
            if (file.Path.Path.Count > 1
                && SchematicNativeSheetChanges.SheetSymbolOf(snapshot.Data, path)?.FilenameField?.Text?.Text_ != file.DeclaredFilename)
                throw Error("The native file expression changed while its hierarchy was being observed.");
        }
        if (!seen.SetEquals(screens.Keys)) throw Error("The native file observation omits a loaded sheet.");
    }

    internal static SchematicFileLocation? Find(DesignRecoveryState state, string nativePath)
    {
        var files = state.NativeFileLocations;
        if (files?.Revision is null || files.Revision.Epoch != state.NativeRevision.Epoch
            || files.Revision.Sequence != state.NativeRevision.Sequence) return null;
        Validate(files, new() { Data = state.Observed, Revision = files.Revision.Clone() });
        return files.Locations.Single(f => string.Join('/', f.Path.Path.Select(p => p.Value)) == nativePath);
    }

    private static AutomationException Error(string message) => new("native_sheet_file_observation_invalid", message);
}
