using System.ComponentModel;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingExportInput
{
    [Description("Caller-owned canonical operation UUID. Reuse this exact UUID and request after a lost reply.")]
    public string OperationId { get; set; } = "";
    public NativeDocumentInput? Document { get; set; }
    public DocumentLifecycleInput? ExpectedState { get; set; }
    public string OutputDirectory { get; set; } = "";
    public PcbManufacturingJobInput? Job { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbManufacturingJobInput
{
    public RunBoardJobExport3DInput? Export3D { get; set; }
    public RunBoardJobExportRenderInput? ExportRender { get; set; }
    public RunBoardJobExportSvgInput? ExportSvg { get; set; }
    public RunBoardJobExportDxfInput? ExportDxf { get; set; }
    public RunBoardJobExportPdfInput? ExportPdf { get; set; }
    public RunBoardJobExportPsInput? ExportPs { get; set; }
    public RunBoardJobExportGerbersInput? ExportGerbers { get; set; }
    public RunBoardJobExportDrillInput? ExportDrill { get; set; }
    public RunBoardJobExportPositionInput? ExportPosition { get; set; }
    public RunBoardJobExportGencadInput? ExportGencad { get; set; }
    public RunBoardJobExportIpc2581Input? ExportIpc2581 { get; set; }
    public RunBoardJobExportIpcD356Input? ExportIpcD356 { get; set; }
    public RunBoardJobExportODBInput? ExportOdb { get; set; }
    public RunBoardJobExportStatsInput? ExportStats { get; set; }

    internal void ApplyTo(RunPcbManufacturingExport request)
    {
        int count = (Export3D is null ? 0 : 1) + (ExportRender is null ? 0 : 1)
            + (ExportSvg is null ? 0 : 1) + (ExportDxf is null ? 0 : 1)
            + (ExportPdf is null ? 0 : 1) + (ExportPs is null ? 0 : 1)
            + (ExportGerbers is null ? 0 : 1) + (ExportDrill is null ? 0 : 1)
            + (ExportPosition is null ? 0 : 1) + (ExportGencad is null ? 0 : 1)
            + (ExportIpc2581 is null ? 0 : 1) + (ExportIpcD356 is null ? 0 : 1)
            + (ExportOdb is null ? 0 : 1) + (ExportStats is null ? 0 : 1);
        if (count != 1)
            throw new AutomationException("invalid_pcb_export_job", "Choose exactly one concrete PCB manufacturing job setting.");

        if (Export3D is not null) request.Export3D = Export3D.ToNative();
        else if (ExportRender is not null) request.ExportRender = ExportRender.ToNative();
        else if (ExportSvg is not null) request.ExportSvg = ExportSvg.ToNative();
        else if (ExportDxf is not null) request.ExportDxf = ExportDxf.ToNative();
        else if (ExportPdf is not null) request.ExportPdf = ExportPdf.ToNative();
        else if (ExportPs is not null) request.ExportPs = ExportPs.ToNative();
        else if (ExportGerbers is not null) request.ExportGerbers = ExportGerbers.ToNative();
        else if (ExportDrill is not null) request.ExportDrill = ExportDrill.ToNative();
        else if (ExportPosition is not null) request.ExportPosition = ExportPosition.ToNative();
        else if (ExportGencad is not null) request.ExportGencad = ExportGencad.ToNative();
        else if (ExportIpc2581 is not null) request.ExportIpc2581 = ExportIpc2581.ToNative();
        else if (ExportIpcD356 is not null) request.ExportIpcD356 = ExportIpcD356.ToNative();
        else if (ExportOdb is not null) request.ExportOdb = ExportOdb.ToNative();
        else request.ExportStats = ExportStats!.ToNative();
    }
}
