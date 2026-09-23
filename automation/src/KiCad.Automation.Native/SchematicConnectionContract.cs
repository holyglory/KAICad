using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// Frozen CN-1 records (automation/design/contracts/cn1-wiring-intent.md §5.9).
// Intent comes only from exact Circuit.Nets pin identities; label texts are outputs,
// never identities. Changing any shape or code here is a contract revision.

public enum ConnectionScope { Local = 1, Global = 2 }

public enum ConnectionMemberRole { Signal = 1, PowerCarrier = 2, ImplicitPower = 3 }

public sealed record ConnectionPinKey(string SheetPathKey, Guid PlacedPinId);

public sealed record ConnectionPlacedPin(PinEndpoint Endpoint, Guid SymbolOccurrenceId, Guid SheetInstanceId,
    string SheetPathKey, Guid ScreenId, Guid SymbolId, Guid PlacedPinId, Guid LibraryPinId, bool CreatedSymbol);

public sealed record ConnectionMember(ConnectionPlacedPin Pin, ConnectionMemberRole Role, bool AlreadyConnected,
    string? PowerName)
{
    public bool RequiresStub => Role == ConnectionMemberRole.Signal && !AlreadyConnected;
}

public sealed record ConnectionIsland(Guid NetId, Guid SheetInstanceId, string SheetPathKey, Guid ScreenId,
    ConnectionScope Scope, string LabelText, IReadOnlyList<ConnectionMember> Members,
    IReadOnlyList<Guid> AnchorItemIds, bool AnchorHasMatchingDriver, bool JoinRequired,
    IReadOnlyList<ConnectionPlacedPin> JoinCandidates, Guid? UplinkSheetSymbolId,
    IReadOnlyList<Guid> ChildSheetSymbolIds);

public sealed record ConnectionPort(Guid NetId, Guid ChildSheetInstanceId, string ChildPathKey, Guid ChildScreenId,
    Guid ParentSheetInstanceId, string ParentPathKey, Guid ParentScreenId, Guid SheetSymbolId, string PortText,
    bool SheetPinExists, bool UplinkLabelExists);

public sealed record ConnectionNet(Guid NetId, string Name, ConnectionScope Scope, string? GlobalName,
    IReadOnlyList<PinEndpoint> AddedPins);

/// <summary>One physical screen. <paramref name="InstancePathKeys"/> are ordinal; the first is
/// the representative path whose islands need realization.</summary>
public sealed record ConnectionScreen(Guid ScreenId, IReadOnlyList<string> InstancePathKeys,
    IReadOnlyList<ConnectionIsland> Islands);

/// <summary>Pure planner output for an admitted connected addition. <paramref name="DesiredSha256"/>
/// is the lowercase hex SHA-256 of the desired file bytes.</summary>
public sealed record SchematicConnectionIntent(int Version, Guid OriginId, Guid CircuitId,
    KiCad.Automation.Model.DocumentRevision NativeRevision, string DesiredSha256,
    IReadOnlyList<ConnectionNet> Nets, IReadOnlyList<ConnectionScreen> Screens, IReadOnlyList<ConnectionPort> Ports,
    IReadOnlyList<IReadOnlyList<ConnectionPinKey>> ExpectedGroups, IReadOnlyList<Guid> CreatedSymbolIds)
{
    public const int CurrentVersion = 1;
}
