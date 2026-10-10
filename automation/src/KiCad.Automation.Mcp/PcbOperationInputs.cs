using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Board.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured input for PCB netlist import.  The native operation ID remains
/// an outer MCP argument so the same ID can be used for receipt replay.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbImportNetlistInput : NativeDocumentInputObject
{
    private NativeDocumentInput? board;
    private string? netlistPath;
    private bool dryRun;
    private PcbNetlistMatchModeInput matchMode;
    private bool deleteExtraFootprints;
    private bool updateFootprints;
    private bool transferGroups;
    private bool overrideLocks;

    public NativeDocumentInput? Board { get => board; set => board = BindOnce(value); }
    public string? NetlistPath { get => netlistPath; set => netlistPath = BindOnce(value); }
    public bool DryRun { get => dryRun; set => dryRun = BindOnce(value); }
    public PcbNetlistMatchModeInput MatchMode { get => matchMode; set => matchMode = BindOnce(value); }
    public bool DeleteExtraFootprints { get => deleteExtraFootprints; set => deleteExtraFootprints = BindOnce(value); }
    public bool UpdateFootprints { get => updateFootprints; set => updateFootprints = BindOnce(value); }
    public bool TransferGroups { get => transferGroups; set => transferGroups = BindOnce(value); }
    public bool OverrideLocks { get => overrideLocks; set => overrideLocks = BindOnce(value); }

    internal ImportNetlist ToNative(string operationId)
    {
        DocumentSpecifier target = NativeDocumentInput.ToNative(Board);
        if (target.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_netlist_target", "board must identify a PCB document.");
        if (!Enum.IsDefined(MatchMode))
            throw new AutomationException("invalid_pcb_netlist_request", "matchMode must be a defined PCB netlist match mode.");
        return new ImportNetlist
        {
            Board = target,
            NetlistPath = NetlistPath ?? "",
            DryRun = DryRun,
            MatchMode = (NetlistMatchMode)(int)MatchMode,
            DeleteExtraFootprints = DeleteExtraFootprints,
            UpdateFootprints = UpdateFootprints,
            TransferGroups = TransferGroups,
            OverrideLocks = OverrideLocks,
            OperationId = operationId ?? ""
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PcbNetlistMatchModeInput>))]
public enum PcbNetlistMatchModeInput
{
    [JsonStringEnumMemberName("NMM_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("NMM_UUID")] Uuid = 1,
    [JsonStringEnumMemberName("NMM_REFERENCE")] Reference = 2
}

/// <summary>Structured input for synchronous selected-zone and asynchronous all-zone refill.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRefillZonesInput : NativeDocumentInputObject
{
    private NativeDocumentInput? board;
    private List<NativeUuidInput?> zones = [];

    public NativeDocumentInput? Board { get => board; set => board = BindOnce(value); }
    public List<NativeUuidInput?> Zones
    {
        get => zones;
        set => zones = BindOnce(value ?? throw new JsonException("zones cannot be null."));
    }

    internal RefillZones ToNative(string operationId)
    {
        DocumentSpecifier target = NativeDocumentInput.ToNative(Board);
        if (target.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_refill_target", "board must identify a PCB document.");
        if (Zones.Count > 4096 || Zones.Any(zone => zone is null
            || !Guid.TryParseExact(zone.Value, "D", out var id) || id == Guid.Empty
            || zone.Value != id.ToString("D")))
            throw new AutomationException("invalid_pcb_refill_request", "zones must contain at most 4096 canonical zone KIIDs.");
        var request = new RefillZones { Board = target, OperationId = operationId ?? "" };
        request.Zones.Add(Zones.Select(zone => new KIID { Value = zone!.Value! }));
        return request;
    }
}
