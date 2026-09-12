using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record SchematicHierarchyConflict(string InstancePath, string Reason,
    SchematicScreenData? Baseline, SchematicScreenData? Xml, SchematicScreenData? Native,
    IReadOnlyList<SchematicItemConflict> Items);

public sealed record SchematicHierarchyMergeResult(SchematicHierarchyData? Merged,
    IReadOnlyList<SchematicItemOperation> NativeOperations, IReadOnlyList<SchematicHierarchyConflict> Conflicts,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null)
{
    // A plan is not live revision admission, complete coverage, or electrical verification.
    public bool CanApply => Merged is not null && Conflicts.Count == 0 && ErrorCode is null;
}

/// <summary>Pure whole-hierarchy reconciliation against the last shared snapshot.
/// Keeps exact paths and object identities, and returns no partial operations on conflict.
/// The native hierarchy delta validates shared screens and orders structural edits.</summary>
public static class SchematicHierarchyMerge
{
    public static string SnapshotToken(SchematicHierarchyData baseline, SchematicHierarchyData xml,
        SchematicHierarchyData native)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var state in new[] { baseline, xml, native })
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(SchematicDataXml.Write(state));
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Whole-sheet choices are bound to the exact three supplied versions.
    /// They can intentionally discard other changes on that sheet. They do not silently
    /// resolve other sheets, repair topology, persist choices or authorize live edits.</summary>
    public static SchematicHierarchyMergeResult Resolve(SchematicHierarchyData baseline,
        SchematicHierarchyData xml, SchematicHierarchyData native, string expectedSnapshotToken,
        IReadOnlyDictionary<string, SchematicConflictChoice> choices, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SnapshotToken(baseline, xml, native) != expectedSnapshotToken)
            throw new AutomationException("hierarchy_conflicts_changed", "The conflict versions changed; inspect them before choosing.");
        var unresolved = Plan(baseline, xml, native, cancellationToken);
        var paths = unresolved.Conflicts.Select(c => c.InstancePath).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, choice) in choices)
            if (!paths.Contains(path) || !Enum.IsDefined(choice))
                throw new AutomationException("invalid_hierarchy_resolution", "Choose an existing conflicting sheet path and a supported version.");
        if (choices.Count == 0 || unresolved.ErrorCode is not null) return unresolved;
        return PlanCore(baseline, xml, native, choices, cancellationToken);
    }

    public static SchematicHierarchyMergeResult Plan(SchematicHierarchyData baseline,
        SchematicHierarchyData xml, SchematicHierarchyData native, CancellationToken cancellationToken = default) =>
        PlanCore(baseline, xml, native, null, cancellationToken);

    private static SchematicHierarchyMergeResult PlanCore(SchematicHierarchyData baseline,
        SchematicHierarchyData xml, SchematicHierarchyData native,
        IReadOnlyDictionary<string, SchematicConflictChoice>? choices, CancellationToken cancellationToken)
    {
        var gaps = new List<HierarchyCoverageGap>();
        try
        {
            foreach (var state in new[] { baseline, xml, native })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var topology = SchematicHierarchyTopology.Inspect(state, cancellationToken);
                gaps.AddRange(topology.CoverageGaps);
                if (!topology.IsValid)
                    return new(null, [], [], gaps.Distinct().ToArray(), "invalid_merge_hierarchy", "Every input hierarchy must have valid exact instance/reference topology.");
                // Validate unchanged persisted content too; unknown content is not absence.
                SchematicHierarchyDelta.Plan(state, state, cancellationToken);
                foreach (var shared in state.Instances.GroupBy(s => s.Metadata.ScreenId.Value).Where(g => g.Count() > 1))
                {
                    var representative = SchematicHierarchyDelta.PhysicalScreen(shared.First());
                    foreach (var instance in shared.Skip(1))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!representative.Equals(SchematicHierarchyDelta.PhysicalScreen(instance)))
                            return new(null, [], [], gaps.Distinct().ToArray(), "inconsistent_shared_merge_input",
                                "Repeated instances disagree about the persisted contents of one physical sheet.");
                    }
                }
            }
            if (!baseline.Document.Equals(xml.Document) || !baseline.Document.Equals(native.Document))
                return new(null, [], [], gaps.Distinct().ToArray(), "merge_document_changed", "All versions must identify the same hierarchy root.");
            string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
            var before = baseline.Instances.ToDictionary(Key, StringComparer.Ordinal);
            var wanted = xml.Instances.ToDictionary(Key, StringComparer.Ordinal);
            var current = native.Instances.ToDictionary(Key, StringComparer.Ordinal);
            var rootProject = baseline.Instances.Single(s => s.Metadata.Document.Equals(baseline.Document)).Metadata;
            var newSheetIds = wanted.Keys.Union(current.Keys).Where(path => !before.ContainsKey(path))
                .Select(path => Guid.Parse(path[(path.LastIndexOf('/') + 1)..])).ToHashSet();
            var merged = new SchematicHierarchyData { Document = native.Document.Clone() };
            var conflicts = new List<SchematicHierarchyConflict>();
            foreach (string path in before.Keys.Union(wanted.Keys).Union(current.Keys).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var b = before.GetValueOrDefault(path); var x = wanted.GetValueOrDefault(path); var n = current.GetValueOrDefault(path);
                // Project values copied onto sheet observations have one owner, the root.
                // Do not turn a project-variable edit into a delete/modify conflict on
                // every removed sheet, or lose it on a concurrently inserted branch.
                bool root = path.IndexOf('/') < 0;
                var compareB = root || b is null ? b : WithProject(b, rootProject);
                var compareX = root || x is null ? x : WithProject(x, rootProject);
                var compareN = root || n is null ? n : WithProject(n, rootProject);
                SchematicScreenData? chosen;
                if (choices is not null && choices.TryGetValue(path, out var choice))
                    chosen = choice switch { SchematicConflictChoice.Xml => x, SchematicConflictChoice.Native => n, _ => b };
                else if (Equals(compareX, compareN)) chosen = x;
                else if (Equals(compareB, compareX)) chosen = n;
                else if (Equals(compareB, compareN)) chosen = x;
                else if (b is not null && x is not null && n is not null)
                {
                    var itemMerge = SchematicItemMerge.PlanWithNewSheets(compareB!, compareX!, compareN!, newSheetIds);
                    if (!itemMerge.CanApply)
                    {
                        conflicts.Add(new(path, itemMerge.UnsupportedChange is null ? "sheet_edits_conflict" : "unsupported_sheet_merge",
                            b.Clone(), x.Clone(), n.Clone(), itemMerge.Conflicts));
                        continue;
                    }
                    chosen = itemMerge.Merged;
                }
                else
                {
                    conflicts.Add(new(path, b is null ? "competing_sheet_addition" : "sheet_delete_modify",
                        b?.Clone(), x?.Clone(), n?.Clone(), []));
                    continue;
                }
                if (chosen is not null) merged.Instances.Add(chosen.Clone());
            }
            if (conflicts.Count > 0) return new(null, [], conflicts, gaps.Distinct().ToArray());
            var project = merged.Instances.Single(s => s.Metadata.Document.Equals(merged.Document)).Metadata;
            for (int i = 0; i < merged.Instances.Count; ++i)
                merged.Instances[i] = WithProject(merged.Instances[i], project);
            // Combining individually valid changes can create invalid shared state or cycles.
            // Reject the entire batch rather than executing only the easy sheets.
            var operations = SchematicHierarchyDelta.Plan(native, merged, cancellationToken);
            foreach (var shared in merged.Instances.GroupBy(s => s.Metadata.ScreenId.Value).Where(g => g.Count() > 1))
                if (shared.Select(SchematicHierarchyDelta.PhysicalScreen).Distinct().Count() != 1)
                    return new(null, [], [], gaps.Distinct().ToArray(), "inconsistent_shared_merge_result",
                        "The chosen versions disagree about a shared physical sheet; choose compatible versions for all instances.");
            return new(merged, operations, [], gaps.Distinct().ToArray());
        }
        catch (AutomationException error)
        {
            return new(null, [], [], gaps.Distinct().ToArray(), error.Code, error.Message);
        }
    }

    private static SchematicScreenData WithProject(SchematicScreenData source, SchematicMetadata project)
    {
        var result = source.Clone(); var metadata = result.Metadata;
        metadata.EmbeddedFiles = project.EmbeddedFiles?.Clone(); metadata.EmbeddedFonts = project.EmbeddedFonts;
        metadata.BusAliases.Clear(); metadata.BusAliases.Add(project.BusAliases.Select(a => a.Clone()));
        metadata.TextVariables.Clear(); metadata.TextVariables.Add(project.TextVariables);
        metadata.NetChains.Clear(); metadata.NetChains.Add(project.NetChains.Select(c => c.Clone()));
        metadata.VariantDescriptions.Clear(); metadata.VariantDescriptions.Add(project.VariantDescriptions);
        metadata.DrawingRatios = project.DrawingRatios?.Clone(); metadata.Formatting = project.Formatting?.Clone();
        metadata.Annotation = project.Annotation?.Clone();
        SchematicVariantProjection.Reproject(result);
        return result;
    }
}
