using System.Globalization;
using Google.Protobuf;
using M = KiCad.Automation.Model;
using P = KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Native;

/// <summary>Exact typed exchange between the native structural view and the
/// existing engineering model. No identity/name inference or file I/O.</summary>
public static class StructuralEditorCodec
{
    public static P.StructuralDiagramData Encode(M.StructuralDiagram model, M.Circuit circuit)
    {
        model.Validate(circuit);
        var result = new P.StructuralDiagramData { Id = Id(model.Id) };
        foreach (var block in model.Blocks)
        {
            var row = new P.StructuralBlockData { Id = Id(block.Id), Name = block.Name, Purpose = block.Purpose };
            if (block.ParentId is { } parent) row.ParentId = Id(parent);
            row.ComponentIds.Add(block.ComponentIds.Select(Id)); result.Blocks.Add(row);
        }
        result.Ports.Add(model.Ports.Select(p => new P.StructuralPortData { Id = Id(p.Id), BlockId = Id(p.BlockId), Name = p.Name }));
        foreach (var link in model.Connections)
        {
            var row = new P.StructuralConnectionData { Id = Id(link.Id), FirstPortId = Id(link.FirstPortId),
                SecondPortId = Id(link.SecondPortId), Kind = (P.StructuralLinkKind)link.Kind,
                Description = link.Description, Direction = (P.StructuralLinkDirection)link.Direction };
            row.NetIds.Add(link.NetIds.Select(Id)); result.Connections.Add(row);
        }
        foreach (var item in model.Statements)
        {
            var row = new P.StructuralStatementData { Id = Id(item.Id), TargetId = Id(item.TargetId),
                Role = (P.StructuralStatementRole)item.Role, Text = item.Text };
            if (item.Strength is { } strength) row.Strength = (P.StructuralGuidanceStrength)strength;
            if (item.Connection is { } pins) row.Connection = new() { First = Pin(pins.First), Second = Pin(pins.Second) };
            row.DerivedFrom.Add(item.DerivedFrom.Select(Id)); row.Sources.Add(item.Sources.Select(Source)); result.Statements.Add(row);
        }
        foreach (var item in model.UnresolvedNetBindings ?? [])
        {
            var row = new P.StructuralUnresolvedNet { OwnerId = Id(item.OwnerId), FormerNetId = Id(item.FormerNetId),
                Change = (P.StructuralNetChange)item.Change, Reason = item.Reason };
            row.CandidateNetIds.Add(item.CandidateNetIds.Select(Id)); result.UnresolvedNets.Add(row);
        }
        foreach (var item in model.UnresolvedComponentReferences ?? [])
        {
            var row = new P.StructuralUnresolvedComponent { OwnerId = Id(item.OwnerId), Slot = (P.StructuralReferenceSlot)item.Slot,
                FormerTarget = Target(item.FormerTarget), Change = (P.StructuralComponentChange)item.Change, Reason = item.Reason };
            row.CandidateTargets.Add(item.CandidateTargets.Select(Target)); result.UnresolvedComponents.Add(row);
        }
        foreach (var item in model.Properties ?? [])
        {
            var value = item.Statement;
            var row = new P.StructuralPropertyData { Id = Id(value.Id), OwnerId = Id(item.OwnerId), Key = value.Key,
                Category = value.Category, Text = value.Text, Strength = (P.StructuralGuidanceStrength)value.Strength,
                Applicability = value.Applicability, Verification = (P.StructuralVerification)value.Verification };
            row.Sources.Add(value.Sources.Select(Source));
            if (value.Quantity is { } quantity) row.Quantity = Quantity(quantity);
            result.Properties.Add(row);
        }
        if (model.Presentation is { } layout)
        {
            result.Presentation = new();
            foreach (var block in layout.Blocks)
            {
                var row = new P.StructuralBlockPlacement { BlockId = Id(block.BlockId), Position = new() { XNm = block.XNm, YNm = block.YNm },
                    WidthNm = block.WidthNm, HeightNm = block.HeightNm, Locked = block.Locked };
                if (block.FillRgb is uint color) row.FillRgb = color;
                result.Presentation.Blocks.Add(row);
            }
            result.Presentation.Ports.Add(layout.Ports.Select(p => new P.StructuralPortPlacement
                { PortId = Id(p.PortId), Side = (P.StructuralPortSide)p.Side, OffsetNm = p.OffsetNm }));
            foreach (var link in layout.Connections)
            {
                var row = new P.StructuralConnectionPlacement { ConnectionId = Id(link.ConnectionId), Locked = link.Locked };
                row.Waypoints.Add(link.Waypoints.Select(Point));
                if (link.Label is { } label) row.Label = Point(label);
                result.Presentation.Connections.Add(row);
            }
        }
        return result;
    }

    public static M.StructuralDiagram Decode(P.StructuralDiagramData data, M.Circuit circuit)
    {
        // JSON omits protobuf unknown fields, including nested ones. Comparing
        // the complete reconstructed message rejects data this adapter cannot keep.
        if (!data.Equals(P.StructuralDiagramData.Parser.ParseJson(JsonFormatter.Default.Format(data))))
            throw Invalid("The structural view contains unsupported fields.");
        var result = new M.StructuralDiagram(GuidValue(data.Id),
            data.Blocks.Select(b => new M.StructuralBlock(GuidValue(b.Id), b.Name,
                b.HasParentId ? GuidValue(b.ParentId) : null, b.ComponentIds.Select(GuidValue).ToArray(), b.Purpose)).ToArray(),
            data.Ports.Select(p => new M.StructuralPort(GuidValue(p.Id), GuidValue(p.BlockId), p.Name)).ToArray(),
            data.Connections.Select(c => new M.StructuralConnection(GuidValue(c.Id), GuidValue(c.FirstPortId), GuidValue(c.SecondPortId),
                (M.StructuralConnectionKind)c.Kind, c.Description, c.NetIds.Select(GuidValue).ToArray(), (M.StructuralConnectionDirection)c.Direction)).ToArray(),
            data.Statements.Select(s => new M.EngineeringStatement(GuidValue(s.Id), GuidValue(s.TargetId),
                (M.EngineeringStatementRole)s.Role, s.HasStrength ? (M.GuidanceStrength)s.Strength : null, s.Text,
                s.Connection is { } c ? new M.PinConnectionDetail(Pin(c.First), Pin(c.Second)) : null,
                s.DerivedFrom.Select(GuidValue).ToArray(), s.Sources.Select(Source).ToArray())).ToArray(),
            data.UnresolvedNets.Count == 0 ? null : data.UnresolvedNets.Select(n => new M.UnresolvedNetBinding(GuidValue(n.OwnerId),
                GuidValue(n.FormerNetId), (M.NetBindingChangeKind)n.Change, n.Reason, n.CandidateNetIds.Select(GuidValue).ToArray())).ToArray(),
            data.UnresolvedComponents.Count == 0 ? null : data.UnresolvedComponents.Select(c => new M.UnresolvedComponentReference(GuidValue(c.OwnerId),
                (M.ComponentReferenceSlot)c.Slot, Target(c.FormerTarget), (M.ComponentReferenceChangeKind)c.Change, c.Reason,
                c.CandidateTargets.Select(Target).ToArray())).ToArray(),
            data.Properties.Count == 0 ? null : data.Properties.Select(p => new M.StructuralProperty(GuidValue(p.OwnerId),
                new M.GuidanceStatement(GuidValue(p.Id), p.Key, p.Category, p.Text,
                    p.HasStrength ? (M.GuidanceStrength)p.Strength : throw Invalid("A named property requires an explicit strength."),
                    p.Applicability, p.Sources.Select(Source).ToArray(), (M.VerificationState)p.Verification,
                    Quantity: p.Quantity is { } quantity ? Quantity(quantity) : null))).ToArray(),
            data.Presentation is { } layout ? new M.StructuralPresentation(
                layout.Blocks.Select(b => new M.StructuralBlockPlacement(GuidValue(b.BlockId),
                    Need(b.Position).XNm, b.Position.YNm, b.WidthNm, b.HeightNm, b.Locked, b.HasFillRgb ? b.FillRgb : null)).ToArray(),
                layout.Ports.Select(p => new M.StructuralPortPlacement(GuidValue(p.PortId), (M.StructuralPortSide)p.Side, p.OffsetNm)).ToArray(),
                layout.Connections.Select(c => new M.StructuralConnectionPlacement(GuidValue(c.ConnectionId), c.Waypoints.Select(Point).ToArray(),
                    c.Label is { } label ? Point(label) : null, c.Locked)).ToArray()) : null);
        result.Validate(circuit);
        return result;
    }

    private static P.StructuralQuantity Quantity(M.GuidanceQuantity value)
    {
        var result = new P.StructuralQuantity { Kind = (P.StructuralParameterKind)value.Kind, Unit = value.Unit };
        if (value.Nominal is decimal nominal) result.Nominal = Number(nominal);
        if (value.Minimum is decimal minimum) result.Minimum = Number(minimum);
        if (value.Maximum is decimal maximum) result.Maximum = Number(maximum);
        if (value.UnknownReason is string unknown) result.UnknownReason = unknown;
        if (value.Tolerance is { } tolerance) result.Tolerance = new() { Kind = (P.StructuralToleranceKind)tolerance.Kind,
            Minus = Number(tolerance.Minus), Plus = Number(tolerance.Plus) };
        return result;
    }
    private static M.GuidanceQuantity Quantity(P.StructuralQuantity value) => new((M.ParameterKind)value.Kind, value.Unit,
        value.HasNominal ? Number(value.Nominal) : null, value.HasMinimum ? Number(value.Minimum) : null,
        value.HasMaximum ? Number(value.Maximum) : null, value.Tolerance is { } tolerance
            ? new M.ParameterTolerance((M.ToleranceKind)tolerance.Kind, Number(tolerance.Minus), Number(tolerance.Plus)) : null,
        value.HasUnknownReason ? value.UnknownReason : null);
    private static P.StructuralSourceReference Source(M.SourceReference value)
    {
        var result = new P.StructuralSourceReference { DocumentId = value.DocumentId, Revision = value.Revision };
        if (value.Page is int page) result.Page = page;
        if (value.Table is string table) result.Table = table;
        if (value.PartVariant is string variant) result.PartVariant = variant;
        return result;
    }
    private static M.SourceReference Source(P.StructuralSourceReference value) => new(value.DocumentId, value.Revision,
        value.HasPage ? value.Page : null, value.HasTable ? value.Table : null, value.HasPartVariant ? value.PartVariant : null);
    private static P.StructuralPinEndpoint Pin(M.PinEndpoint value) => new() { ComponentId = Id(value.ComponentId), Pin = value.Pin };
    private static M.PinEndpoint Pin(P.StructuralPinEndpoint value) => new(GuidValue(Need(value).ComponentId), value.Pin);
    private static P.StructuralComponentTarget Target(M.ComponentReferenceTarget value)
    {
        var result = new P.StructuralComponentTarget { ComponentId = Id(value.ComponentId) };
        if (value.PinNumber is string pin) result.PinNumber = pin;
        return result;
    }
    private static M.ComponentReferenceTarget Target(P.StructuralComponentTarget value) =>
        new(GuidValue(Need(value).ComponentId), value.HasPinNumber ? value.PinNumber : null);
    private static P.StructuralPoint Point(M.StructuralPoint p) => new() { XNm = p.XNm, YNm = p.YNm };
    private static M.StructuralPoint Point(P.StructuralPoint p) => new(p.XNm, p.YNm);
    private static T Need<T>(T? value) where T : class => value ?? throw Invalid("A required structural record is missing.");
    private static string Id(Guid value) => value.ToString("D");
    private static Guid GuidValue(string value) => Guid.TryParseExact(value, "D", out var id) && Id(id) == value
        ? id : throw Invalid("Structural identities require canonical UUIDs.");
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Number(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal value) || Canonical(text) != Canonical(Number(value)))
            throw Invalid("Structural quantities require exact decimal values; no rounded value was accepted.");
        return value;
        static string Canonical(string source)
        {
            bool negative = source.StartsWith('-'); source = source.TrimStart('+', '-');
            int point = source.IndexOf('.'); string integer = (point < 0 ? source : source[..point]).TrimStart('0');
            string fraction = point < 0 ? "" : source[(point + 1)..].TrimEnd('0');
            if (integer.Length == 0) integer = "0";
            return (negative && (integer != "0" || fraction.Length != 0) ? "-" : "")
                + integer + (fraction.Length == 0 ? "" : "." + fraction);
        }
    }
    private static M.AutomationException Invalid(string message) => new("invalid_structural_editor_data", message);
}
