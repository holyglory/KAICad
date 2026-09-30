using Google.Protobuf;
using Kiapi.Common.Types;
using Kiapi.Schematic;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// Structural replacement validation. The required rule set comes from an
// identified native peer; this does not invent defaults or resolve marker IDs.
internal static class SchematicErcSettingsValidation
{
    internal static SchematicErcSettings Normalize(SchematicErcSettings value, IReadOnlySet<ErcErrorType> requiredRules)
    {
        Validate(value, requiredRules);
        var result = value.Clone();
        result.RuleSeverities.Clear();
        result.RuleSeverities.Add(value.RuleSeverities.OrderBy(rule => (int)rule.RuleType).Select(rule => rule.Clone()));
        result.PinMap.Clear();
        result.PinMap.Add(value.PinMap.OrderBy(cell => (int)cell.First).ThenBy(cell => (int)cell.Second).Select(cell => cell.Clone()));
        result.Exclusions.Clear();
        result.Exclusions.Add(value.Exclusions.OrderBy(exclusion => exclusion.Marker.ToByteString().ToBase64(), StringComparer.Ordinal)
            .Select(exclusion => exclusion.Clone()));
        return result;
    }

    internal static bool Same(SchematicErcSettings? first, SchematicErcSettings? second)
    {
        if (first is null || second is null) return first is null && second is null;
        var rules = first.RuleSeverities.Select(rule => rule.RuleType).ToHashSet();
        return Normalize(first, rules).Equals(Normalize(second, rules));
    }

    internal static void Validate(SchematicErcSettings value, IReadOnlySet<ErcErrorType> requiredRules)
    {
        if (requiredRules.Count == 0 || requiredRules.Any(rule => !KnownRule(rule)))
            throw Invalid("A nonempty native persisted-rule catalogue is required.");
        var rules = new HashSet<ErcErrorType>();
        foreach (var rule in value.RuleSeverities)
        {
            if (!KnownRule(rule.RuleType) || !rules.Add(rule.RuleType)
                || rule.Severity is not (RuleSeverity.RsWarning or RuleSeverity.RsError or RuleSeverity.RsIgnore))
                throw Invalid("ERC rules require unique known identities and warning, error or ignore severity.");
        }
        if (!rules.SetEquals(requiredRules))
            throw Invalid("Replace every persisted rule in the identified native catalogue, without additions or omissions.");

        var types = Enum.GetValues<ElectricalPinType>().Where(type => (int)type != 0).ToArray();
        var cells = new HashSet<(ElectricalPinType, ElectricalPinType)>();
        foreach (var cell in value.PinMap)
        {
            if (!types.Contains(cell.First) || !types.Contains(cell.Second)
                || !Enum.IsDefined(cell.Conflict) || (int)cell.Conflict == 0
                || !cells.Add((cell.First, cell.Second)))
                throw Invalid("Pin rules require known types, a defined interaction and unique ordered pairs.");
        }
        if (cells.Count != types.Length * types.Length)
            throw Invalid("Replace the complete native pin interaction matrix; missing entries are not defaults.");

        var exclusions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exclusion in value.Exclusions)
        {
            var marker = exclusion.Marker;
            if (marker is null || !KnownRule(marker.ErrorType) || marker.Position is null
                || marker.Items.Count > 2 || exclusion.Comment.Contains('\0')
                || marker.Items.Any(item => !Identifier(item.Value))
                || !Path(marker.SheetSpecificPath) || !Path(marker.MainItemSheetPath) || !Path(marker.AuxItemSheetPath)
                || marker.Child is { } child && child.TextValue.Contains('\0'))
                throw Invalid("ERC exclusions require explicit marker data, canonical references and NUL-free text.");
            foreach (long position in new[] { marker.Position.XNm, marker.Position.YNm })
                if (position % 100 != 0 || position / 100 < int.MinValue || position / 100 > int.MaxValue)
                    throw Invalid("ERC marker positions must be representable on the native 100 nm grid.");
            if (!exclusions.Add(marker.ToByteString().ToBase64()))
                throw Invalid("An ERC marker may have only one exclusion comment.");
        }
    }

    private static bool KnownRule(ErcErrorType value) => (int)value != 0 && Enum.IsDefined(value);
    private static bool Identifier(string value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && id.ToString("D") == value;
    private static bool Path(SheetPath? path) => path is null
        || path.Path.Count > 0 && path.Path.All(id => Identifier(id.Value));
    private static AutomationException Invalid(string message) => new("invalid_erc_settings", message);
}
