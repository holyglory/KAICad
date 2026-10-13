using System.ComponentModel;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>A complete board checkpoint and task-oriented footprint geometry, without protobuf JSON or Any payloads.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintPlacementInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private PcbFootprintAuthoringInput? footprint;
    private string? operationId;
    private string? expectedPlanId;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public PcbFootprintAuthoringInput? Footprint { get => footprint; set => footprint = BindOnce(value); }
    [Description("Canonical operation UUID. Required when applying; retain the same UUID and exact input after a lost reply.")]
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    [Description("Exact planId returned by kicad_pcb_footprint_place_plan_typed. Required when applying.")]
    public string? ExpectedPlanId { get => expectedPlanId; set => expectedPlanId = BindOnce(value); }

    internal (DocumentSpecifier Document, DocumentLifecycleState State, FootprintInstance Footprint) ToNative(
        CancellationToken token)
    {
        if (Document is null || ExpectedState is null || Footprint is null)
            throw Invalid("An explicit PCB document, complete lifecycle observation and footprint definition are required.");
        DocumentSpecifier target = NativeDocumentInput.ToNative(Document);
        DocumentLifecycleState state = DocumentLifecycleInput.ToNative(ExpectedState);
        if (target.Type != DocumentType.DoctypePcb || state.Scope != DocumentLifecycleScope.DlsPcb
            || !target.Equals(state.Document))
            throw Invalid("The placement document and lifecycle observation must identify the same PCB.");
        if (!state.ProjectSettingsIncluded || !state.CompleteChangeTracking || !state.DiskBaselineChecked
            || state.Revision.Epoch != state.NativeIdentity
            || !CheckedSchematicContract.FileCoverage(state))
            throw Invalid("The complete PCB checkpoint must include project settings, the native revision and unchanged file-baseline coverage.");
        return (target, state, Footprint.ToNative(token));
    }

    internal static AutomationException Invalid(string message) => new("invalid_pcb_footprint_typed_input", message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintAuthoringInput : NativeDocumentInputObject
{
    private string id = "";
    private NativeLibraryIdentifierInput? library;
    private long xNm;
    private long yNm;
    private double rotationDegrees;
    private string layer = "F.Cu";
    private bool locked;
    private PcbFootprintMountingInput mounting = PcbFootprintMountingInput.Unspecified;
    private string description = "";
    private string keywords = "";
    private PcbFootprintTextInput? referenceField;
    private PcbFootprintTextInput? valueField;
    private PcbFootprintTextInput? datasheetField;
    private PcbFootprintTextInput? descriptionField;
    private List<PcbFootprintPadInput>? pads = [];
    private List<PcbFootprintGraphicInput>? graphics = [];

    public string Id { get => id; set => id = BindOnce(value); }
    public NativeLibraryIdentifierInput? Library { get => library; set => library = BindOnce(value); }
    public long XNm { get => xNm; set => xNm = BindOnce(value); }
    public long YNm { get => yNm; set => yNm = BindOnce(value); }
    public double RotationDegrees { get => rotationDegrees; set => rotationDegrees = BindOnce(value); }
    [Description("Footprint placement layer: F.Cu or B.Cu. Child coordinates and layers are supplied explicitly in the footprint definition.")]
    public string Layer { get => layer; set => layer = BindOnce(value); }
    public bool Locked { get => locked; set => locked = BindOnce(value); }
    public PcbFootprintMountingInput Mounting { get => mounting; set => mounting = BindOnce(value); }
    public string Description { get => description; set => description = BindOnce(value); }
    public string Keywords { get => keywords; set => keywords = BindOnce(value); }
    public PcbFootprintTextInput? ReferenceField { get => referenceField; set => referenceField = BindOnce(value); }
    public PcbFootprintTextInput? ValueField { get => valueField; set => valueField = BindOnce(value); }
    [Description("Optional datasheet field. When absent, the exact proposal includes an empty hidden field with a deterministic UUID.")]
    public PcbFootprintTextInput? DatasheetField { get => datasheetField; set => datasheetField = BindOnce(value); }
    [Description("Optional description field. When absent, the exact proposal includes an empty hidden field with a deterministic UUID.")]
    public PcbFootprintTextInput? DescriptionField { get => descriptionField; set => descriptionField = BindOnce(value); }
    public List<PcbFootprintPadInput>? Pads { get => pads; set => pads = BindOnce(value); }
    public List<PcbFootprintGraphicInput>? Graphics { get => graphics; set => graphics = BindOnce(value); }

    internal FootprintInstance ToNative(CancellationToken token)
    {
        FootprintInputValidation.Uuid(Id);
        if (Library is null || string.IsNullOrWhiteSpace(Library.LibraryNickname)
            || string.IsNullOrWhiteSpace(Library.EntryName)
            || Library.LibraryNickname.Contains('\0') || Library.EntryName.Contains('\0'))
            throw PcbFootprintPlacementInput.Invalid("The footprint needs a library nickname and entry name without NUL characters.");
        if (!Enum.IsDefined(Mounting) || ReferenceField is null || ValueField is null)
            throw PcbFootprintPlacementInput.Invalid("A defined mounting style, reference field and value field are required.");
        if (Pads is null || Graphics is null || Pads.Count + (long)Graphics.Count > 16_384)
            throw PcbFootprintPlacementInput.Invalid("Pads and graphics must be arrays containing at most 16384 total child items.");
        FootprintInputValidation.Angle(RotationDegrees);
        BoardLayer placementLayer = FootprintInputValidation.Layer(Layer);
        if (placementLayer is not (BoardLayer.BlFCu or BoardLayer.BlBCu))
            throw PcbFootprintPlacementInput.Invalid("A footprint is placed on F.Cu or B.Cu.");
        var definition = new Footprint
        {
            Id = new() { LibraryNickname = Library.LibraryNickname, EntryName = Library.EntryName },
            Anchor = new(),
            Attributes = new() { MountingStyle = (FootprintMountingStyle)Mounting,
                Description = Description ?? "", Keywords = Keywords ?? "" },
            ReferenceField = ReferenceField.ToField(0, "Reference"),
            ValueField = ValueField.ToField(1, "Value"),
            DatasheetField = (DatasheetField ?? EmptyField("Datasheet")).ToField(2, "Datasheet"),
            DescriptionField = (DescriptionField ?? EmptyField("Description")).ToField(3, "Description")
        };
        var identities = new HashSet<string>(StringComparer.Ordinal) { Id };
        foreach (Field field in new[] { definition.ReferenceField, definition.ValueField,
            definition.DatasheetField, definition.DescriptionField })
            Unique(field.Text.Id.Value);
        foreach (PcbFootprintPadInput? pad in Pads)
        {
            token.ThrowIfCancellationRequested();
            if (pad is null) throw PcbFootprintPlacementInput.Invalid("A footprint pad cannot be null.");
            Unique(pad.Id);
            definition.Items.Add(Any.Pack(pad.ToNative()));
        }
        foreach (PcbFootprintGraphicInput? graphic in Graphics)
        {
            token.ThrowIfCancellationRequested();
            if (graphic is null) throw PcbFootprintPlacementInput.Invalid("A footprint graphic cannot be null.");
            Unique(graphic.Id);
            definition.Items.Add(Any.Pack(graphic.ToNative()));
        }
        var result = new FootprintInstance
        {
            Id = new() { Value = Id }, Position = new() { XNm = XNm, YNm = YNm },
            Orientation = new() { ValueDegrees = RotationDegrees }, Layer = placementLayer,
            Locked = Locked ? LockedState.LsLocked : LockedState.LsUnlocked, Definition = definition,
            ReferenceField = definition.ReferenceField.Clone(), ValueField = definition.ValueField.Clone(),
            DatasheetField = definition.DatasheetField.Clone(), DescriptionField = definition.DescriptionField.Clone(),
            Attributes = definition.Attributes.Clone()
        };
        IReadOnlyList<string> issues = FootprintLibraryCheckedContract.ValidateCandidate(result, token);
        if (issues.Count != 0)
            throw PcbFootprintPlacementInput.Invalid("The footprint definition is incomplete: " + string.Join(", ", issues.Take(16)));
        return result;

        void Unique(string identity)
        {
            FootprintInputValidation.Uuid(identity);
            if (!identities.Add(identity))
                throw PcbFootprintPlacementInput.Invalid("Footprint, field, pad and graphic UUIDs must be distinct.");
        }
    }

    private PcbFootprintTextInput EmptyField(string role)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Id + "\nfootprint-field\n" + role));
        // Deterministic RFC 9562 version-8 UUID; the full generated field is visible in the proposal.
        bytes[6] = (byte)((bytes[6] & 15) | 128);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        return new() { Id = new Guid(bytes.AsSpan(0, 16), bigEndian: true).ToString("D"), Visible = false };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintTextInput : NativeDocumentInputObject
{
    private string id = "";
    private string text = "";
    private long xNm;
    private long yNm;
    private long sizeNm = 1_000_000;
    private long strokeWidthNm = 150_000;
    private double rotationDegrees;
    private string layer = "F.Fab";
    private bool visible = true;
    public string Id { get => id; set => id = BindOnce(value); }
    public string Text { get => text; set => text = BindOnce(value); }
    public long XNm { get => xNm; set => xNm = BindOnce(value); }
    public long YNm { get => yNm; set => yNm = BindOnce(value); }
    public long SizeNm { get => sizeNm; set => sizeNm = BindOnce(value); }
    public long StrokeWidthNm { get => strokeWidthNm; set => strokeWidthNm = BindOnce(value); }
    public double RotationDegrees { get => rotationDegrees; set => rotationDegrees = BindOnce(value); }
    public string Layer { get => layer; set => layer = BindOnce(value); }
    public bool Visible { get => visible; set => visible = BindOnce(value); }

    internal Kiapi.Board.Types.Field ToField(int fieldId, string name)
    {
        FootprintInputValidation.Uuid(Id);
        FootprintInputValidation.Angle(RotationDegrees);
        if (Text is null || Text.Contains('\0') || SizeNm <= 0 || StrokeWidthNm <= 0)
            throw PcbFootprintPlacementInput.Invalid("Footprint fields need text without NUL characters and positive text size and stroke width.");
        return new()
        {
            Id = new() { Id = fieldId }, Name = name, Visible = Visible,
            Text = new()
            {
                Id = new() { Value = Id }, Layer = FootprintInputValidation.Layer(Layer),
                Text = new()
                {
                    Text_ = Text, Position = new() { XNm = XNm, YNm = YNm },
                    Attributes = new()
                    {
                        Size = new() { XNm = SizeNm, YNm = SizeNm }, StrokeWidth = new() { ValueNm = StrokeWidthNm },
                        Angle = new() { ValueDegrees = RotationDegrees }, HorizontalAlignment = HorizontalAlignment.HaCenter,
                        VerticalAlignment = VerticalAlignment.VaCenter, KeepUpright = true
                    }
                }
            }
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintPadInput : NativeDocumentInputObject
{
    private string id = "";
    private string number = "";
    private string netName = "";
    private PcbFootprintPadTypeInput type;
    private PcbFootprintPadShapeInput shape;
    private long xNm;
    private long yNm;
    private long widthNm;
    private long heightNm;
    private double rotationDegrees;
    private double cornerRoundingRatio;
    private long drillWidthNm;
    private long drillHeightNm;
    private List<string>? layers = [];
    public string Id { get => id; set => id = BindOnce(value); }
    [Description("Electrical pad number; may be empty for a nonplated mounting hole. Repeated pad numbers are permitted.")]
    public string Number { get => number; set => number = BindOnce(value); }
    public string NetName { get => netName; set => netName = BindOnce(value); }
    public PcbFootprintPadTypeInput Type { get => type; set => type = BindOnce(value); }
    public PcbFootprintPadShapeInput Shape { get => shape; set => shape = BindOnce(value); }
    public long XNm { get => xNm; set => xNm = BindOnce(value); }
    public long YNm { get => yNm; set => yNm = BindOnce(value); }
    public long WidthNm { get => widthNm; set => widthNm = BindOnce(value); }
    public long HeightNm { get => heightNm; set => heightNm = BindOnce(value); }
    public double RotationDegrees { get => rotationDegrees; set => rotationDegrees = BindOnce(value); }
    public double CornerRoundingRatio { get => cornerRoundingRatio; set => cornerRoundingRatio = BindOnce(value); }
    public long DrillWidthNm { get => drillWidthNm; set => drillWidthNm = BindOnce(value); }
    public long DrillHeightNm { get => drillHeightNm; set => drillHeightNm = BindOnce(value); }
    [Description("Explicit pad layers, such as F.Cu, F.Paste and F.Mask. Copper shape uses a normal padstack; arbitrary per-layer shapes are outside this typed constructor.")]
    public List<string>? Layers { get => layers; set => layers = BindOnce(value); }

    internal Pad ToNative()
    {
        FootprintInputValidation.Uuid(Id);
        FootprintInputValidation.Angle(RotationDegrees);
        if (!Enum.IsDefined(Type) || Type == PcbFootprintPadTypeInput.Unknown
            || !Enum.IsDefined(Shape) || Shape == PcbFootprintPadShapeInput.Unknown
            || Number is null || Number.Contains('\0') || Number.Length > 128
            || (Type != PcbFootprintPadTypeInput.NonPlatedHole && string.IsNullOrWhiteSpace(Number))
            || NetName is null || NetName.Contains('\0') || WidthNm <= 0 || HeightNm <= 0)
            throw PcbFootprintPlacementInput.Invalid("A pad needs a defined type/shape, a valid number and net name, and positive dimensions.");
        if (!double.IsFinite(CornerRoundingRatio) || CornerRoundingRatio < 0 || CornerRoundingRatio > 0.5
            || (Shape != PcbFootprintPadShapeInput.RoundRectangle && CornerRoundingRatio != 0)
            || (Shape == PcbFootprintPadShapeInput.Circle && WidthNm != HeightNm))
            throw PcbFootprintPlacementInput.Invalid("Circular pads need equal dimensions; only round rectangles accept a corner ratio between 0 and 0.5.");
        if (Layers is null || Layers.Count is 0 or > 96)
            throw PcbFootprintPlacementInput.Invalid("A pad needs an explicit, bounded layer set.");
        BoardLayer[] nativeLayers = Layers.Select(FootprintInputValidation.Layer).ToArray();
        BoardLayer[] copper = nativeLayers.Where(FootprintInputValidation.Copper).ToArray();
        if (copper.Length == 0 || nativeLayers.Distinct().Count() != nativeLayers.Length
            || nativeLayers.Any(layer => !FootprintInputValidation.Copper(layer)
                && layer is not (BoardLayer.BlFMask or BoardLayer.BlBMask or BoardLayer.BlFPaste or BoardLayer.BlBPaste)))
            throw PcbFootprintPlacementInput.Invalid("Pad layers must be distinct copper, paste or mask layers and include copper.");
        bool drilled = Type is PcbFootprintPadTypeInput.ThroughHole or PcbFootprintPadTypeInput.NonPlatedHole;
        if (drilled ? DrillWidthNm <= 0 || DrillHeightNm <= 0
            : DrillWidthNm != 0 || DrillHeightNm != 0)
            throw PcbFootprintPlacementInput.Invalid("Through holes need two positive drill dimensions; surface pads cannot have a drill.");
        if ((Type == PcbFootprintPadTypeInput.Smd || Type == PcbFootprintPadTypeInput.EdgeConnector)
            && (copper.Length != 1 || copper[0] is not (BoardLayer.BlFCu or BoardLayer.BlBCu)))
            throw PcbFootprintPlacementInput.Invalid("Surface pads need exactly one outer copper layer.");
        if (Type == PcbFootprintPadTypeInput.NonPlatedHole && NetName.Length != 0)
            throw PcbFootprintPlacementInput.Invalid("A nonplated mounting hole cannot carry a net.");
        var stack = new PadStack
        {
            Type = PadStackType.PstNormal, Angle = new() { ValueDegrees = RotationDegrees },
            UnconnectedLayerRemoval = UnconnectedLayerRemoval.UlrKeep
        };
        stack.Layers.Add(nativeLayers);
        stack.CopperLayers.Add(new PadStackLayer
        {
            Layer = copper[0], Shape = (PadStackShape)Shape,
            Size = new() { XNm = WidthNm, YNm = HeightNm }, CornerRoundingRatio = CornerRoundingRatio
        });
        if (drilled) stack.Drill = new()
        {
            StartLayer = BoardLayer.BlFCu, EndLayer = BoardLayer.BlBCu,
            Diameter = new() { XNm = DrillWidthNm, YNm = DrillHeightNm },
            Shape = DrillWidthNm == DrillHeightNm ? DrillShape.DsCircle : DrillShape.DsOblong
        };
        return new()
        {
            Id = new() { Value = Id }, Number = Number, Type = (PadType)Type,
            Position = new() { XNm = XNm, YNm = YNm }, PadStack = stack,
            Net = new() { Name = NetName }, Locked = LockedState.LsUnlocked
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintGraphicInput : NativeDocumentInputObject
{
    private string id = "";
    private PcbFootprintGraphicKind kind;
    private string layer = "F.SilkS";
    private long strokeWidthNm;
    private bool filled;
    private List<PcbFootprintPointInput>? points = [];
    public string Id { get => id; set => id = BindOnce(value); }
    public PcbFootprintGraphicKind Kind { get => kind; set => kind = BindOnce(value); }
    public string Layer { get => layer; set => layer = BindOnce(value); }
    public long StrokeWidthNm { get => strokeWidthNm; set => strokeWidthNm = BindOnce(value); }
    public bool Filled { get => filled; set => filled = BindOnce(value); }
    [Description("Local geometry: segment start/end, rectangle opposite corners, circle center/radius point, or arc start/mid/end.")]
    public List<PcbFootprintPointInput>? Points { get => points; set => points = BindOnce(value); }

    internal BoardGraphicShape ToNative()
    {
        FootprintInputValidation.Uuid(Id);
        if (!Enum.IsDefined(Kind) || Kind == PcbFootprintGraphicKind.Unknown || Points is null
            || Points.Count != (Kind == PcbFootprintGraphicKind.Arc ? 3 : 2) || Points.Any(point => point is null)
            || StrokeWidthNm < 0 || (StrokeWidthNm == 0 && !Filled)
            || (Filled && Kind is PcbFootprintGraphicKind.Segment or PcbFootprintGraphicKind.Arc))
            throw PcbFootprintPlacementInput.Invalid("A graphic needs exact non-null points, a positive stroke or closed fill, and a defined kind.");
        Vector2 first = Points[0].ToNative();
        Vector2 last = Points[^1].ToNative();
        if (first.Equals(last) || (Kind == PcbFootprintGraphicKind.Rectangle
            && (first.XNm == last.XNm || first.YNm == last.YNm)))
            throw PcbFootprintPlacementInput.Invalid("Footprint graphics cannot have zero length or area.");
        if (Kind == PcbFootprintGraphicKind.Arc)
        {
            Vector2 middle = Points[1].ToNative();
            BigInteger cross = ((BigInteger)middle.XNm - first.XNm) * ((BigInteger)last.YNm - first.YNm)
                - ((BigInteger)middle.YNm - first.YNm) * ((BigInteger)last.XNm - first.XNm);
            if (cross == 0) throw PcbFootprintPlacementInput.Invalid("Arc start, middle and end must not be collinear.");
        }
        var shape = new GraphicShape
        {
            Attributes = new()
            {
                Stroke = new() { Width = new() { ValueNm = StrokeWidthNm }, Style = StrokeLineStyle.SlsSolid },
                Fill = new() { FillType = Filled ? GraphicFillType.GftFilled : GraphicFillType.GftUnfilled }
            }
        };
        switch (Kind)
        {
            case PcbFootprintGraphicKind.Segment: shape.Segment = new() { Start = first, End = last }; break;
            case PcbFootprintGraphicKind.Rectangle: shape.Rectangle = new() { TopLeft = first, BottomRight = last }; break;
            case PcbFootprintGraphicKind.Circle: shape.Circle = new() { Center = first, RadiusPoint = last }; break;
            case PcbFootprintGraphicKind.Arc: shape.Arc = new() { Start = first, Mid = Points[1].ToNative(), End = last }; break;
        }
        return new() { Id = new() { Value = Id }, Layer = FootprintInputValidation.Layer(Layer),
            Shape = shape, Locked = LockedState.LsUnlocked };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbFootprintPointInput : NativeDocumentInputObject
{
    private long xNm;
    private long yNm;
    public long XNm { get => xNm; set => xNm = BindOnce(value); }
    public long YNm { get => yNm; set => yNm = BindOnce(value); }
    internal Kiapi.Common.Types.Vector2 ToNative() => new() { XNm = XNm, YNm = YNm };
}

[JsonConverter(typeof(JsonStringEnumConverter<PcbFootprintMountingInput>))]
public enum PcbFootprintMountingInput { ThroughHole = 1, Smd = 2, Unspecified = 3 }
[JsonConverter(typeof(JsonStringEnumConverter<PcbFootprintPadTypeInput>))]
public enum PcbFootprintPadTypeInput { Unknown = 0, ThroughHole = 1, Smd = 2, EdgeConnector = 3, NonPlatedHole = 4 }
[JsonConverter(typeof(JsonStringEnumConverter<PcbFootprintPadShapeInput>))]
public enum PcbFootprintPadShapeInput { Unknown = 0, Circle = 1, Rectangle = 2, Oval = 3, RoundRectangle = 5 }
[JsonConverter(typeof(JsonStringEnumConverter<PcbFootprintGraphicKind>))]
public enum PcbFootprintGraphicKind { Unknown = 0, Segment = 1, Rectangle = 2, Circle = 3, Arc = 4 }

internal static class FootprintInputValidation
{
    internal static void Uuid(string? value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid uuid) || uuid == Guid.Empty || value != uuid.ToString("D"))
            throw PcbFootprintPlacementInput.Invalid("Every footprint item identity must be a nonempty canonical lowercase UUID.");
    }

    internal static void Angle(double degrees)
    {
        if (!double.IsFinite(degrees) || Math.Abs(degrees) > 360_000)
            throw PcbFootprintPlacementInput.Invalid("Rotation must be finite and within +/-360000 degrees.");
    }

    internal static bool Copper(BoardLayer layer) => (int)layer >= (int)BoardLayer.BlFCu
        && (int)layer <= (int)BoardLayer.BlBCu;

    internal static BoardLayer Layer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw PcbFootprintPlacementInput.Invalid("A layer name is required.");
        string compact = string.Concat(name.Where(char.IsLetterOrDigit));
        string? value = Enum.GetNames<BoardLayer>().FirstOrDefault(candidate =>
            string.Concat(candidate[2..].Where(char.IsLetterOrDigit)).Equals(compact, StringComparison.OrdinalIgnoreCase));
        if (value is null || !Enum.TryParse(value, out BoardLayer layer)
            || (int)layer < (int)BoardLayer.BlFCu || layer == BoardLayer.BlRescue)
            throw PcbFootprintPlacementInput.Invalid("Unknown footprint layer: " + name);
        return layer;
    }
}
