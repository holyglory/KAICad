using System.ComponentModel;
using System.Text.Json.Serialization;
using Kiapi.Board.Jobs;
using Kiapi.Common.Types;
using KiCad.Automation.Protocol;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// The outer input is deliberately a typed discriminated union. Concrete board
// job messages retain their existing fields and writable protobuf collections;
// the envelope owns document, lifecycle, operation and publication identity.
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
    public RunBoardJobExport3D? Export3D { get; set; }
    public RunBoardJobExportRender? ExportRender { get; set; }
    public RunBoardJobExportSvg? ExportSvg { get; set; }
    public RunBoardJobExportDxf? ExportDxf { get; set; }
    public RunBoardJobExportPdf? ExportPdf { get; set; }
    public RunBoardJobExportPs? ExportPs { get; set; }
    public RunBoardJobExportGerbers? ExportGerbers { get; set; }
    public RunBoardJobExportDrill? ExportDrill { get; set; }
    public RunBoardJobExportPosition? ExportPosition { get; set; }
    public RunBoardJobExportGencad? ExportGencad { get; set; }
    public RunBoardJobExportIpc2581? ExportIpc2581 { get; set; }
    public RunBoardJobExportIpcD356? ExportIpcD356 { get; set; }
    public RunBoardJobExportODB? ExportOdb { get; set; }
    public RunBoardJobExportStats? ExportStats { get; set; }

    internal void ApplyTo(RunPcbManufacturingExport request,
        Kiapi.Common.Types.DocumentSpecifier document)
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

        if (Export3D is not null) request.Export3D = Prepare(Export3D, document, value => value.JobSettings);
        else if (ExportRender is not null) request.ExportRender = Prepare(ExportRender, document, value => value.JobSettings);
        else if (ExportSvg is not null) request.ExportSvg = Prepare(ExportSvg, document, value => value.JobSettings);
        else if (ExportDxf is not null) request.ExportDxf = Prepare(ExportDxf, document, value => value.JobSettings);
        else if (ExportPdf is not null) request.ExportPdf = Prepare(ExportPdf, document, value => value.JobSettings);
        else if (ExportPs is not null) request.ExportPs = Prepare(ExportPs, document, value => value.JobSettings);
        else if (ExportGerbers is not null) request.ExportGerbers = Prepare(ExportGerbers, document, value => value.JobSettings);
        else if (ExportDrill is not null) request.ExportDrill = Prepare(ExportDrill, document, value => value.JobSettings);
        else if (ExportPosition is not null) request.ExportPosition = Prepare(ExportPosition, document, value => value.JobSettings);
        else if (ExportGencad is not null) request.ExportGencad = Prepare(ExportGencad, document, value => value.JobSettings);
        else if (ExportIpc2581 is not null) request.ExportIpc2581 = Prepare(ExportIpc2581, document, value => value.JobSettings);
        else if (ExportIpcD356 is not null) request.ExportIpcD356 = Prepare(ExportIpcD356, document, value => value.JobSettings);
        else if (ExportOdb is not null) request.ExportOdb = Prepare(ExportOdb, document, value => value.JobSettings);
        else request.ExportStats = Prepare(ExportStats!, document, value => value.JobSettings);
    }

    private static T Prepare<T>(T source, DocumentSpecifier document,
        Func<T, RunJobSettings> settings)
        where T : class, Google.Protobuf.IMessage<T>, new()
    {
        T result = source.Clone();
        settings(result).Document = document.Clone();
        settings(result).OutputPath = "";
        return result;
    }
}
