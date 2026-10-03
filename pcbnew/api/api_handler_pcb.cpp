/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2023 Jon Evans <jon@craftyjon.com>
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#include <magic_enum.hpp>
#include <cerrno>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <unordered_map>
#include <properties/property.h>

#include <common.h>
#include <fmt.h>
#include <api/api_handler_pcb.h>
#include <api/api_pcb_utils.h>
#include <api/api_enums.h>
#include <api/api_utils.h>
#include <api/api_server.h>
#include <api/native_state_digest.h>
#include <google/protobuf/util/message_differencer.h>
#include <api/native_file_observation.h>
#include <api/document_lifecycle_controller.h>
#include <project/project_file.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <json_common.h>
#include <api/cross_probe_client.h>
#include <wx/log.h>
#include <wx/weakref.h>
#include <base_screen.h>
#include <board_commit.h>
#include <board_connected_item.h>
#include <board_design_settings.h>
#include <core/kicad_algo.h>
#include <footprint.h>
#include <kicad_clipboard.h>
#include <netinfo.h>
#include <pad.h>
#include <pcb_draw_panel_gal.h>
#include <api/pcb_drc_run_inputs.h>
#include <api/pcb_drc_document_snapshot.h>
#include <drc/drc_library_inputs.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_proxy_view_item.h>
#include <project_pcb.h>
#include <pcb_edit_frame.h>
#include <pgm_base.h>
#include <pcb_group.h>
#include <pcb_reference_image.h>
#include <pcb_shape.h>
#include <pcb_text.h>
#include <pcb_textbox.h>
#include <pcb_track.h>
#include <pcbnew_id.h>
#include <pcbnew_settings.h>
#include <pcb_project_editor_state.h>
#include <pcb_marker.h>
#include <pcb_point.h>
#include <kiway.h>
#include <drc/drc_item.h>
#include <jobs/job_export_pcb_3d.h>
#include <jobs/job_export_pcb_dxf.h>
#include <jobs/job_export_pcb_drill.h>
#include <jobs/job_export_pcb_gencad.h>
#include <jobs/job_export_pcb_gerber.h>
#include <jobs/job_export_pcb_gerbers.h>
#include <jobs/job_export_pcb_ipc2581.h>
#include <jobs/job_export_pcb_ipcd356.h>
#include <jobs/job_export_pcb_odb.h>
#include <jobs/job_export_pcb_pdf.h>
#include <jobs/job_export_pcb_pos.h>
#include <jobs/job_export_pcb_ps.h>
#include <jobs/job_export_pcb_stats.h>
#include <jobs/job_export_pcb_svg.h>
#include <pcb_plot_params.h>
#include <jobs/job_pcb_render.h>
#include <layer_ids.h>
#include <netlist_reader/board_netlist_updater.h>
#include <netlist_reader/pcb_netlist.h>
#include <project.h>
#include <tool/actions.h>
#include <tool/tool_manager.h>
#include <tools/drc_tool.h>
#include <tools/pcb_actions.h>
#include <tools/pcb_selection_tool.h>
#include <tools/zone_filler_tool.h>
#include <tools/generator_tool.h>
#include <router/router_tool.h>
#include <router/pns_kicad_iface.h>
#include <router/pns_segment.h>
#include <router/pns_arc.h>
#include <router/pns_via.h>
#include <router/pns_diff_pair.h>
#include <router/pns_line.h>
#include <router/pns_routing_settings.h>
#include <pcbnew_settings.h>
#include <zone.h>
#include <zone_filler.h>

#include <api/common/types/base_types.pb.h>
#include <api/board/board_rules.pb.h>
#include <connectivity/connectivity_data.h>
#include <google/protobuf/util/json_util.h>
#include <drc/drc_rule_condition.h>
#include <drc/drc_rule_parser.h>
#include <widgets/appearance_controls.h>
#include <widgets/report_severity.h>
#include <drc/rule_editor/drc_re_rule_loader.h>
#include <trace_helpers.h>
#include <wx/ffile.h>

#if defined( __linux__ )
#include <fcntl.h>
#include <linux/fs.h>
#include <sys/syscall.h>
#include <unistd.h>
#endif

using namespace kiapi::common::commands;

KICAD_API_SERVER& API_HANDLER_PCB::drcApiServer() const
{
    return apiServerOrNull() ? apiServer() : Pgm().GetApiServer();
}
using types::CommandStatus;
using types::DocumentType;
using types::ItemRequestStatus;


namespace
{
namespace fs = std::filesystem;
using namespace kiapi::automation::v1;


ApiResponseStatus ManufacturingError( const std::string& aMessage,
                                       ApiStatusCode aStatus = ApiStatusCode::AS_BAD_REQUEST )
{
    ApiResponseStatus error;
    error.set_status( aStatus );
    error.set_error_message( aMessage );
    return error;
}


bool IsOperationId( const std::string& aOperationId )
{
    return aOperationId.size() == 36
        && KIID::SniffTest( wxString::FromUTF8( aOperationId ) );
}


wxString WxPath( const fs::path& aPath )
{
    return wxString::FromUTF8( aPath.generic_string() );
}


bool ExistingPath( const fs::path& aPath, std::string& aReason )
{
    std::error_code error;
    const fs::file_status status = fs::symlink_status( aPath, error );
    if( error )
    {
        if( error == std::errc::no_such_file_or_directory )
            return false;
        aReason = error.message();
        return true;
    }

    if( status.type() != fs::file_type::not_found )
    {
        aReason = "the output or staging path already exists";
        return true;
    }

    return false;
}


bool ValidatePayloadMetadata( const RunPcbManufacturingExport& aRequest, std::string& aReason )
{
    auto check = [&]( const auto& aPayload )
    {
        if( aPayload.has_job_settings() )
        {
            if( !aPayload.job_settings().output_path().empty() )
            {
                aReason = "concrete job output_path must be empty; outputDirectory owns publication";
                return false;
            }
            if( aPayload.job_settings().has_document()
                && !google::protobuf::util::MessageDifferencer::Equivalent(
                        aPayload.job_settings().document(), aRequest.document() ) )
            {
                aReason = "concrete job document contradicts the envelope document";
                return false;
            }
        }
        return true;
    };

    switch( aRequest.job_case() )
    {
    case RunPcbManufacturingExport::kExport3d: return check( aRequest.export_3d() );
    case RunPcbManufacturingExport::kExportRender: return check( aRequest.export_render() );
    case RunPcbManufacturingExport::kExportSvg: return check( aRequest.export_svg() );
    case RunPcbManufacturingExport::kExportDxf: return check( aRequest.export_dxf() );
    case RunPcbManufacturingExport::kExportPdf: return check( aRequest.export_pdf() );
    case RunPcbManufacturingExport::kExportPs: return check( aRequest.export_ps() );
    case RunPcbManufacturingExport::kExportGerbers: return check( aRequest.export_gerbers() );
    case RunPcbManufacturingExport::kExportDrill: return check( aRequest.export_drill() );
    case RunPcbManufacturingExport::kExportPosition: return check( aRequest.export_position() );
    case RunPcbManufacturingExport::kExportGencad: return check( aRequest.export_gencad() );
    case RunPcbManufacturingExport::kExportIpc2581: return check( aRequest.export_ipc2581() );
    case RunPcbManufacturingExport::kExportIpcD356: return check( aRequest.export_ipc_d356() );
    case RunPcbManufacturingExport::kExportOdb: return check( aRequest.export_odb() );
    case RunPcbManufacturingExport::kExportStats: return check( aRequest.export_stats() );
    case RunPcbManufacturingExport::JOB_NOT_SET: break;
    }
    aReason = "exactly one concrete job is required";
    return false;
}


bool AtomicPublishNoReplaceDirectory( const fs::path& aStaging, const fs::path& aOutput,
                                      std::string& aReason )
{
#if defined( __linux__ ) && defined( SYS_renameat2 ) && defined( RENAME_NOREPLACE )
    if( ::syscall( SYS_renameat2, AT_FDCWD, aStaging.c_str(), AT_FDCWD, aOutput.c_str(),
                   RENAME_NOREPLACE ) == 0 )
    {
        return true;
    }

    aReason = std::strerror( errno );
    return false;
#else
    (void) aStaging;
    (void) aOutput;
    aReason = "atomic no-replace directory publication is unavailable on this platform";
    return false;
#endif
}


bool NativeManufacturingPublicationAvailable()
{
#if defined( __linux__ ) && defined( SYS_renameat2 ) && defined( RENAME_NOREPLACE )
    return true;
#else
    return false;
#endif
}


std::string ManufacturingJobKind( const RunPcbManufacturingExport& aRequest )
{
    switch( aRequest.job_case() )
    {
    case RunPcbManufacturingExport::kExport3d: return "3d";
    case RunPcbManufacturingExport::kExportRender: return "render";
    case RunPcbManufacturingExport::kExportSvg: return "svg";
    case RunPcbManufacturingExport::kExportDxf: return "dxf";
    case RunPcbManufacturingExport::kExportPdf: return "pdf";
    case RunPcbManufacturingExport::kExportPs: return "ps";
    case RunPcbManufacturingExport::kExportGerbers: return "gerbers";
    case RunPcbManufacturingExport::kExportDrill: return "drill";
    case RunPcbManufacturingExport::kExportPosition: return "position";
    case RunPcbManufacturingExport::kExportGencad: return "gencad";
    case RunPcbManufacturingExport::kExportIpc2581: return "ipc2581";
    case RunPcbManufacturingExport::kExportIpcD356: return "ipc-d356";
    case RunPcbManufacturingExport::kExportOdb: return "odb";
    case RunPcbManufacturingExport::kExportStats: return "stats";
    case RunPcbManufacturingExport::JOB_NOT_SET: return {};
    }
    return {};
}


bool ManufacturingOutputIsDirectory( const RunPcbManufacturingExport& aRequest )
{
    switch( aRequest.job_case() )
    {
    case RunPcbManufacturingExport::kExportGerbers:
    case RunPcbManufacturingExport::kExportDrill:
        return true;
    case RunPcbManufacturingExport::kExportOdb:
        return aRequest.export_odb().compression() == kiapi::board::jobs::OdbCompression::ODBC_NONE;
    default:
        return false;
    }
}


std::string ManufacturingOutputExtension( const RunPcbManufacturingExport& aRequest )
{
    switch( aRequest.job_case() )
    {
    case RunPcbManufacturingExport::kExport3d:
        switch( aRequest.export_3d().format() )
        {
        case kiapi::board::jobs::Board3DFormat::B3D_STEP: return "step";
        case kiapi::board::jobs::Board3DFormat::B3D_STEPZ: return "stpz";
        case kiapi::board::jobs::Board3DFormat::B3D_BREP: return "brep";
        case kiapi::board::jobs::Board3DFormat::B3D_XAO: return "xao";
        case kiapi::board::jobs::Board3DFormat::B3D_GLB: return "glb";
        case kiapi::board::jobs::Board3DFormat::B3D_VRML: return "wrl";
        case kiapi::board::jobs::Board3DFormat::B3D_PLY: return "ply";
        case kiapi::board::jobs::Board3DFormat::B3D_STL: return "stl";
        case kiapi::board::jobs::Board3DFormat::B3D_U3D: return "u3d";
        case kiapi::board::jobs::Board3DFormat::B3D_PDF: return "pdf";
        default: return "out";
        }
    case RunPcbManufacturingExport::kExportRender:
        return aRequest.export_render().format() == kiapi::board::jobs::RenderFormat::RF_JPEG ? "jpg" : "png";
    case RunPcbManufacturingExport::kExportSvg: return "svg";
    case RunPcbManufacturingExport::kExportDxf: return "dxf";
    case RunPcbManufacturingExport::kExportPdf: return "pdf";
    case RunPcbManufacturingExport::kExportPs: return "ps";
    case RunPcbManufacturingExport::kExportPosition:
        switch( aRequest.export_position().format() )
        {
        case kiapi::board::jobs::PositionFormat::PF_CSV: return "csv";
        case kiapi::board::jobs::PositionFormat::PF_GERBER: return "gbr";
        default: return "pos";
        }
    case RunPcbManufacturingExport::kExportGencad: return "cad";
    case RunPcbManufacturingExport::kExportIpc2581: return "xml";
    case RunPcbManufacturingExport::kExportIpcD356: return "d356";
    case RunPcbManufacturingExport::kExportOdb:
        switch( aRequest.export_odb().compression() )
        {
        case kiapi::board::jobs::OdbCompression::ODBC_TGZ: return "tgz";
        case kiapi::board::jobs::OdbCompression::ODBC_NONE: return {};
        default: return "zip";
        }
    case RunPcbManufacturingExport::kExportStats:
        return aRequest.export_stats().format() == kiapi::board::jobs::StatsOutputFormat::SOF_JSON ? "json" : "rpt";
    default: return "out";
    }
}


bool InventoryManufacturingArtifacts( const fs::path& aRoot,
                                      PcbManufacturingExportReceipt& aReceipt,
                                      std::string& aReason )
{
    std::error_code error;
    std::vector<fs::path> files;
    fs::recursive_directory_iterator it( aRoot, error );
    if( error )
    {
        aReason = error.message();
        return false;
    }
    for( fs::recursive_directory_iterator end; it != end; it.increment( error ) )
    {
        if( error )
        {
            aReason = error.message();
            return false;
        }

        const fs::file_status status = it->symlink_status( error );
        if( error || status.type() == fs::file_type::symlink )
        {
            aReason = "staged output contains an unreadable or symlinked path";
            return false;
        }
        if( status.type() == fs::file_type::regular ) files.push_back( it->path() );
        else if( status.type() != fs::file_type::directory )
        {
            aReason = "staged output contains a non-regular file";
            return false;
        }
    }
    if( error )
    {
        aReason = error.message();
        return false;
    }

    std::sort( files.begin(), files.end(), []( const fs::path& aLeft, const fs::path& aRight )
    {
        return aLeft.generic_string() < aRight.generic_string();
    } );
    if( files.empty() || files.size() > 4096 )
    {
        aReason = "the export produced no files or too many files";
        return false;
    }

    NATIVE_STATE_DIGEST manifest;
    uint64_t totalBytes = 0;
    for( const fs::path& file : files )
    {
        const fs::path relative = file.lexically_relative( aRoot );
        const std::string relativeText = relative.generic_string();
        if( relative.empty() || relative.is_absolute()
            || ( relativeText.size() >= 2 && relativeText.compare( 0, 2, ".." ) == 0 ) )
        {
            aReason = "staged output escaped its owned directory";
            return false;
        }
        const FILE_CONTENT_BASELINE baseline = FILE_CONTENT_BASELINE::Read( WxPath( file ) );
        if( !baseline.Known() || !baseline.Exists() )
        {
            aReason = "staged output could not be hashed";
            return false;
        }
        totalBytes += baseline.Bytes();
        if( baseline.Bytes() > 512ULL * 1024 * 1024 || totalBytes > 2ULL * 1024 * 1024 * 1024 )
        {
            aReason = "staged output exceeds the native artifact budget";
            return false;
        }
        manifest.Append( relative.generic_string() );
        manifest.Append( baseline.Sha256() );
        PcbManufacturingArtifact* artifact = aReceipt.add_artifacts();
        artifact->set_path( WxPath( file ).ToStdString() );
        artifact->set_relative_path( relative.generic_string() );
        artifact->set_bytes( baseline.Bytes() );
        artifact->set_sha256( baseline.Sha256() );
        artifact->set_ownership( PcbManufacturingArtifactOwnership::PCB_MFG_ARTIFACT_CREATED );
    }
    aReceipt.set_output_manifest_complete( true );
    aReceipt.set_output_manifest_sha256( manifest.Hex() );
    return true;
}
}


API_HANDLER_PCB::API_HANDLER_PCB( PCB_EDIT_FRAME* aFrame ) :
        API_HANDLER_PCB( CreatePcbFrameContext( aFrame ), aFrame )
{
}


API_HANDLER_PCB::API_HANDLER_PCB( std::shared_ptr<PCB_CONTEXT> aContext, PCB_EDIT_FRAME* aFrame ) :
        API_HANDLER_BOARD( std::move( aContext ), aFrame ),
        m_drcJobs( [this]( BOARD& source ) { return observeDrcAuxiliary( source ); } )
{
    refreshCustomRulesBaseline();
    // Only the editor frame detaches before it replaces or destroys its board.
    if( aFrame )
    {
        m_drcJobs.EnableNativeEvents( [this]( BOARD& source ) -> FOOTPRINT_LIBRARY_ADAPTER*
        {
            return &source == board() ? PROJECT_PCB::FootprintLibAdapter( &project() ) : nullptr;
        } );
    }

    registerHandler<GetOpenDocuments, GetOpenDocumentsResponse>(
            &API_HANDLER_PCB::handleGetOpenDocuments );
    registerHandler<SaveDocument, Empty>( &API_HANDLER_PCB::handleSaveDocument );
    registerHandler<kiapi::automation::v1::ReadDocumentLifecycleState, kiapi::automation::v1::DocumentLifecycleState>(
            &API_HANDLER_PCB::handleReadLifecycleState );
    registerHandler<kiapi::automation::v1::ReadPcbDrcState, kiapi::automation::v1::PcbDrcState>(
            &API_HANDLER_PCB::handleReadDrcState );
    // The manager runs against a detached board/project snapshot, so these
    // jobs do not hold or mutate the live editor while DRC is running.
    registerHandler<kiapi::automation::v1::StartPcbDrcJob, kiapi::automation::v1::PcbDrcJobState>(
            &API_HANDLER_PCB::handleStartDrcJob );
    registerHandler<kiapi::automation::v1::ReadPcbDrcJob, kiapi::automation::v1::PcbDrcJobState>(
            &API_HANDLER_PCB::handleReadDrcJob );
    registerHandler<kiapi::automation::v1::NativeCapabilityPcbDrcReplacementJob, kiapi::automation::v1::PcbDrcJobState>(
            &API_HANDLER_PCB::handleReadDrcReplacementJob );
    registerHandler<kiapi::automation::v1::CancelPcbDrcJob, kiapi::automation::v1::PcbDrcJobState>(
            &API_HANDLER_PCB::handleCancelDrcJob );
    registerHandler<kiapi::automation::v1::StartPcbRoutePreview, kiapi::automation::v1::PcbRoutePreviewState>(
            &API_HANDLER_PCB::handleRoutePreview );
    registerHandler<kiapi::automation::v1::NativeCapabilityRoutePreviewContinue, kiapi::automation::v1::PcbRoutePreviewState>(
            &API_HANDLER_PCB::handleContinueRoutePreview );
    registerHandler<kiapi::automation::v1::NativeCapabilityRoutePreviewCancel, kiapi::automation::v1::PcbRoutePreviewState>(
            &API_HANDLER_PCB::handleCancelRoutePreview );
    registerHandler<kiapi::automation::v1::NativeCapabilityRoutePreviewRead, kiapi::automation::v1::PcbRoutePreviewState>(
            &API_HANDLER_PCB::handleReadRoutePreview );
    registerHandler<SaveCopyOfDocument, Empty>( &API_HANDLER_PCB::handleSaveCopyOfDocument );
    registerHandler<RevertDocument, Empty>( &API_HANDLER_PCB::handleRevertDocument );

    registerHandler<GetItems, GetItemsResponse>( &API_HANDLER_PCB::handleGetItems );

    registerHandler<SetBoardEnabledLayers, BoardEnabledLayersResponse>( &API_HANDLER_PCB::handleSetBoardEnabledLayers );
    registerHandler<GetBoardDesignRules, BoardDesignRulesResponse>( &API_HANDLER_PCB::handleGetBoardDesignRules );
    registerHandler<SetBoardDesignRules, BoardDesignRulesResponse>( &API_HANDLER_PCB::handleSetBoardDesignRules );
    registerHandler<GetCustomDesignRules, CustomRulesResponse>( &API_HANDLER_PCB::handleGetCustomDesignRules );
    registerHandler<SetCustomDesignRules, CustomRulesResponse>( &API_HANDLER_PCB::handleSetCustomDesignRules );
    registerHandler<GetEmbeddedFiles, common::types::EmbeddedFiles>( &API_HANDLER_PCB::handleGetEmbeddedFiles );
    registerHandler<AddEmbeddedFiles, Empty>( &API_HANDLER_PCB::handleAddEmbeddedFiles );
    registerHandler<SetEmbeddedFiles, Empty>( &API_HANDLER_PCB::handleSetEmbeddedFiles );
    registerHandler<GetBoardOrigin, types::Vector2>( &API_HANDLER_PCB::handleGetBoardOrigin );
    registerHandler<SetBoardOrigin, Empty>( &API_HANDLER_PCB::handleSetBoardOrigin );
    registerHandler<GetBoardLayerName, BoardLayerNameResponse>( &API_HANDLER_PCB::handleGetBoardLayerName );
    registerHandler<GetBoardLayerByName, BoardLayerResponse>( &API_HANDLER_PCB::handleGetBoardLayerByName );

    registerHandler<GetNets, NetsResponse>( &API_HANDLER_PCB::handleGetNets );
    registerHandler<GetConnectedItems, GetItemsResponse>( &API_HANDLER_PCB::handleGetConnectedItems );
    registerHandler<GetItemsByNet, GetItemsResponse>( &API_HANDLER_PCB::handleGetItemsByNet );
    registerHandler<GetItemsByNetClass, GetItemsResponse>( &API_HANDLER_PCB::handleGetItemsByNetClass );
    registerHandler<GetNetClassForNets, NetClassForNetsResponse>(
            &API_HANDLER_PCB::handleGetNetClassForNets );
    registerHandler<RefillZones, Empty>( &API_HANDLER_PCB::handleRefillZones );
    registerHandler<ImportNetlist, ImportNetlistResponse>( &API_HANDLER_PCB::handleImportNetlist );
    registerHandler<kiapi::automation::v1::NativeCapabilityPcbReadOperation,
                    kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse>(
            &API_HANDLER_PCB::handleReadPcbOperation );
    registerHandler<kiapi::automation::v1::RunPcbManufacturingExport,
                    kiapi::automation::v1::PcbManufacturingExportReceipt>(
            &API_HANDLER_PCB::handleRunPcbManufacturingExport );
    registerHandler<kiapi::automation::v1::ReadPcbManufacturingExport,
                    kiapi::automation::v1::PcbManufacturingExportReceiptResponse>(
            &API_HANDLER_PCB::handleReadPcbManufacturingExport );

    registerHandler<GetBoardEditorAppearanceSettings, BoardEditorAppearanceSettings>(
            &API_HANDLER_PCB::handleGetBoardEditorAppearanceSettings );
    registerHandler<SetBoardEditorAppearanceSettings, Empty>(
            &API_HANDLER_PCB::handleSetBoardEditorAppearanceSettings );
    registerHandler<GetBoardPlotSettings, BoardPlotSettingsResponse>( &API_HANDLER_PCB::handleGetBoardPlotSettings );
    registerHandler<SetBoardPlotSettings, Empty>( &API_HANDLER_PCB::handleSetBoardPlotSettings );
    registerHandler<InjectDrcError, InjectDrcErrorResponse>(
            &API_HANDLER_PCB::handleInjectDrcError );

    registerHandler<RunBoardJobExport3D, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExport3D );
    registerHandler<RunBoardJobExportRender, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportRender );
    registerHandler<kiapi::automation::v1::PcbRenderInspectionRequest,
                    kiapi::automation::v1::PcbRenderInspectionResponse>(
            &API_HANDLER_PCB::handlePcbRenderInspection );
    registerHandler<RunBoardJobExportSvg, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportSvg );
    registerHandler<RunBoardJobExportDxf, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportDxf );
    registerHandler<RunBoardJobExportPdf, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportPdf );
    registerHandler<RunBoardJobExportPs, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportPs );
    registerHandler<RunBoardJobExportGerbers, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportGerbers );
    registerHandler<RunBoardJobExportDrill, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportDrill );
    registerHandler<RunBoardJobExportPosition, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportPosition );
    registerHandler<RunBoardJobExportGencad, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportGencad );
    registerHandler<RunBoardJobExportIpc2581, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportIpc2581 );
    registerHandler<RunBoardJobExportIpcD356, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportIpcD356 );
    registerHandler<RunBoardJobExportODB, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportODB );
    registerHandler<RunBoardJobExportStats, types::RunJobResponse>(
            &API_HANDLER_PCB::handleRunBoardJobExportStats );

    registerHandler<GetPageSettings, types::PageSettings>( &API_HANDLER_PCB::handleGetPageSettings );
    registerHandler<SetPageSettings, types::PageSettings>( &API_HANDLER_PCB::handleSetPageSettings );

    registerHandler<CrossProbeAnnounce, CrossProbeAnnounceResponse>( &API_HANDLER_PCB::handleCrossProbeAnnounce );
    registerHandler<SyncSelection, SyncSelectionResponse>( &API_HANDLER_PCB::handleSyncSelection );
    registerHandler<HighlightNets, HighlightNetsResponse>( &API_HANDLER_PCB::handleHighlightNets );
}


struct API_HANDLER_PCB::ROUTE_PREVIEW_SESSION
{
    std::string operationId;
    kiapi::common::types::DocumentSpecifier document;
    kiapi::automation::v1::DocumentRevision expectedRevision;
    std::string processEpoch;
    std::unique_ptr<TOOL_MANAGER> manager;
    GENERATOR_TOOL* generator = nullptr; // owned by manager
    kiapi::automation::v1::PcbRoutePreviewState lastState;
    bool active = false;
    bool terminal = false;
    bool cancelled = false;
    uint32_t waypointCount = 0;
};


API_HANDLER_PCB::~API_HANDLER_PCB()
{
    clearRoutePreviewSessions();
}


void API_HANDLER_PCB::clearRoutePreviewSessions()
{
    for( auto& [unused, session] : m_routePreviewSessions )
    {
        if( session && session->generator && session->generator->Router()
            && session->generator->Router()->RoutingInProgress() )
            session->generator->Router()->StopRouting();

        if( session )
        {
            if( session->active )
            {
                session->lastState.set_session_active( false );
                session->lastState.set_reconciled( true );
                session->lastState.set_error_code( "route_preview_stale" );
                session->lastState.set_error_message( "The PCB changed while the native route preview session was active" );
            }
            session->active = false;
            session->terminal = true;
        }
    }
}


void API_HANDLER_PCB::refreshCustomRulesBaseline()
{
    BOARD* current = board();
    const wxString path = current ? current->GetDesignRulesPath() : wxString();

    if( current && FILE_CONTENT_BASELINE::SamePath( path, current->CustomRulesFileBaseline().Path() )
        && current->CustomRulesFileBaseline().Known() )
        return;

    const FILE_CONTENT_BASELINE baseline = path.empty() ? FILE_CONTENT_BASELINE{} : FILE_CONTENT_BASELINE::Read( path );
    if( current )
        current->SetCustomRulesFileBaseline( baseline );
}


PCB_EDIT_FRAME* API_HANDLER_PCB::frame() const
{
    return static_cast<PCB_EDIT_FRAME*>( m_frame );
}


HANDLER_RESULT<GetOpenDocumentsResponse> API_HANDLER_PCB::handleGetOpenDocuments(
        const HANDLER_CONTEXT<GetOpenDocuments>& aCtx )
{
    if( aCtx.Request.type() != DocumentType::DOCTYPE_PCB )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    GetOpenDocumentsResponse response;
    common::types::DocumentSpecifier doc;

    wxFileName fn( pcbContext()->GetCurrentFileName() );

    doc.set_type( DocumentType::DOCTYPE_PCB );
    doc.set_board_filename( fn.GetFullName() );

    doc.mutable_project()->set_name( project().GetProjectName().ToStdString( wxConvUTF8 ) );
    // Use the same project representation as schematic documents in this
    // instance, including the directory separator returned by GetProjectPath.
    doc.mutable_project()->set_path( project().GetProjectPath().ToStdString( wxConvUTF8 ) );

    response.mutable_documents()->Add( std::move( doc ) );
    return response;
}


HANDLER_RESULT<kiapi::automation::v1::DocumentLifecycleState> API_HANDLER_PCB::handleReadLifecycleState(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadDocumentLifecycleState>& aCtx )
{
    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    try
    {
        kiapi::automation::v1::DocumentLifecycleState result;
        result.mutable_document()->CopyFrom( aCtx.Request.document() );
        result.set_native_identity( board()->m_Uuid.AsStdString() );
        result.set_process_epoch( drcApiServer().Token() );
        result.mutable_revision()->set_epoch( result.native_identity() );
        const int sequence = board()->GetTimeStamp();
        if( sequence < 0 ) throw std::runtime_error( "Board observation counter requires a new document epoch" );
        result.mutable_revision()->set_sequence( static_cast<uint64_t>( sequence ) );
        result.set_scope( kiapi::automation::v1::DLS_PCB );
        result.set_native_content_dirty( frame() ? frame()->IsContentModified() : board()->IsModified() );
        NATIVE_DOCUMENT_DIGEST digest;
        NATIVE_STATE_DIGEST content, settings;
        PCB_IO_KICAD_SEXPR writer;
        writer.FormatBoardToFormatter( &content, board(), nullptr, false );
        digest.Add( "board", content );
        const nlohmann::json projectState = frame()
                ? frame()->CaptureProjectEditorState()
                : PCB_PROJECT_EDITOR_STATE::Capture( *board(), project().GetProjectFile(),
                                                     project().GetProjectFile().m_LayerPresets,
                                                     project().GetProjectFile().m_Viewports );
        settings.Append( projectState.dump() );
        refreshCustomRulesBaseline();
        const wxString rulesPath = board()->GetDesignRulesPath();
        if( !rulesPath.empty() )
        {
            const FILE_CONTENT_BASELINE currentRules = FILE_CONTENT_BASELINE::Read( rulesPath );
            const FILE_CONTENT_BASELINE& trustedRules = board()->CustomRulesFileBaseline();
            if( !trustedRules.Known() || !currentRules.Known() )
                throw std::runtime_error( "Cannot observe PCB custom rules" );
            NATIVE_STATE_DIGEST ruleContent;
            // Keep the sidecar in the same lifecycle file set as the board and
            // project settings.  The digest prevents stale admission while the
            // explicit baseline lets save/route checks explain an external edit.
            const wxString observedRulesPath = currentRules.Path();
            ruleContent.Append( observedRulesPath.ToStdString( wxConvUTF8 ) );
            ruleContent.Append( currentRules.Exists() ? "present" : "absent" );
            ruleContent.Append( currentRules.Sha256() );
            digest.Add( "custom-rules", ruleContent );
            result.add_native_files( observedRulesPath.ToStdString( wxConvUTF8 ) );
            result.add_file_baselines()->CopyFrom( ObserveNativeFile( observedRulesPath, trustedRules ) );
        }
        digest.Add( "project-settings", settings );
        result.set_project_settings_included( true );
        result.add_native_files( project().AbsolutePath( board()->GetFileName() ).ToStdString( wxConvUTF8 ) );
        result.add_native_files( project().GetProjectFullName().ToStdString( wxConvUTF8 ) );
        result.add_file_baselines()->CopyFrom( ObserveNativeFile(
                project().AbsolutePath( board()->GetFileName() ), board()->FileBaseline() ) );
        result.add_file_baselines()->CopyFrom( ObserveNativeFile(
                project().GetProjectFullName(), project().GetProjectFile().FileBaseline() ) );
        // The serialized projection and retained sidecar baselines are exposed
        // for checked route admission. The numeric BOARD timestamp remains a
        // cache invalidation cursor; it is not universal mutation tracking.
        const bool baselineChecked = DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( result );
        result.set_disk_baseline_checked( baselineChecked );
        result.set_complete_change_tracking( false );
        result.set_state_sha256( digest.Hex() );
        if( result.native_identity() != board()->m_Uuid.AsStdString() || sequence != board()->GetTimeStamp() )
        {
            ApiResponseStatus changed;
            changed.set_status( ApiStatusCode::AS_BUSY );
            changed.set_error_message( "PCB changed during native state observation" );
            return tl::unexpected( changed );
        }
        return result;
    }
    catch( const std::exception& error )
    {
        // The exception's own problem text (for an IO_ERROR, its Problem()), not the pointer
        // IO_ERROR::what() returns into the conversion cache of that string (p10897cd52e677d15).
        ApiResponseStatus failure;
        failure.set_status( ApiStatusCode::AS_BAD_REQUEST );
        failure.set_error_message( "Native state could not be observed: " + PcbDrcExceptionMessage( error ) );
        return tl::unexpected( failure );
    }
}


HANDLER_RESULT<kiapi::automation::v1::PcbDrcState> API_HANDLER_PCB::handleReadDrcState(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbDrcState>& aCtx )
{
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    kiapi::automation::v1::PcbDrcState result;
    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.set_process_epoch( drcApiServer().Token() );
    result.mutable_revision()->set_epoch( board()->m_Uuid.AsStdString() );
    const int sequence = board()->GetTimeStamp();
    if( sequence < 0 )
    {
        ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "PCB observation counter requires a new document epoch" );
        return tl::unexpected( error );
    }
    result.mutable_revision()->set_sequence( sequence );
    if( frame() && frame()->GetToolManager() )
        if( const DRC_TOOL* tool = frame()->GetToolManager()->GetTool<DRC_TOOL>() )
            result.set_running( tool->IsDRCRunning() );
    result.set_results_freshness_known( false );
    if( result.running() ) return result;
    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    std::map<std::string, PCB_MARKER*> markers;
    for( PCB_MARKER* marker : board()->Markers() )
    {
        if( !marker || !markers.emplace( marker->m_Uuid.AsStdString(), marker ).second )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Native DRC inventory contains a missing or duplicate marker" );
            return tl::unexpected( error );
        }
    }
    for( const auto& [id, marker] : markers )
    {
        if( !marker->GetRCItem() )
        {
            ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "A native DRC marker has no rule-check data" );
            return tl::unexpected( error );
        }
        google::protobuf::Any encoded;
        try
        {
            marker->Serialize( encoded );
        }
        catch( const std::exception& error )
        {
            ApiResponseStatus failure;
            failure.set_status( ApiStatusCode::AS_BAD_REQUEST );
            failure.set_error_message( std::string( "Native DRC marker serialization failed: " )
                                       + error.what() );
            return tl::unexpected( failure );
        }
        auto* finding = result.add_findings();
        if( !encoded.UnpackTo( finding->mutable_marker() ) )
        {
            ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Native DRC marker serialization failed" );
            return tl::unexpected( error );
        }
        finding->set_native_id( id ); finding->set_excluded( marker->IsExcluded() );
        finding->set_comment( marker->GetComment().ToStdString( wxConvUTF8 ) );
    }
    if( sequence != board()->GetTimeStamp() || result.revision().epoch() != board()->m_Uuid.AsStdString() )
    {
        ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BUSY );
        error.set_error_message( "PCB changed during DRC marker observation" );
        return tl::unexpected( error );
    }
    result.set_marker_snapshot_complete( true );
    return result;
}

HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> API_HANDLER_PCB::handleStartDrcJob(
        const HANDLER_CONTEXT<kiapi::automation::v1::StartPcbDrcJob>& aCtx )
{
    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto replay = m_drcJobs.ReadOperation( aCtx.Request, *board(), drcApiServer().Token(),
            [this]( const auto& document ) { return observeDrcSchematic( document ); },
            [this]( BOARD& source ) { return observeDrcLibraries( source ); } );
    if( !replay )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST ); error.set_error_message( replay.error() );
        return tl::unexpected( error );
    }
    if( replay->has_value() ) return **replay;

    auto* libraries = PROJECT_PCB::FootprintLibAdapter( &project() );
    auto* drawing = frame() ? frame()->GetCanvas()->GetDrawingSheet() : nullptr;
    if( !libraries || !drawing )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "Native DRC requires initialized project library and drawing-sheet inputs" );
        return tl::unexpected( error );
    }
    PCB_DRC_CAPTURE_CONTEXT capture{ *libraries, DS_DATA_MODEL::GetTheInstance(), drawing->m_Uuid,
                                    frame()->GetPcbNewSettings()->m_PnsSettings.get() };
    kiapi::automation::v1::SchematicParityNetlistSnapshot schematic;
    if( aCtx.Request.test_footprints() )
    {
        const auto& expected = aCtx.Request.expected_schematic_state();
        if( !aCtx.Request.has_expected_schematic_state()
            || expected.process_epoch() != drcApiServer().Token()
            || !google::protobuf::util::MessageDifferencer::Equals(
                    expected.document().project(), aCtx.Request.document().project() ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "DRC parity requires an observed schematic in this project and native process" );
            return tl::unexpected( error );
        }
        kiapi::automation::v1::ReadSchematicParityNetlist query;
        query.set_schema_version( 1 );
        query.mutable_document()->CopyFrom( expected.document() );
        query.mutable_expected_state()->CopyFrom( expected );
        query.set_allow_duplicate_sheet_names( aCtx.Request.allow_duplicate_sheet_names() );
        ApiRequest envelope;
        envelope.mutable_message()->PackFrom( query );
        auto response = drcApiServer().DispatchToHandlers( envelope );
        if( !response ) return tl::unexpected( response.error() );
        if( response->status().status() != ApiStatusCode::AS_OK ) return tl::unexpected( response->status() );
        if( !response->message().UnpackTo( &schematic ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_NOT_READY );
            error.set_error_message( "Native schematic comparison response could not be decoded" );
            return tl::unexpected( error );
        }
        capture.schematic = &schematic;
    }
    auto started = m_drcJobs.Start( aCtx.Request, *board(), drcApiServer().Token(), capture );
    if( !started )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( started.error() );
        return tl::unexpected( error );
    }
    return *started;
}

tl::expected<kiapi::automation::v1::DocumentLifecycleState, std::string> API_HANDLER_PCB::observeDrcSchematic(
        const kiapi::common::types::DocumentSpecifier& document )
{
    kiapi::automation::v1::ReadDocumentLifecycleState query;
    query.mutable_document()->CopyFrom( document );
    ApiRequest envelope;
    envelope.mutable_message()->PackFrom( query );
    auto response = drcApiServer().DispatchToHandlers( envelope );
    if( !response ) return tl::unexpected( response.error().error_message() );
    if( response->status().status() != ApiStatusCode::AS_OK )
        return tl::unexpected( response->status().error_message() );
    kiapi::automation::v1::DocumentLifecycleState state;
    if( !response->message().UnpackTo( &state ) ) return tl::unexpected( "Native schematic state could not be decoded" );
    return state;
}

tl::expected<std::string, std::string> API_HANDLER_PCB::observeDrcLibraries( BOARD& source )
{
    if( &source != board() ) return tl::unexpected( "Library observation target changed" );
    auto* libraries = PROJECT_PCB::FootprintLibAdapter( &project() );
    if( !libraries ) return tl::unexpected( "Native footprint libraries are unavailable" );
    auto captured = DRC_LIBRARY_INPUTS::Capture( source, *libraries );
    if( !captured ) return tl::unexpected( "Native footprint library observation was cancelled" );
    return captured->ContentFingerprint();
}

void API_HANDLER_PCB::ObserveNativeDrcInputs( bool aLibraryConfigurationMayHaveChanged )
{
    if( !board() ) return;
    m_drcJobs.ObserveInputs( *board(), drcApiServer().Token(),
            [this]( const auto& document ) { return observeDrcSchematic( document ); },
            [this]( BOARD& source ) { return observeDrcLibraries( source ); },
            aLibraryConfigurationMayHaveChanged );
}

tl::expected<std::string, std::string> API_HANDLER_PCB::observeDrcAuxiliary( BOARD& source )
{
    if( &source != board() || !frame() ) return tl::unexpected( "Native editor inputs are unavailable" );
    auto* libraries = PROJECT_PCB::FootprintLibAdapter( &project() );
    auto* drawing = frame()->GetCanvas()->GetDrawingSheet();
    if( !libraries || !drawing ) return tl::unexpected( "Native drawing inputs are unavailable" );
    PCB_DRC_CAPTURE_CONTEXT current{ *libraries, DS_DATA_MODEL::GetTheInstance(), drawing->m_Uuid,
                                    frame()->GetPcbNewSettings()->m_PnsSettings.get() };
    return PCB_DRC_AUXILIARY_BASELINE::Capture( current ).Fingerprint();
}

HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> API_HANDLER_PCB::handleReadDrcJob(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbDrcJob>& aCtx )
{
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto read = m_drcJobs.Read( aCtx.Request, *board(), drcApiServer().Token(),
            [this]( const auto& document ) { return observeDrcSchematic( document ); },
            [this]( BOARD& source ) { return observeDrcLibraries( source ); } );
    if( !read )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( read.error() );
        return tl::unexpected( error );
    }
    return *read;
}


HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> API_HANDLER_PCB::handleReadDrcReplacementJob(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityPcbDrcReplacementJob>& aCtx )
{
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    kiapi::automation::v1::ReadPcbDrcJob base;
    base.mutable_document()->CopyFrom( aCtx.Request.document() );
    base.set_job_id( aCtx.Request.job_id() );
    base.set_process_epoch( aCtx.Request.process_epoch() );
    base.mutable_expected_candidate_items()->CopyFrom( aCtx.Request.expected_candidate_items() );
    auto read = m_drcJobs.Read( base, *board(), apiServer().Token(),
            [this]( const auto& document ) { return observeDrcSchematic( document ); },
            [this]( BOARD& source ) { return observeDrcLibraries( source ); }, true );
    if( !read )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( read.error() );
        return tl::unexpected( error );
    }
    auto deletes = m_drcJobs.VerifyDeleteItems( aCtx.Request.job_id(), apiServer().Token(),
                                                 aCtx.Request.document(),
                                                 aCtx.Request.expected_delete_item_ids() );
    if( !deletes )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( deletes.error() );
        return tl::unexpected( error );
    }
    read->set_candidate_payload_verified( true );
    return *read;
}

HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> API_HANDLER_PCB::handleCancelDrcJob(
        const HANDLER_CONTEXT<kiapi::automation::v1::CancelPcbDrcJob>& aCtx )
{
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto cancelled = m_drcJobs.Cancel( aCtx.Request, *board(), drcApiServer().Token(),
            [this]( const auto& document ) { return observeDrcSchematic( document ); },
            [this]( BOARD& source ) { return observeDrcLibraries( source ); } );
    if( !cancelled )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( cancelled.error() );
        return tl::unexpected( error );
    }
    return *cancelled;
}

namespace
{
ApiResponseStatus RoutePreviewError( const std::string& aMessage,
                                     ApiStatusCode aStatus = ApiStatusCode::AS_BAD_REQUEST )
{
    ApiResponseStatus error;
    error.set_status( aStatus );
    error.set_error_message( aMessage );
    return error;
}


kiapi::automation::v1::DocumentRevision CurrentRoutePreviewRevision( const BOARD& aBoard )
{
    kiapi::automation::v1::DocumentRevision revision;
    revision.set_epoch( aBoard.m_Uuid.AsStdString() );
    revision.set_sequence( static_cast<uint64_t>( aBoard.GetTimeStamp() ) );
    return revision;
}


bool RoutePreviewRevisionMatches( const BOARD& aBoard,
                                  const kiapi::automation::v1::DocumentRevision& aExpected )
{
    return aExpected.epoch() == aBoard.m_Uuid.AsStdString()
        && aExpected.sequence() == static_cast<uint64_t>( aBoard.GetTimeStamp() );
}
}




static HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> ExtractRoutePreviewGeometry(
        const std::string& operationId, PNS_KICAD_IFACE* iface,
        const std::vector<std::unique_ptr<PNS::ITEM>>& removedSnapshot,
        const std::vector<std::unique_ptr<PNS::ITEM>>& addedSnapshot,
        kiapi::automation::v1::PcbRoutePreviewState response )
{
    response.set_operation_id( operationId );
    auto reject = [&]( const char* code, const std::string& message )
            -> HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState>
    {
        response.clear_route_items(); response.clear_replacement_updates(); response.clear_removed_item_ids();
        response.set_completed( false ); response.set_error_code( code ); response.set_error_message( message );
        return response;
    };
    std::vector<PNS::ITEM*> removed, added, deleted, created, updated;
    auto auxiliary = []( PNS::ITEM* item )
    {
        return item->IsVirtual() || ( item->OfKind( PNS::ITEM::HOLE_T ) && item->ParentPadVia() );
    };
    std::set<std::string> displacedIds;
    for( const auto& item : removedSnapshot )
    {
        if( auxiliary( item.get() ) || !item->Parent() )
            continue;
        BOARD_ITEM* parent = item->Parent();
        if( parent->m_Uuid == niluuid || !parent->IsType( { PCB_TRACE_T, PCB_ARC_T, PCB_VIA_T } ) )
            return reject( "route_delta_removed_geometry_unsupported", "The complete native route delta removes unsupported board geometry" );
        displacedIds.insert( parent->m_Uuid.AsStdString() );
        removed.push_back( item.get() );
    }
    for( const auto& item : addedSnapshot )
        if( !auxiliary( item.get() ) )
            added.push_back( item.get() );

    PNS::ROUTER::PartitionRoutingChanges( removed, added, deleted, created, updated );
    response.set_operation_id( operationId );
    for( const auto& id : displacedIds )
        response.add_removed_item_ids()->set_value( id );

    auto geometry = [&]( PNS::ITEM* item, bool preserveIdentity )
            -> std::unique_ptr<BOARD_CONNECTED_ITEM>
    {
        if( !item || item->Layers().Start() < 0 || item->Layers().End() < item->Layers().Start()
            || iface->GetNetCode( item->Net() ) <= 0
            || !IsCopperLayer( iface->GetBoardLayerFromPNSLayer( item->Layers().Start() ) )
            || !IsCopperLayer( iface->GetBoardLayerFromPNSLayer( item->Layers().End() ) ) )
            return nullptr;
        if( PNS::VIA::ClassOf( item ) )
        {
            auto* via = static_cast<PNS::VIA*>( item );
            if( via->Drill() <= 0 || via->Diameter( via->Layers().Start() ) <= via->Drill() )
                return nullptr;
        }
        else if( PNS::SEGMENT::ClassOf( item ) )
        {
            if( static_cast<PNS::SEGMENT*>( item )->Width() <= 0 )
                return nullptr;
        }
        else if( PNS::ARC::ClassOf( item ) )
        {
            if( static_cast<PNS::ARC*>( item )->Width() <= 0 )
                return nullptr;
        }
        else
            return nullptr;
        return iface->MaterializeRouteItem( item, preserveIdentity );
    };
    kiapi::common::commands::UpdateItems updates;
    updates.mutable_header()->mutable_document()->CopyFrom( response.document() );
    std::set<std::string> updatedIds;
    for( PNS::ITEM* item : updated )
    {
        auto value = geometry( item, true );
        if( !value || !updatedIds.insert( value->m_Uuid.AsStdString() ).second )
            return reject( "route_delta_replacement_invalid", "The complete native route delta cannot preserve a replacement identity" );
        value->Serialize( *updates.add_items() );
    }
    if( updates.items_size() )
        response.mutable_replacement_updates()->CopyFrom( updates );

    // Canonical geometry order makes the operation-derived create identities
    // stable. The retained response is reused by session reads and never
    // reconstructs IDs from a volatile leader or an unordered NODE index.
    std::vector<std::pair<std::string, google::protobuf::Any>> creates;
    for( PNS::ITEM* item : created )
    {
        BOARD_ITEM* source = item->Parent() ? item->Parent() : item->GetSourceItem();
        if( source && source->GetParentGroup() )
            return reject( "route_delta_group_membership_unsupported",
                           "The route delta creates grouped-source copper whose membership cannot yet be preserved" );
        auto value = geometry( item, false );
        if( !value )
            return reject( "route_delta_added_geometry_unsupported", "The complete native route delta contains unsupported added or unassigned geometry" );
        value->SetUuidDirect( niluuid );
        google::protobuf::Any serialized;
        value->Serialize( serialized );
        creates.emplace_back( serialized.SerializeAsString(), std::move( serialized ) );
    }
    std::sort( creates.begin(), creates.end(), []( const auto& a, const auto& b ) { return a.first < b.first; } );
    for( size_t index = 0; index < creates.size(); ++index )
    {
        const std::string id = KIID::FromName( operationId + "-preview-create-" + std::to_string( index ) ).AsStdString();
        google::protobuf::Any& value = creates[index].second;
        if( value.Is<kiapi::board::types::Track>() )
        {
            kiapi::board::types::Track track;
            value.UnpackTo( &track ); track.mutable_id()->set_value( id ); value.PackFrom( track );
        }
        else if( value.Is<kiapi::board::types::Arc>() )
        {
            kiapi::board::types::Arc arc;
            value.UnpackTo( &arc ); arc.mutable_id()->set_value( id ); value.PackFrom( arc );
        }
        else
        {
            kiapi::board::types::Via via;
            value.UnpackTo( &via ); via.mutable_id()->set_value( id ); value.PackFrom( via );
        }
        response.add_route_items()->CopyFrom( value );
    }
    if( creates.empty() && updates.items_size() == 0 && deleted.empty() )
        return reject( "route_delta_empty", "The native router produced no actionable complete delta" );
    response.set_complete_delta_version( 1 );
    return response;
}


HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> API_HANDLER_PCB::handleRoutePreview(
        const HANDLER_CONTEXT<kiapi::automation::v1::StartPcbRoutePreview>& aCtx )
{
    using namespace kiapi::automation::v1;
    using kiapi::board::types::BoardLayer;

    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    const auto& request = aCtx.Request;
    BOARD* target = board();
    if( request.operation_id().empty() || request.operation_id() == niluuid.AsStdString() )
        return tl::unexpected( RoutePreviewError( "A route preview requires a nonempty operation ID" ) );
    if( request.process_epoch() != drcApiServer().Token() )
        return tl::unexpected( RoutePreviewError( "The route preview process epoch is stale" ) );
    if( !request.has_expected_revision()
        || request.expected_revision().epoch() != target->m_Uuid.AsStdString()
        || request.expected_revision().sequence() != static_cast<uint64_t>( target->GetTimeStamp() ) )
        return tl::unexpected( RoutePreviewError( "The PCB changed since the requested route preview revision" ) );
    if( request.waypoints_size() == 0 || request.waypoints_size() > 256 )
        return tl::unexpected( RoutePreviewError( "A route preview requires one to 256 waypoints" ) );
    if( !request.has_start() )
        return tl::unexpected( RoutePreviewError( "A route preview requires an explicit start point" ) );

    PCB_LAYER_ID boardLayer = FromProtoEnum<PCB_LAYER_ID>( request.layer() );
    if( !IsCopperLayer( boardLayer ) )
        return tl::unexpected( RoutePreviewError( "A route preview must use a copper layer" ) );
    BOARD_ITEM* startBoardItem = target->ResolveItem( KIID( request.start_item_id() ), true );
    if( !startBoardItem || !startBoardItem->IsConnected() )
        return tl::unexpected( RoutePreviewError( "The route preview start item is not a connected board object" ) );

    if( request.persistent_session() )
    {
        if( request.differential_pair() )
            return tl::unexpected( RoutePreviewError(
                    "Persistent differential-pair preview sessions are unavailable; use the bounded preview" ,
                    ApiStatusCode::AS_UNIMPLEMENTED ) );
        if( m_routePreviewSessions.contains( request.operation_id() ) )
            return tl::unexpected( RoutePreviewError( "The route preview operation ID already identifies a session" ) );
        if( m_routePreviewSessions.size() >= 256 )
            return tl::unexpected( RoutePreviewError( "The native route preview session limit has been reached" ) );

        PCBNEW_SETTINGS* settings = frame() ? frame()->GetPcbNewSettings() : nullptr;
        if( !settings || !settings->m_PnsSettings )
            return tl::unexpected( RoutePreviewError( "Native routing settings are unavailable",
                                                       ApiStatusCode::AS_UNIMPLEMENTED ) );

        auto session = std::make_unique<ROUTE_PREVIEW_SESSION>();
        session->operationId = request.operation_id();
        session->document.CopyFrom( request.document() );
        session->expectedRevision.CopyFrom( request.expected_revision() );
        session->processEpoch = request.process_epoch();
        session->manager = std::make_unique<TOOL_MANAGER>();
        session->manager->SetEnvironment( target, nullptr, nullptr, nullptr, nullptr );
        auto snapshot = std::make_unique<PNS::ROUTING_SETTINGS>( nullptr,
                                                                  settings->m_PnsSettings->GetPath() );
        settings->m_PnsSettings->CopyCurrentStateTo( *snapshot );
        if( request.push_and_shove() )
            snapshot->SetMode( PNS::RM_Shove );
        session->generator = new GENERATOR_TOOL( false );
        session->manager->RegisterTool( session->generator );
        try
        {
            session->generator->InitializeSnapshot( std::move( snapshot ) );
        }
        catch( const std::exception& error )
        {
            return tl::unexpected( RoutePreviewError( error.what() ) );
        }

        PNS::ROUTER* router = session->generator->Router();
        PNS_KICAD_IFACE* iface = session->generator->GetInterface();
        router->SetPreviewCapture( true );
        PNS::ITEM* startItem = router->GetWorld()->FindItemByParent( startBoardItem );
        if( !startItem )
            return tl::unexpected( RoutePreviewError( "The start item is not available in the native router world" ) );
        const VECTOR2I start( request.start().x_nm(), request.start().y_nm() );
        const int pnsLayer = iface->GetPNSLayerFromBoardLayer( boardLayer );
        PNS::SIZES_SETTINGS sizes( router->Sizes() );
        if( !iface->ImportSizes( sizes, startItem, nullptr, start ) )
            return tl::unexpected( RoutePreviewError( "The native router could not resolve track and via sizes" ) );
        router->UpdateSizes( sizes );
        router->SetMode( PNS::PNS_MODE_ROUTE_SINGLE );
        if( !router->StartRouting( start, startItem, pnsLayer ) )
            return tl::unexpected( RoutePreviewError( router->FailureReason().ToStdString() ) );

        session->active = true;
        auto* sessionPtr = session.get();
        m_routePreviewSessions.emplace( request.operation_id(), std::move( session ) );
        return advanceRoutePreviewSession( *sessionPtr, request.waypoints(), request.force_finish() );
    }

    PCBNEW_SETTINGS* pcbSettings = frame() ? frame()->GetPcbNewSettings() : nullptr;
    if( !pcbSettings || !pcbSettings->m_PnsSettings )
        return tl::unexpected( RoutePreviewError( "Native routing settings are unavailable", ApiStatusCode::AS_UNIMPLEMENTED ) );
    auto snapshotSettings = std::make_unique<PNS::ROUTING_SETTINGS>( nullptr,
                                                                       pcbSettings->m_PnsSettings->GetPath() );
    pcbSettings->m_PnsSettings->CopyCurrentStateTo( *snapshotSettings );
    if( request.push_and_shove() )
        snapshotSettings->SetMode( PNS::RM_Shove );
    TOOL_MANAGER previewManager;
    previewManager.SetEnvironment( target, nullptr, nullptr, nullptr, nullptr );
    auto* generatorTool = new GENERATOR_TOOL( false );
    previewManager.RegisterTool( generatorTool );
    try { generatorTool->InitializeSnapshot( std::move( snapshotSettings ) ); }
    catch( const std::exception& error )
    { return tl::unexpected( RoutePreviewError( error.what() ) ); }
    PNS::ROUTER* router = generatorTool->Router();
    PNS_KICAD_IFACE* iface = generatorTool->GetInterface();
    router->SetPreviewCapture( true );
    PNS::ITEM* startItem = router->GetWorld()->FindItemByParent( startBoardItem );
    if( !startItem )
        return tl::unexpected( RoutePreviewError( "The start item is not available in the native router world" ) );
    const VECTOR2I start( request.start().x_nm(), request.start().y_nm() );
    const int pnsLayer = iface->GetPNSLayerFromBoardLayer( boardLayer );
    PNS::SIZES_SETTINGS sizes( router->Sizes() );
    if( !iface->ImportSizes( sizes, startItem, nullptr, start ) )
        return tl::unexpected( RoutePreviewError( "The native router could not resolve track and via sizes" ) );
    router->UpdateSizes( sizes );
    router->SetMode( request.differential_pair() ? PNS::PNS_MODE_ROUTE_DIFF_PAIR : PNS::PNS_MODE_ROUTE_SINGLE );
    if( !router->StartRouting( start, startItem, pnsLayer ) )
        return tl::unexpected( RoutePreviewError( router->FailureReason().ToStdString() ) );

    std::string failure;
    // Differential-pair FixRoute commits the temporary pair into the router's
    // branch and clears its active node. Capture the heads before that cleanup
    // so an open-ended preview can still return both P/N geometries. Single
    // route behavior remains on the existing post-fix extraction path.
    for( int index = 0; index < request.waypoints_size(); ++index )
    {
        const VECTOR2I point( request.waypoints( index ).x_nm(), request.waypoints( index ).y_nm() );
        if( !router->Move( point, nullptr ) )
        {
            failure = router->FailureReason().ToStdString();
            break;
        }
        const bool last = index == request.waypoints_size() - 1;
        if( last && !request.force_finish() )
            continue;
        if( last && !router->FixRoute( point, nullptr, true, false ) )
        {
            failure = router->FailureReason().ToStdString();
            break;
        }
        if( !last ) router->FixRoute( point, nullptr, false, false );
    }

    std::vector<std::unique_ptr<PNS::ITEM>> removed, added;
    if( failure.empty() && !router->GetPreviewItems( removed, added, !request.force_finish() ) )
        failure = "The native router could not capture the complete prospective route delta";
    if( !failure.empty() )
    {
        router->StopRouting();
        return tl::unexpected( RoutePreviewError( failure ) );
    }

    PcbRoutePreviewState response;
    response.mutable_document()->CopyFrom( request.document() );
    response.set_operation_id( request.operation_id() );
    response.mutable_checked_revision()->CopyFrom( request.expected_revision() );
    response.set_process_epoch( request.process_epoch() );
    response.set_completed( true );
    response.set_preview_only( true );
    response.set_native_commit( false );
    auto extracted = ExtractRoutePreviewGeometry( request.operation_id(), iface, removed, added,
                                                  std::move( response ) );
    router->StopRouting();
    if( !extracted ) return tl::unexpected( extracted.error() );
    return *extracted;
}


HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> API_HANDLER_PCB::advanceRoutePreviewSession(
        ROUTE_PREVIEW_SESSION& session,
        const google::protobuf::RepeatedPtrField<kiapi::common::types::Vector2>& waypoints,
        bool forceFinish )
{
    using namespace kiapi::automation::v1;

    if( !session.active || !session.generator || !session.generator->Router() )
        return session.lastState;

    BOARD* target = board();
    PNS::ROUTER* router = session.generator->Router();
    if( !RoutePreviewRevisionMatches( *target, session.expectedRevision ) )
    {
        router->StopRouting();
        session.active = false;
        session.terminal = true;
        session.lastState.Clear();
        session.lastState.mutable_document()->CopyFrom( session.document );
        session.lastState.set_operation_id( session.operationId );
        session.lastState.mutable_checked_revision()->CopyFrom( session.expectedRevision );
        session.lastState.set_process_epoch( session.processEpoch );
        session.lastState.set_completed( true );
        session.lastState.set_preview_only( true );
        session.lastState.set_reconciled( true );
        session.lastState.set_session_active( false );
        session.lastState.set_error_code( "route_preview_stale" );
        session.lastState.set_error_message( "The PCB changed while the native route preview session was active" );
        session.lastState.mutable_observed_revision()->CopyFrom( CurrentRoutePreviewRevision( *target ) );
        session.lastState.set_waypoint_count( session.waypointCount );
        return session.lastState;
    }

    if( waypoints.empty() || waypoints.size() > 256 )
        return tl::unexpected( RoutePreviewError( "A route continuation requires one to 256 waypoints" ) );

    PNS_KICAD_IFACE* iface = session.generator->GetInterface();
    std::string failure;
    // An open-ended FixRoute leaves the active leader in the router's current
    // branch rather than publishing it through GetUpdatedItems. Capture the
    // heads before that transition so continuation can return a truthful
    // candidate while retaining the branch for the next call.
    for( int index = 0; index < waypoints.size(); ++index )
    {
        const VECTOR2I point( waypoints.Get( index ).x_nm(), waypoints.Get( index ).y_nm() );
        if( !router->Move( point, nullptr ) )
        {
            failure = router->FailureReason().ToStdString();
            break;
        }
        const bool last = index == waypoints.size() - 1;
        if( last && !forceFinish )
            continue;
        if( last && !router->FixRoute( point, nullptr, true, false ) )
        {
            failure = router->FailureReason().ToStdString();
            break;
        }
        if( !last ) router->FixRoute( point, nullptr, false, false );
    }

    std::vector<std::unique_ptr<PNS::ITEM>> removed, added;
    if( failure.empty() && !router->GetPreviewItems( removed, added, !forceFinish ) )
        failure = "The native router could not capture the complete prospective route delta";
    if( !failure.empty() )
    {
        router->StopRouting();
        session.active = false;
        session.terminal = true;
        return tl::unexpected( RoutePreviewError( failure ) );
    }

    PcbRoutePreviewState response;
    response.mutable_document()->CopyFrom( session.document );
    response.mutable_checked_revision()->CopyFrom( session.expectedRevision );
    response.set_process_epoch( session.processEpoch );
    response.set_completed( true );
    response.set_preview_only( true );
    response.set_native_commit( false );
    auto extracted = ExtractRoutePreviewGeometry( session.operationId, iface, removed, added,
                                                  std::move( response ) );
    if( !extracted )
    {
        router->StopRouting();
        session.active = false;
        session.terminal = true;
        return tl::unexpected( extracted.error() );
    }

    if( !extracted->completed() )
    {
        router->StopRouting(); session.active = false; session.terminal = true;
        session.lastState = *extracted; session.lastState.set_session_active( false );
        return session.lastState;
    }
    session.waypointCount += static_cast<uint32_t>( waypoints.size() );
    if( forceFinish )
    {
        // ExtractRoutePreviewGeometry has copied the branch into protobufs; the
        // detached native branch can now be discarded without a board commit.
        router->StopRouting();
        session.active = false;
        session.terminal = true;
    }

    extracted->set_session_active( session.active );
    extracted->set_waypoint_count( session.waypointCount );
    extracted->set_reconciled( false );
    extracted->set_cancelled( false );
    extracted->mutable_observed_revision()->CopyFrom( CurrentRoutePreviewRevision( *target ) );
    session.lastState = *extracted;
    return *extracted;
}


HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> API_HANDLER_PCB::handleContinueRoutePreview(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewContinue>& aCtx )
{
    using namespace kiapi::automation::v1;
    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    const auto& request = aCtx.Request;
    if( request.operation_id().empty() || request.process_epoch() != apiServer().Token()
        || !request.has_expected_revision() || request.waypoints_size() == 0
        || request.waypoints_size() > 256 )
        return tl::unexpected( RoutePreviewError( "A route continuation requires a session ID, process epoch, revision and one to 256 waypoints" ) );
    auto found = m_routePreviewSessions.find( request.operation_id() );
    if( found == m_routePreviewSessions.end() )
        return tl::unexpected( RoutePreviewError( "The native route preview session was not found" ) );
    ROUTE_PREVIEW_SESSION& session = *found->second;
    if( !google::protobuf::util::MessageDifferencer::Equals( session.document, request.document() )
        || session.processEpoch != request.process_epoch()
        || !google::protobuf::util::MessageDifferencer::Equals( session.expectedRevision,
                                                                 request.expected_revision() ) )
        return tl::unexpected( RoutePreviewError( "The route continuation does not match its native session identity" ) );
    if( session.terminal ) return session.lastState;
    return advanceRoutePreviewSession( session, request.waypoints(), request.force_finish() );
}


HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> API_HANDLER_PCB::handleCancelRoutePreview(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewCancel>& aCtx )
{
    using namespace kiapi::automation::v1;
    if( auto busy = checkForBusy() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    const auto& request = aCtx.Request;
    if( request.operation_id().empty() || request.process_epoch() != apiServer().Token() )
        return tl::unexpected( RoutePreviewError( "A route cancellation requires a session ID and current process epoch" ) );
    auto found = m_routePreviewSessions.find( request.operation_id() );
    if( found == m_routePreviewSessions.end() )
        return tl::unexpected( RoutePreviewError( "The native route preview session was not found" ) );
    ROUTE_PREVIEW_SESSION& session = *found->second;
    if( !google::protobuf::util::MessageDifferencer::Equals( session.document, request.document() )
        || session.processEpoch != request.process_epoch() )
        return tl::unexpected( RoutePreviewError( "The route cancellation does not match its native session identity" ) );
    if( session.active && session.generator && session.generator->Router() )
        session.generator->Router()->StopRouting();
    session.active = false;
    session.terminal = true;
    session.cancelled = true;
    session.lastState.Clear();
    session.lastState.mutable_document()->CopyFrom( session.document );
    session.lastState.set_operation_id( session.operationId );
    session.lastState.mutable_checked_revision()->CopyFrom( session.expectedRevision );
    session.lastState.set_process_epoch( session.processEpoch );
    session.lastState.set_completed( true );
    session.lastState.set_preview_only( true );
    session.lastState.set_cancellation_requested( true );
    session.lastState.set_cancelled( true );
    session.lastState.set_reconciled( true );
    session.lastState.mutable_observed_revision()->CopyFrom( CurrentRoutePreviewRevision( *board() ) );
    session.lastState.set_waypoint_count( session.waypointCount );
    return session.lastState;
}


HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> API_HANDLER_PCB::handleReadRoutePreview(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewRead>& aCtx )
{
    using namespace kiapi::automation::v1;
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    const auto& request = aCtx.Request;
    if( request.operation_id().empty() || request.process_epoch() != apiServer().Token() )
        return tl::unexpected( RoutePreviewError( "A route reconciliation requires a session ID and current process epoch" ) );
    auto found = m_routePreviewSessions.find( request.operation_id() );
    if( found == m_routePreviewSessions.end() )
        return tl::unexpected( RoutePreviewError( "The native route preview session was not found" ) );
    ROUTE_PREVIEW_SESSION& session = *found->second;
    if( !google::protobuf::util::MessageDifferencer::Equals( session.document, request.document() )
        || session.processEpoch != request.process_epoch() )
        return tl::unexpected( RoutePreviewError( "The route reconciliation does not match its native session identity" ) );
    if( session.active && RoutePreviewRevisionMatches( *board(), session.expectedRevision ) )
    {
        session.lastState.set_session_active( true );
        session.lastState.mutable_observed_revision()->CopyFrom( CurrentRoutePreviewRevision( *board() ) );
        return session.lastState;
    }
    if( session.active && session.generator && session.generator->Router() )
        session.generator->Router()->StopRouting();
    if( session.active )
    {
        session.active = false;
        session.terminal = true;
        session.lastState.Clear();
        session.lastState.mutable_document()->CopyFrom( session.document );
        session.lastState.set_operation_id( session.operationId );
        session.lastState.mutable_checked_revision()->CopyFrom( session.expectedRevision );
        session.lastState.set_process_epoch( session.processEpoch );
        session.lastState.set_completed( true );
        session.lastState.set_preview_only( true );
        session.lastState.set_reconciled( true );
        session.lastState.set_session_active( false );
        session.lastState.set_error_code( "route_preview_stale" );
        session.lastState.set_error_message( "The PCB changed while the native route preview session was active" );
        session.lastState.mutable_observed_revision()->CopyFrom( CurrentRoutePreviewRevision( *board() ) );
        session.lastState.set_waypoint_count( session.waypointCount );
    }
    return session.lastState;
}

HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSaveDocument(
        const HANDLER_CONTEXT<SaveDocument>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    if( !pcbContext()->SaveBoard() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "PCB persistence failed; the native document remains open" );
        return tl::unexpected( error );
    }
    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSaveCopyOfDocument(
        const HANDLER_CONTEXT<SaveCopyOfDocument>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    wxFileName boardPath( project().AbsolutePath( wxString::FromUTF8( aCtx.Request.path() ) ) );

    if( !boardPath.IsOk() || !boardPath.IsDirWritable() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "save path '{}' could not be opened",
                                          boardPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    if( boardPath.FileExists()
        && ( !boardPath.IsFileWritable() || !aCtx.Request.options().overwrite() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "save path '{}' exists and cannot be overwritten",
                                          boardPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    if( boardPath.GetExt() != FILEEXT::KiCadPcbFileExtension )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "save path '{}' must have a kicad_pcb extension",
                                          boardPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    BOARD* board = this->board();

    if( board->GetFileName().Matches( boardPath.GetFullPath() ) )
    {
        if( !pcbContext()->SaveBoard() )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "PCB persistence failed; the native document remains open" );
            return tl::unexpected( error );
        }
        return Empty();
    }

    bool includeProject = true;

    if( aCtx.Request.has_options() )
        includeProject = aCtx.Request.options().include_project();

    if( !pcbContext()->SavePcbCopy( boardPath.GetFullPath(), includeProject, /* aHeadless = */ true ) )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "PCB copy could not be saved; inspect native diagnostics" );
        return tl::unexpected( error );
    }

    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleRevertDocument(
        const HANDLER_CONTEXT<RevertDocument>& aCtx )
{
    // Validate first so a request meant for another editor's document is not captured here
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    // Reloading frees every item, so refuse while any client transaction is open; the staged
    // commit would otherwise dangle
    if( !m_commits.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BUSY );
        e.set_error_message( "cannot revert while a commit is open" );
        return tl::unexpected( e );
    }

    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "RevertDocument" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    wxFileName fn = project().AbsolutePath( board()->GetFileName() );

    frame()->GetScreen()->SetContentModified( false );
    frame()->ReleaseFile();
    frame()->OpenProjectFiles( std::vector<wxString>( 1, fn.GetFullPath() ), KICTL_REVERT );

    return Empty();
}


tl::expected<bool, ApiResponseStatus> API_HANDLER_PCB::validateDocumentInternal( const DocumentSpecifier& aDocument ) const
{
    if( aDocument.type() != DocumentType::DOCTYPE_PCB )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "the requested document is not a board" );
        return tl::unexpected( e );
    }

    // The one comparison of the named project with this handler's project, shared with
    // API_HANDLER_EDITOR::validateDocument; its refusal names both projects, in the editor and in
    // a headless API server alike.
    if( std::optional<PROJECT_MISMATCH> mismatch = boardProjectMismatch( aDocument ) )
        return tl::unexpected( boardProjectRefusal( aDocument, *mismatch ) );

    wxFileName fn( pcbContext()->GetCurrentFileName() );

    if( aDocument.board_filename().compare( fn.GetFullName() ) != 0 )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "the requested document {} is not open",
                                          aDocument.board_filename() ) );
        return tl::unexpected( e );
    }

    return true;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_PCB::handleGetItems( const HANDLER_CONTEXT<GetItems>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    GetItemsResponse response;

    BOARD* board = this->board();
    std::vector<BOARD_ITEM*> items;
    std::set<KICAD_T> typesRequested, typesInserted;
    bool handledAnything = false;

    for( KICAD_T type : parseRequestedItemTypes( aCtx.Request.types() ) )
    {
        typesRequested.emplace( type );

        if( typesInserted.count( type ) )
            continue;

        switch( type )
        {
        case PCB_TRACE_T:
        case PCB_ARC_T:
        case PCB_VIA_T:
            handledAnything = true;
            std::copy( board->Tracks().begin(), board->Tracks().end(),
                       std::back_inserter( items ) );
            typesInserted.insert( { PCB_TRACE_T, PCB_ARC_T, PCB_VIA_T } );
            break;

        case PCB_PAD_T:
        {
            handledAnything = true;

            for( FOOTPRINT* fp : board->Footprints() )
            {
                std::copy( fp->Pads().begin(), fp->Pads().end(),
                           std::back_inserter( items ) );
            }

            typesInserted.insert( PCB_PAD_T );
            break;
        }

        case PCB_FOOTPRINT_T:
        {
            handledAnything = true;

            std::copy( board->Footprints().begin(), board->Footprints().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_FOOTPRINT_T );
            break;
        }

        case PCB_SHAPE_T:
        case PCB_TABLE_T:
        case PCB_TEXT_T:
        case PCB_TEXTBOX_T:
        case PCB_BARCODE_T:
        case PCB_REFERENCE_IMAGE_T:
        case PCB_GRIDITEM_T:
        {
            handledAnything = true;
            bool inserted = false;

            for( BOARD_ITEM* item : board->Drawings() )
            {
                if( item->Type() == type )
                {
                    items.emplace_back( item );
                    inserted = true;
                }
            }

            if( inserted )
                typesInserted.insert( type );

            break;
        }

        case PCB_DIMENSION_T:
        {
            handledAnything = true;
            bool inserted = false;

            for( BOARD_ITEM* item : board->Drawings() )
            {
                switch (item->Type()) {
                    case PCB_DIM_ALIGNED_T:
                    case PCB_DIM_CENTER_T:
                    case PCB_DIM_RADIAL_T:
                    case PCB_DIM_ORTHOGONAL_T:
                    case PCB_DIM_LEADER_T:
                        items.emplace_back( item );
                        inserted = true;
                        break;
                    default:
                        break;
                }
            }
            // we have to add the dimension subtypes to the requested to get them out
            typesRequested.insert( {PCB_DIM_ALIGNED_T, PCB_DIM_CENTER_T, PCB_DIM_RADIAL_T, PCB_DIM_ORTHOGONAL_T, PCB_DIM_LEADER_T } );

            if( inserted )
                typesInserted.insert( {PCB_DIM_ALIGNED_T, PCB_DIM_CENTER_T, PCB_DIM_RADIAL_T, PCB_DIM_ORTHOGONAL_T, PCB_DIM_LEADER_T } );

            break;
        }

        case PCB_ZONE_T:
        {
            handledAnything = true;

            std::copy( board->Zones().begin(), board->Zones().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_ZONE_T );
            break;
        }

        case PCB_GROUP_T:
        {
            handledAnything = true;

            std::copy( board->Groups().begin(), board->Groups().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_GROUP_T );
            break;
        }

        case PCB_POINT_T:
        {
            handledAnything = true;
            std::copy( board->Points().begin(), board->Points().end(), std::back_inserter( items ) );
            typesInserted.insert( PCB_POINT_T );
            break;
        }

        case PCB_CONSTRAINT_T:
        {
            handledAnything = true;

            std::copy( board->Constraints().begin(), board->Constraints().end(),
                       std::back_inserter( items ) );

            typesInserted.insert( PCB_CONSTRAINT_T );
            break;
        }

        default:
            break;
        }
    }

    if( !handledAnything )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Board object" );
        return tl::unexpected( e );
    }

    for( const BOARD_ITEM* item : items )
    {
        if( !typesRequested.count( item->Type() ) )
            continue;

        google::protobuf::Any itemBuf;
        item->Serialize( itemBuf );
        response.mutable_items()->Add( std::move( itemBuf ) );
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<BoardEnabledLayersResponse> API_HANDLER_PCB::handleSetBoardEnabledLayers(
        const HANDLER_CONTEXT<SetBoardEnabledLayers>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    if( aCtx.Request.copper_layer_count() % 2 != 0 )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "copper_layer_count must be an even number" );
        return tl::unexpected( e );
    }

    if( aCtx.Request.copper_layer_count() > MAX_CU_LAYERS )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "copper_layer_count must be below %d", MAX_CU_LAYERS ) );
        return tl::unexpected( e );
    }

    int copperLayerCount = static_cast<int>( aCtx.Request.copper_layer_count() );
    LSET enabled = board::UnpackLayerSet( aCtx.Request.layers() );

    // Sanitize the input
    enabled |= LSET( { Edge_Cuts, Margin, F_CrtYd, B_CrtYd } );
    enabled &= ~LSET::AllCuMask();
    enabled |= LSET::AllCuMask( copperLayerCount );

    BOARD* board = this->board();

    LSET previousEnabled = board->GetEnabledLayers();
    LSET changedLayers = enabled ^ previousEnabled;

    board->SetEnabledLayers( enabled );
    board->SetVisibleLayers( board->GetVisibleLayers() | changedLayers );

    LSEQ removedLayers;

    for( PCB_LAYER_ID layer_id : previousEnabled )
    {
        if( !enabled[layer_id] && board->HasItemsOnLayer( layer_id ) )
            removedLayers.push_back( layer_id );
    }

    bool modified = false;

    if( !removedLayers.empty() )
    {
        toolManager()->RunAction( PCB_ACTIONS::selectionClear );

        for( PCB_LAYER_ID layer_id : removedLayers )
            modified |= board->RemoveAllItemsOnLayer( layer_id );
    }

    if( frame() )
    {
        if( enabled != previousEnabled )
            frame()->UpdateUserInterface();

        if( modified || enabled != previousEnabled )
            frame()->OnModify();
    }
    else if( enabled != previousEnabled || modified )
        onModified();

    BoardEnabledLayersResponse response;

    response.set_copper_layer_count( copperLayerCount );
    board::PackLayerSet( *response.mutable_layers(), enabled );

    return response;
}


HANDLER_RESULT<common::types::EmbeddedFiles>
API_HANDLER_PCB::handleGetEmbeddedFiles( const HANDLER_CONTEXT<GetEmbeddedFiles>& aCtx )
{
    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() ); !documentValidation )
        return tl::unexpected( documentValidation.error() );

    common::types::EmbeddedFiles response;
    board::PackEmbeddedFiles( response, *board()->GetEmbeddedFiles() );
    return response;
}


HANDLER_RESULT<Empty> unpackEmbeddedFiles( EMBEDDED_FILES& aOutput, const common::types::EmbeddedFiles& aProto )
{
    if( !board::UnpackEmbeddedFiles( aOutput, aProto ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "embedded file validation failed" );
        return tl::unexpected( e );
    }

    return Empty();
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleAddEmbeddedFiles( const HANDLER_CONTEXT<AddEmbeddedFiles>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() ); !documentValidation )
        return tl::unexpected( documentValidation.error() );

    EMBEDDED_FILES files;
    HANDLER_RESULT<Empty> result = unpackEmbeddedFiles( files, aCtx.Request.files() );

    if( !result.has_value() )
        return result;

    EMBEDDED_FILES* boardFiles = board()->GetEmbeddedFiles();

    for( const std::shared_ptr<EMBEDDED_FILES::EMBEDDED_FILE>& file : files.EmbeddedFileMap() | std::views::values )
    {
        auto copy = std::make_shared<EMBEDDED_FILES::EMBEDDED_FILE>( *file );
        boardFiles->AddFile( copy );
    }

    onModified();
    return result;
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSetEmbeddedFiles( const HANDLER_CONTEXT<SetEmbeddedFiles>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() ); !documentValidation )
        return tl::unexpected( documentValidation.error() );

    HANDLER_RESULT<Empty> result = unpackEmbeddedFiles( *board()->GetEmbeddedFiles(),
                                                        aCtx.Request.files() );

    if( !result.has_value() )
        return result;

    onModified();
    return result;
}


HANDLER_RESULT<BoardDesignRulesResponse> API_HANDLER_PCB::handleGetBoardDesignRules(
        const HANDLER_CONTEXT<GetBoardDesignRules>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    const BOARD_DESIGN_SETTINGS& bds = board()->GetDesignSettings();
    BoardDesignRulesResponse     response;
    kiapi::board::BoardDesignRules* rules = response.mutable_rules();

    kiapi::board::MinimumConstraints* constraints = rules->mutable_constraints();

    constraints->mutable_min_clearance()->set_value_nm( bds.m_MinClearance );
    constraints->mutable_min_groove_width()->set_value_nm( bds.m_MinGrooveWidth );
    constraints->mutable_min_connection_width()->set_value_nm( bds.m_MinConn );
    constraints->mutable_min_track_width()->set_value_nm( bds.m_TrackMinWidth );
    constraints->mutable_min_via_annular_width()->set_value_nm( bds.m_ViasMinAnnularWidth );
    constraints->mutable_min_via_size()->set_value_nm( bds.m_ViasMinSize );
    constraints->mutable_min_through_drill()->set_value_nm( bds.m_MinThroughDrill );
    constraints->mutable_min_microvia_size()->set_value_nm( bds.m_MicroViasMinSize );
    constraints->mutable_min_microvia_drill()->set_value_nm( bds.m_MicroViasMinDrill );
    constraints->mutable_copper_edge_clearance()->set_value_nm( bds.m_CopperEdgeClearance );
    constraints->mutable_hole_clearance()->set_value_nm( bds.m_HoleClearance );
    constraints->mutable_hole_to_hole_min()->set_value_nm( bds.m_HoleToHoleMin );
    constraints->mutable_silk_clearance()->set_value_nm( bds.m_SilkClearance );
    constraints->set_min_resolved_spokes( bds.m_MinResolvedSpokes );
    constraints->mutable_min_silk_text_height()->set_value_nm( bds.m_MinSilkTextHeight );
    constraints->mutable_min_silk_text_thickness()->set_value_nm( bds.m_MinSilkTextThickness );

    kiapi::board::PredefinedSizes* sizes = rules->mutable_predefined_sizes();

    for( size_t ii = 1; ii < bds.m_TrackWidthList.size(); ++ii )
        sizes->add_tracks()->mutable_width()->set_value_nm( bds.m_TrackWidthList[ii] );

    for( size_t ii = 1; ii < bds.m_ViasDimensionsList.size(); ++ii )
    {
        kiapi::board::PresetViaDimension* via = sizes->add_vias();
        via->mutable_diameter()->set_value_nm( bds.m_ViasDimensionsList[ii].m_Diameter );
        via->mutable_drill()->set_value_nm( bds.m_ViasDimensionsList[ii].m_Drill );
    }

    for( size_t ii = 1; ii < bds.m_DiffPairDimensionsList.size(); ++ii )
    {
        kiapi::board::PresetDiffPairDimension* pair = sizes->add_diff_pairs();
        pair->mutable_width()->set_value_nm( bds.m_DiffPairDimensionsList[ii].m_Width );
        pair->mutable_gap()->set_value_nm( bds.m_DiffPairDimensionsList[ii].m_Gap );
        pair->mutable_via_gap()->set_value_nm( bds.m_DiffPairDimensionsList[ii].m_ViaGap );
    }

    kiapi::board::SolderMaskPasteDefaults* maskPaste = rules->mutable_solder_mask_paste();

    maskPaste->mutable_mask_expansion()->set_value_nm( bds.m_SolderMaskExpansion );
    maskPaste->mutable_mask_min_width()->set_value_nm( bds.m_SolderMaskMinWidth );
    maskPaste->mutable_mask_to_copper_clearance()->set_value_nm( bds.m_SolderMaskToCopperClearance );
    maskPaste->mutable_paste_margin()->set_value_nm( bds.m_SolderPasteMargin );
    maskPaste->set_paste_margin_ratio( bds.m_SolderPasteMarginRatio );
    maskPaste->set_allow_soldermask_bridges_in_footprints( bds.m_AllowSoldermaskBridgesInFPs );

    kiapi::board::TeardropDefaults* teardrops = rules->mutable_teardrops();

    teardrops->set_target_vias( bds.m_TeardropParamsList.m_TargetVias );
    teardrops->set_target_pth_pads( bds.m_TeardropParamsList.m_TargetPTHPads );
    teardrops->set_target_smd_pads( bds.m_TeardropParamsList.m_TargetSMDPads );
    teardrops->set_target_track_to_track( bds.m_TeardropParamsList.m_TargetTrack2Track );
    teardrops->set_use_round_shapes_only( bds.m_TeardropParamsList.m_UseRoundShapesOnly );

    TEARDROP_PARAMETERS_LIST& tdList = const_cast<TEARDROP_PARAMETERS_LIST&>( bds.m_TeardropParamsList );

    for( int target = TARGET_ROUND; target <= TARGET_TRACK; ++target )
    {
        const TEARDROP_PARAMETERS* params = tdList.GetParameters( static_cast<TARGET_TD>( target ) );
        kiapi::board::TeardropTargetEntry* entry = teardrops->add_target_params();

        entry->set_target( ToProtoEnum<TARGET_TD, kiapi::board::TeardropTarget>(
            static_cast<TARGET_TD>( target ) ) );
        entry->mutable_params()->set_enabled( params->m_Enabled );
        entry->mutable_params()->mutable_max_length()->set_value_nm( params->m_TdMaxLen );
        entry->mutable_params()->mutable_max_width()->set_value_nm( params->m_TdMaxWidth );
        entry->mutable_params()->set_best_length_ratio( params->m_BestLengthRatio );
        entry->mutable_params()->set_best_width_ratio( params->m_BestWidthRatio );
        entry->mutable_params()->set_width_to_size_filter_ratio( params->m_WidthtoSizeFilterRatio );
        entry->mutable_params()->set_curved_edges( params->m_CurvedEdges );
        entry->mutable_params()->set_allow_two_tracks( params->m_AllowUseTwoTracks );
        entry->mutable_params()->set_on_pads_in_zones( params->m_TdOnPadsInZones );
    }

    kiapi::board::ViaProtectionDefaults* viaProtection = rules->mutable_via_protection();

    viaProtection->set_tent_front( bds.m_TentViasFront );
    viaProtection->set_tent_back( bds.m_TentViasBack );
    viaProtection->set_cover_front( bds.m_CoverViasFront );
    viaProtection->set_cover_back( bds.m_CoverViasBack );
    viaProtection->set_plug_front( bds.m_PlugViasFront );
    viaProtection->set_plug_back( bds.m_PlugViasBack );
    viaProtection->set_cap( bds.m_CapVias );
    viaProtection->set_fill( bds.m_FillVias );

    for( const auto& [errorCode, severity] : bds.m_DRCSeverities )
    {
        board::DrcSeveritySetting* setting = rules->add_severities();
        setting->set_rule_type(
                ToProtoEnum<PCB_DRC_CODE, board::DrcErrorType>( static_cast<PCB_DRC_CODE>( errorCode ) ) );
        setting->set_severity( ToProtoEnum<SEVERITY, types::RuleSeverity>( severity ) );
    }

    for( const DRC_EXCLUSION& exclusion : bds.m_DrcExclusions )
        rules->add_exclusions()->CopyFrom( exclusion.ToProto() );

    response.set_custom_rules_status( CRS_NONE );

    wxString rulesPath = board()->GetDesignRulesPath();

    if( !rulesPath.IsEmpty() && wxFileName::IsFileReadable( rulesPath ) )
    {
        wxFFile file( rulesPath, "r" );
        wxString content;
        std::vector<std::shared_ptr<DRC_RULE>> parsedRules;

        if( !file.IsOpened() )
        {
            response.set_custom_rules_status( CRS_INVALID );
            return response;
        }

        file.ReadAll( &content );
        file.Close();

        try
        {
            DRC_RULES_PARSER parser( content, "File" );
            parser.Parse( parsedRules, nullptr );
            response.set_custom_rules_status( CRS_VALID );
        }
        catch( const IO_ERROR& )
        {
            response.set_custom_rules_status( CRS_INVALID );
        }
    }

    return response;
}


HANDLER_RESULT<BoardDesignRulesResponse> API_HANDLER_PCB::handleSetBoardDesignRules(
        const HANDLER_CONTEXT<SetBoardDesignRules>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    BOARD_DESIGN_SETTINGS newSettings( board()->GetDesignSettings() );
    const kiapi::board::BoardDesignRules& rules = aCtx.Request.rules();

    if( rules.has_constraints() )
    {
        const kiapi::board::MinimumConstraints& constraints = rules.constraints();

        newSettings.m_MinClearance = constraints.min_clearance().value_nm();
        newSettings.m_MinGrooveWidth = constraints.min_groove_width().value_nm();
        newSettings.m_MinConn = constraints.min_connection_width().value_nm();
        newSettings.m_TrackMinWidth = constraints.min_track_width().value_nm();
        newSettings.m_ViasMinAnnularWidth = constraints.min_via_annular_width().value_nm();
        newSettings.m_ViasMinSize = constraints.min_via_size().value_nm();
        newSettings.m_MinThroughDrill = constraints.min_through_drill().value_nm();
        newSettings.m_MicroViasMinSize = constraints.min_microvia_size().value_nm();
        newSettings.m_MicroViasMinDrill = constraints.min_microvia_drill().value_nm();
        newSettings.m_CopperEdgeClearance = constraints.copper_edge_clearance().value_nm();
        newSettings.m_HoleClearance = constraints.hole_clearance().value_nm();
        newSettings.m_HoleToHoleMin = constraints.hole_to_hole_min().value_nm();
        newSettings.m_SilkClearance = constraints.silk_clearance().value_nm();
        newSettings.m_MinResolvedSpokes = constraints.min_resolved_spokes();
        newSettings.m_MinSilkTextHeight = constraints.min_silk_text_height().value_nm();
        newSettings.m_MinSilkTextThickness = constraints.min_silk_text_thickness().value_nm();
    }

    if( rules.has_predefined_sizes() )
    {
        newSettings.m_TrackWidthList.clear();
        newSettings.m_TrackWidthList.emplace_back( 0 );

        for( const kiapi::board::PresetTrackWidth& track : rules.predefined_sizes().tracks() )
            newSettings.m_TrackWidthList.emplace_back( track.width().value_nm() );

        newSettings.m_ViasDimensionsList.clear();
        newSettings.m_ViasDimensionsList.emplace_back( 0, 0 );

        for( const kiapi::board::PresetViaDimension& via : rules.predefined_sizes().vias() )
        {
            newSettings.m_ViasDimensionsList.emplace_back( static_cast<int>( via.diameter().value_nm() ),
                                                           static_cast<int>( via.drill().value_nm() ) );
        }

        newSettings.m_DiffPairDimensionsList.clear();
        newSettings.m_DiffPairDimensionsList.emplace_back( 0, 0, 0 );

        for( const kiapi::board::PresetDiffPairDimension& pair : rules.predefined_sizes().diff_pairs() )
        {
            newSettings.m_DiffPairDimensionsList.emplace_back(
                    static_cast<int>( pair.width().value_nm() ),
                    static_cast<int>( pair.gap().value_nm() ),
                    static_cast<int>( pair.via_gap().value_nm() ) );
        }
    }

    if( rules.has_solder_mask_paste() )
    {
        const kiapi::board::SolderMaskPasteDefaults& maskPaste = rules.solder_mask_paste();

        newSettings.m_SolderMaskExpansion = maskPaste.mask_expansion().value_nm();
        newSettings.m_SolderMaskMinWidth = maskPaste.mask_min_width().value_nm();
        newSettings.m_SolderMaskToCopperClearance = maskPaste.mask_to_copper_clearance().value_nm();
        newSettings.m_SolderPasteMargin = maskPaste.paste_margin().value_nm();
        newSettings.m_SolderPasteMarginRatio = maskPaste.paste_margin_ratio();
        newSettings.m_AllowSoldermaskBridgesInFPs =
                maskPaste.allow_soldermask_bridges_in_footprints();
    }

    if( rules.has_teardrops() )
    {
        const kiapi::board::TeardropDefaults& teardrops = rules.teardrops();

        newSettings.m_TeardropParamsList.m_TargetVias = teardrops.target_vias();
        newSettings.m_TeardropParamsList.m_TargetPTHPads = teardrops.target_pth_pads();
        newSettings.m_TeardropParamsList.m_TargetSMDPads = teardrops.target_smd_pads();
        newSettings.m_TeardropParamsList.m_TargetTrack2Track = teardrops.target_track_to_track();
        newSettings.m_TeardropParamsList.m_UseRoundShapesOnly = teardrops.use_round_shapes_only();

        for( const kiapi::board::TeardropTargetEntry& entry : teardrops.target_params() )
        {
            if( entry.target() == kiapi::board::TeardropTarget::TDT_UNKNOWN )
                continue;

            TARGET_TD target = FromProtoEnum<TARGET_TD, kiapi::board::TeardropTarget>(
                    entry.target() );

            TEARDROP_PARAMETERS* params = newSettings.m_TeardropParamsList.GetParameters( target );

            params->m_Enabled = entry.params().enabled();
            params->m_TdMaxLen = entry.params().max_length().value_nm();
            params->m_TdMaxWidth = entry.params().max_width().value_nm();
            params->m_BestLengthRatio = entry.params().best_length_ratio();
            params->m_BestWidthRatio = entry.params().best_width_ratio();
            params->m_WidthtoSizeFilterRatio = entry.params().width_to_size_filter_ratio();
            params->m_CurvedEdges = entry.params().curved_edges();
            params->m_AllowUseTwoTracks = entry.params().allow_two_tracks();
            params->m_TdOnPadsInZones = entry.params().on_pads_in_zones();
        }
    }

    if( rules.has_via_protection() )
    {
        const kiapi::board::ViaProtectionDefaults& viaProtection = rules.via_protection();

        newSettings.m_TentViasFront = viaProtection.tent_front();
        newSettings.m_TentViasBack = viaProtection.tent_back();
        newSettings.m_CoverViasFront = viaProtection.cover_front();
        newSettings.m_CoverViasBack = viaProtection.cover_back();
        newSettings.m_PlugViasFront = viaProtection.plug_front();
        newSettings.m_PlugViasBack = viaProtection.plug_back();
        newSettings.m_CapVias = viaProtection.cap();
        newSettings.m_FillVias = viaProtection.fill();
    }

    if( rules.severities_size() > 0 )
    {
        newSettings.m_DRCSeverities.clear();

        for( const kiapi::board::DrcSeveritySetting& severitySetting : rules.severities() )
        {
            PCB_DRC_CODE ruleType =
                    FromProtoEnum<PCB_DRC_CODE, kiapi::board::DrcErrorType>( severitySetting.rule_type() );

            const std::unordered_set<SEVERITY> permitted( { RPT_SEVERITY_ERROR, RPT_SEVERITY_WARNING, RPT_SEVERITY_IGNORE } );
            SEVERITY setting = FromProtoEnum<SEVERITY, kiapi::common::types::RuleSeverity>( severitySetting.severity() );

            if( !permitted.contains( setting ) )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( fmt::format( "DRC severity must be error, warning, or ignore" ) );
                return tl::unexpected( e );
            }

            newSettings.m_DRCSeverities[ruleType] = setting;
        }
    }

    if( rules.exclusions_size() > 0 )
    {
        newSettings.m_DrcExclusions.clear();

        for( const kiapi::board::DrcExclusion& exclusion : rules.exclusions() )
            newSettings.m_DrcExclusions.insert( DRC_EXCLUSION::FromProto( exclusion ) );
    }

    std::vector<BOARD_DESIGN_SETTINGS::VALIDATION_ERROR> errors = newSettings.ValidateDesignRules();

    if( !errors.empty() )
    {
        const BOARD_DESIGN_SETTINGS::VALIDATION_ERROR& error = errors.front();

        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "Invalid board design rules: {}: {}",
                                          error.setting_name.ToStdString(),
                                          error.error_message.ToStdString() ) );
        return tl::unexpected( e );
    }

    board()->SetDesignSettings( newSettings );

    if( frame() )
    {
        frame()->OnModify();
        frame()->UpdateUserInterface();
    }
    else
        onModified();

    HANDLER_CONTEXT<GetBoardDesignRules> getCtx = { aCtx.ClientName, GetBoardDesignRules() };
    *getCtx.Request.mutable_board() = aCtx.Request.board();

    return handleGetBoardDesignRules( getCtx );
}


HANDLER_RESULT<CustomRulesResponse> API_HANDLER_PCB::handleGetCustomDesignRules(
        const HANDLER_CONTEXT<GetCustomDesignRules>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    CustomRulesResponse response;
    response.set_status( CRS_NONE );

    wxString rulesPath = board()->GetDesignRulesPath();

    if( rulesPath.IsEmpty() || !wxFileName::IsFileReadable( rulesPath ) )
        return response;

    wxFFile file( rulesPath, "r" );

    if( !file.IsOpened() )
    {
        response.set_status( CRS_INVALID );
        response.set_error_text( "Failed to open custom rules file" );
        return response;
    }

    wxString content;
    file.ReadAll( &content );
    file.Close();

    std::vector<std::shared_ptr<DRC_RULE>> parsedRules;

    try
    {
        DRC_RULES_PARSER parser( content, "File" );
        parser.Parse( parsedRules, nullptr );
    }
    catch( const IO_ERROR& ioe )
    {
        response.set_status( CRS_INVALID );
        response.set_error_text( ioe.What().ToStdString() );
        return response;
    }

    for( const std::shared_ptr<DRC_RULE>& rule : parsedRules )
    {
        // TODO(JE) since we now need this for both here and the rules editor, maybe it's time
        // to just make comment parsing part of the parser?
        wxString text = DRC_RULE_LOADER::ExtractRuleText( content, rule->m_Name );
        wxString comment = DRC_RULE_LOADER::ExtractRuleComment( text );

        kiapi::board::CustomRule* customRule = response.add_rules();

        if( rule->m_Condition )
            customRule->set_condition( rule->m_Condition->GetExpression().ToUTF8() );

        for( const DRC_CONSTRAINT& constraint : rule->m_Constraints )
        {
            board::CustomRuleConstraint* constraintProto = customRule->add_constraints();
            constraint.ToProto( *constraintProto );
        }

        customRule->set_severity( ToProtoEnum<SEVERITY, types::RuleSeverity>( rule->m_Severity ) );
        customRule->set_name( rule->m_Name.ToUTF8() );

        if( rule->m_LayerSource.CmpNoCase( wxS( "outer" ) ) == 0 )
        {
            customRule->set_layer_mode( kiapi::board::CRLM_OUTER );
        }
        else if( rule->m_LayerSource.CmpNoCase( wxS( "inner" ) ) == 0 )
        {
            customRule->set_layer_mode( kiapi::board::CRLM_INNER );
        }
        else if( !rule->m_LayerSource.IsEmpty() )
        {
            int layer = LSET::NameToLayer( rule->m_LayerSource );

            if( layer != UNDEFINED_LAYER && layer != UNSELECTED_LAYER && layer < PCB_LAYER_ID_COUNT )
            {
                customRule->set_single_layer(
                        ToProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>( static_cast<PCB_LAYER_ID>( layer ) ) );
            }
        }

        if( !comment.IsEmpty() )
            customRule->set_comments( comment );
    }

    response.set_status( CRS_VALID );
    return response;
}


HANDLER_RESULT<CustomRulesResponse> API_HANDLER_PCB::handleSetCustomDesignRules(
        const HANDLER_CONTEXT<SetCustomDesignRules>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    wxString rulesPath = board()->GetDesignRulesPath();

    if( aCtx.Request.rules_size() == 0 )
    {
        if( wxFileName::FileExists( rulesPath ) )
        {
            if( !wxRemoveFile( rulesPath ) )
            {
                CustomRulesResponse response;
                response.set_status( CRS_INVALID );
                response.set_error_text( "Failed to remove custom rules file" );
                return response;
            }
        }

        CustomRulesResponse response;
        response.set_status( CRS_NONE );
        refreshCustomRulesBaseline();
        board()->SetCustomRulesFileBaseline( FILE_CONTENT_BASELINE::Read( rulesPath ) );
        onModified();
        return response;
    }

    wxString rulesText;
    rulesText << "(version 2)\n";

    for( const board::CustomRule& rule : aCtx.Request.rules() )
    {
        wxString serializationError;
        wxString serializedRule = DRC_RULE::FormatRuleFromProto( rule, &serializationError );

        if( serializedRule.IsEmpty() )
        {
            CustomRulesResponse response;
            response.set_status( CRS_INVALID );

            if( serializationError.IsEmpty() )
                response.set_error_text( "Failed to serialize custom rule" );
            else
                response.set_error_text( serializationError.ToUTF8() );

            return response;
        }

        rulesText << "\n" << serializedRule;
    }

    // Validate generated file text before writing so callers get parser errors in response.
    try
    {
        std::vector<std::shared_ptr<DRC_RULE>> parsedRules;
        DRC_RULES_PARSER parser( rulesText, "SetCustomDesignRules" );
        parser.Parse( parsedRules, nullptr );
    }
    catch( const IO_ERROR& ioe )
    {
        CustomRulesResponse response;
        response.set_status( CRS_INVALID );
        response.set_error_text( ioe.What().ToStdString() );
        return response;
    }

    wxFFile file( rulesPath, "w" );

    if( !file.IsOpened() )
    {
        CustomRulesResponse response;
        response.set_status( CRS_INVALID );
        response.set_error_text( "Failed to open custom rules file for writing" );
        return response;
    }

    if( !file.Write( rulesText ) )
    {
        file.Close();

        CustomRulesResponse response;
        response.set_status( CRS_INVALID );
        response.set_error_text( "Failed to write custom rules file" );
        return response;
    }

    file.Close();
    refreshCustomRulesBaseline();
    board()->SetCustomRulesFileBaseline( FILE_CONTENT_BASELINE::Read( rulesPath ) );
    onModified();

    HANDLER_CONTEXT<GetCustomDesignRules> getCtx = { aCtx.ClientName, GetCustomDesignRules() };
    *getCtx.Request.mutable_board() = aCtx.Request.board();
    return handleGetCustomDesignRules( getCtx );
}


HANDLER_RESULT<types::Vector2> API_HANDLER_PCB::handleGetBoardOrigin(
        const HANDLER_CONTEXT<GetBoardOrigin>& aCtx )
{
    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );
        !documentValidation )
    {
        return tl::unexpected( documentValidation.error() );
    }

    VECTOR2I origin;
    const BOARD_DESIGN_SETTINGS& settings = board()->GetDesignSettings();

    switch( aCtx.Request.type() )
    {
    case BOT_GRID:
        origin = settings.GetGridOrigin();
        break;

    case BOT_DRILL:
        origin = settings.GetAuxOrigin();
        break;

    default:
    case BOT_UNKNOWN:
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "Unexpected origin type" );
        return tl::unexpected( e );
    }
    }

    types::Vector2 reply;
    PackVector2( reply, origin );
    return reply;
}

HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSetBoardOrigin(
        const HANDLER_CONTEXT<SetBoardOrigin>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );
        !documentValidation )
    {
        return tl::unexpected( documentValidation.error() );
    }

    VECTOR2I origin = UnpackVector2( aCtx.Request.origin() );

    switch( aCtx.Request.type() )
    {
    case BOT_GRID:
    {
        PCB_EDIT_FRAME* f = frame();

        if( f )
        {
            frame()->CallAfter(
                    [f, origin]()
                    {
                        // gridSetOrigin takes ownership and frees this
                        VECTOR2D*     dorigin = new VECTOR2D( origin );
                        TOOL_MANAGER* mgr = f->GetToolManager();
                        mgr->RunAction( PCB_ACTIONS::gridSetOrigin, dorigin );
                        f->Refresh();
                    } );
        }
        else
        {
            board()->GetDesignSettings().SetGridOrigin( origin );
        }

        break;
    }

    case BOT_DRILL:
    {
        PCB_EDIT_FRAME* f = frame();

        if( f )
        {
            frame()->CallAfter(
                    [f, origin]()
                    {
                        TOOL_MANAGER* mgr = f->GetToolManager();
                        mgr->RunAction( PCB_ACTIONS::drillSetOrigin, origin );
                        f->Refresh();
                    } );
        }
        else
        {
            board()->GetDesignSettings().SetAuxOrigin( origin );
        }

        break;
    }

    default:
    case BOT_UNKNOWN:
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "Unexpected origin type" );
        return tl::unexpected( e );
    }
    }

    if( !frame() )
        onModified();

    return Empty();
}


HANDLER_RESULT<BoardLayerNameResponse> API_HANDLER_PCB::handleGetBoardLayerName(
            const HANDLER_CONTEXT<GetBoardLayerName>& aCtx )
{
    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );
        !documentValidation )
    {
        return tl::unexpected( documentValidation.error() );
    }

    BoardLayerNameResponse response;

    PCB_LAYER_ID id = FromProtoEnum<PCB_LAYER_ID>( aCtx.Request.layer() );

    response.set_name( board()->GetLayerName( id ) );

    return response;
}


HANDLER_RESULT<BoardLayerResponse> API_HANDLER_PCB::handleGetBoardLayerByName(
            const HANDLER_CONTEXT<GetBoardLayerByName>& aCtx )
{
    if( HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );
        !documentValidation )
    {
        return tl::unexpected( documentValidation.error() );
    }

    BoardLayerResponse response;

    PCB_LAYER_ID id = board()->GetLayerID( wxString::FromUTF8( aCtx.Request.name() ) );
    response.set_layer( ToProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>( id ) );

    return response;
}


std::optional<TITLE_BLOCK*> API_HANDLER_PCB::getTitleBlock()
{
    return &context()->GetBoard()->GetTitleBlock();
}


std::optional<PAGE_INFO> API_HANDLER_PCB::getPageSettings()
{
    return context()->GetBoard()->GetPageSettings();
}


bool API_HANDLER_PCB::setPageSettings( const PAGE_INFO& aPageInfo )
{
    context()->GetBoard()->SetPageSettings( aPageInfo );
    return true;
}


wxString API_HANDLER_PCB::getDrawingSheetFileName()
{
    return project().GetProjectFile().m_BoardDrawingSheetFile;
}


void API_HANDLER_PCB::setDrawingSheetFileName( const wxString& aFileName )
{
    project().GetProjectFile().m_BoardDrawingSheetFile = aFileName;

    if( frame() )
        frame()->LoadDrawingSheet();
}


void API_HANDLER_PCB::onModified()
{
    if( frame() )
    {
        frame()->Refresh();
        frame()->OnModify();
        frame()->UpdateUserInterface();
    }
    else if( board() )
    {
        // Headless settings, title-block, embedded-file and rules-file handlers
        // have no frame OnModify callback. Keep their persisted state revisions
        // in the same checked admission stream as item commits.
        board()->IncrementTimeStamp();
    }
}


HANDLER_RESULT<NetsResponse> API_HANDLER_PCB::handleGetNets( const HANDLER_CONTEXT<GetNets>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    NetsResponse response;
    BOARD* board = this->board();

    std::set<wxString> netclassFilter;

    for( const std::string& nc : aCtx.Request.netclass_filter() )
        netclassFilter.insert( wxString( nc.c_str(), wxConvUTF8 ) );

    for( NETINFO_ITEM* net : board->GetNetInfo() )
    {
        NETCLASS* nc = net->GetNetClass();

        if( !netclassFilter.empty() && nc )
        {
            bool inClass = false;

            for( const wxString& filter : netclassFilter )
            {
                if( nc->ContainsNetclassWithName( filter ) )
                {
                    inClass = true;
                    break;
                }
            }

            if( !inClass )
                continue;
        }

        board::types::Net* netProto = response.add_nets();
        netProto->set_name( net->GetNetname() );
        netProto->mutable_code()->set_value( net->GetNetCode() );
    }

    return response;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_PCB::handleGetConnectedItems(
        const HANDLER_CONTEXT<GetConnectedItems>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    std::vector<KICAD_T> types = parseRequestedItemTypes( aCtx.Request.types() );
    const bool filterByType = aCtx.Request.types_size() > 0;

    if( filterByType && types.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Board object" );
        return tl::unexpected( e );
    }

    std::set<KICAD_T> typeFilter( types.begin(), types.end() );
    std::vector<BOARD_CONNECTED_ITEM*> sourceItems;

    for( const types::KIID& id : aCtx.Request.items() )
    {
        if( std::optional<BOARD_ITEM*> item = getItemById( KIID( id.value() ) ) )
        {
            if( BOARD_CONNECTED_ITEM* connected = dynamic_cast<BOARD_CONNECTED_ITEM*>( *item ) )
                sourceItems.emplace_back( connected );
        }
    }

    if( sourceItems.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested IDs were found or valid connected items" );
        return tl::unexpected( e );
    }

    GetItemsResponse response;
    std::shared_ptr<CONNECTIVITY_DATA> conn = board()->GetConnectivity();
    std::set<KIID> insertedItems;

    for( BOARD_CONNECTED_ITEM* source : sourceItems )
    {
        for( BOARD_CONNECTED_ITEM* connected : conn->GetConnectedItems( source ) )
        {
            if( filterByType && !typeFilter.contains( connected->Type() ) )
                continue;

            if( !insertedItems.insert( connected->m_Uuid ).second )
                continue;

            connected->Serialize( *response.add_items() );
        }
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_PCB::handleGetItemsByNet(
        const HANDLER_CONTEXT<GetItemsByNet>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    std::vector<KICAD_T> types = parseRequestedItemTypes( aCtx.Request.types() );
    const bool filterByType = aCtx.Request.types_size() > 0;

    if( filterByType && types.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Board object" );
        return tl::unexpected( e );
    }

    if( !filterByType )
        types.assign( { PCB_PAD_T, PCB_VIA_T, PCB_TRACE_T, PCB_ARC_T, PCB_SHAPE_T, PCB_ZONE_T } );

    GetItemsResponse response;
    BOARD* board = this->board();
    std::shared_ptr<CONNECTIVITY_DATA> conn = board->GetConnectivity();
    std::set<KIID> insertedItems;

    const NETINFO_LIST& nets = board->GetNetInfo();

    for( const board::types::Net& net : aCtx.Request.nets() )
    {
        NETINFO_ITEM* netInfo = nets.GetNetItem( wxString::FromUTF8( net.name() ) );

        if( !netInfo )
            continue;

        for( BOARD_CONNECTED_ITEM* item : conn->GetNetItems( netInfo->GetNetCode(), types ) )
        {
            if( !insertedItems.insert( item->m_Uuid ).second )
                continue;

            item->Serialize( *response.add_items() );
        }
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_PCB::handleGetItemsByNetClass(
        const HANDLER_CONTEXT<GetItemsByNetClass>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    std::vector<KICAD_T> types = parseRequestedItemTypes( aCtx.Request.types() );
    const bool filterByType = aCtx.Request.types_size() > 0;

    if( filterByType && types.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Board object" );
        return tl::unexpected( e );
    }

    if( !filterByType )
        types.assign( { PCB_PAD_T, PCB_VIA_T, PCB_TRACE_T, PCB_ARC_T, PCB_SHAPE_T, PCB_ZONE_T } );

    std::set<wxString> requestedClasses;

    for( const std::string& netClass : aCtx.Request.net_classes() )
        requestedClasses.insert( wxString( netClass.c_str(), wxConvUTF8 ) );

    GetItemsResponse response;
    BOARD* board = this->board();
    std::shared_ptr<CONNECTIVITY_DATA> conn = board->GetConnectivity();
    std::set<KIID> insertedItems;

    for( NETINFO_ITEM* net : board->GetNetInfo() )
    {
        if( !net )
            continue;

        NETCLASS* nc = net->GetNetClass();

        if( !requestedClasses.empty() )
        {
            if( !nc )
                continue;

            bool inClass = false;

            for( const wxString& filter : requestedClasses )
            {
                if( nc->ContainsNetclassWithName( filter ) )
                {
                    inClass = true;
                    break;
                }
            }

            if( !inClass )
                continue;
        }

        for( BOARD_CONNECTED_ITEM* item : conn->GetNetItems( net->GetNetCode(), types ) )
        {
            if( !insertedItems.insert( item->m_Uuid ).second )
                continue;

            item->Serialize( *response.add_items() );
        }
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<NetClassForNetsResponse> API_HANDLER_PCB::handleGetNetClassForNets(
            const HANDLER_CONTEXT<GetNetClassForNets>& aCtx )
{
    NetClassForNetsResponse response;

    BOARD* board = this->board();
    const NETINFO_LIST& nets = board->GetNetInfo();
    for( const board::types::Net& net : aCtx.Request.net() )
    {
        NETINFO_ITEM* netInfo = nets.GetNetItem( wxString::FromUTF8( net.name() ) );

        if( !netInfo )
            continue;

        auto [pair, rc] = response.mutable_classes()->insert( { net.name(), {} } );
        netInfo->GetNetClass()->Serialize( pair->second );
    }

    return response;
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleRefillZones( const HANDLER_CONTEXT<RefillZones>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );
    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    const std::string operationId = aCtx.Request.operation_id();
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( !operationId.empty() )
    {
        auto existing = m_operationReceipts.find( operationId );
        if( existing != m_operationReceipts.end() )
        {
            if( existing->second->fingerprint != fingerprint )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "The operation ID is already bound to a different PCB operation" );
                return tl::unexpected( e );
            }
            return Empty();
        }
    }

    auto receipt = std::make_shared<PCB_OPERATION_RECEIPT>();
    receipt->operation = "zone-refill";
    receipt->fingerprint = fingerprint;
    receipt->document.CopyFrom( aCtx.Request.board() );
    receipt->request = fingerprint;
    receipt->processEpoch = apiServer().Token();

    auto mapOutcome = []( ZONE_FILLER::OUTCOME aOutcome )
    {
        switch( aOutcome )
        {
        case ZONE_FILLER::OUTCOME::NOT_RUN: return kiapi::automation::v1::NZFO_NOT_RUN;
        case ZONE_FILLER::OUTCOME::COMPLETED: return kiapi::automation::v1::NZFO_COMPLETED;
        case ZONE_FILLER::OUTCOME::CANCELLED: return kiapi::automation::v1::NZFO_CANCELLED;
        case ZONE_FILLER::OUTCOME::NOT_CONVERGED: return kiapi::automation::v1::NZFO_NOT_CONVERGED;
        case ZONE_FILLER::OUTCOME::UNCHANGED_OR_DECLINED:
            return kiapi::automation::v1::NZFO_UNCHANGED_OR_DECLINED;
        case ZONE_FILLER::OUTCOME::FAILED: return kiapi::automation::v1::NZFO_FAILED;
        }
        return kiapi::automation::v1::NZFO_UNKNOWN;
    };

    auto applyResult = [receipt, mapOutcome]( const ZONE_FILL_RESULT& aResult )
    {
        std::lock_guard lock( receipt->mutex );
        receipt->terminal = true;
        receipt->zoneFillOutcome = mapOutcome( aResult.outcome );
        receipt->primaryFillOutcome = mapOutcome( aResult.primaryOutcome );
        receipt->generatorRefillOutcome = mapOutcome( aResult.generatorRefillOutcome );
        receipt->generatorRefillRequired = aResult.generatorRefillRequired;
        receipt->primaryCommitApplied = aResult.primaryCommitApplied;
        receipt->generatorRefillCommitApplied = aResult.generatorRefillCommitApplied;
        receipt->rollbackCompleted = aResult.rollbackCompleted;
        receipt->failureStage = aResult.failureStage;
        receipt->errorCode = aResult.errorCode;
        receipt->errorMessage = aResult.errorMessage;
        receipt->commitStateKnown = aResult.commitStateKnown;
        receipt->completed = aResult.Completed();
    };

    TOOL_MANAGER* mgr = toolManager();
    if( !mgr->FindTool( ZONE_FILLER_TOOL_NAME ) )
        mgr->RegisterTool( new ZONE_FILLER_TOOL );

    if( aCtx.Request.zones().empty() )
    {
        if( !operationId.empty() )
            m_operationReceipts.emplace( operationId, receipt );

        if( PCB_EDIT_FRAME* currentFrame = frame() )
        {
            wxWeakRef<PCB_EDIT_FRAME> weakFrame( currentFrame );
            KIID boardIdentity = board()->m_Uuid;
            auto completion = applyResult;
            currentFrame->CallAfter(
                    [weakFrame, boardIdentity, completion]() mutable
                    {
                        ZONE_FILL_RESULT failed;
                        failed.cleanupFinished = true;
                        failed.commitStateKnown = false;
                        failed.failureStage = "queued-dispatch";
                        failed.errorCode = "zone_fill_target_unavailable";

                        PCB_EDIT_FRAME* liveFrame = weakFrame.get();
                        if( !liveFrame || !liveFrame->GetBoard()
                            || liveFrame->GetBoard()->m_Uuid != boardIdentity )
                        {
                            failed.errorMessage = "The queued zone fill target was closed or replaced.";
                            completion( failed );
                            return;
                        }

                        TOOL_MANAGER* liveManager = liveFrame->GetToolManager();
                        if( !liveManager )
                        {
                            failed.errorMessage = "The queued zone fill tool manager is unavailable.";
                            completion( failed );
                            return;
                        }

                        ZONE_FILL_REQUEST request;
                        request.completion = completion;
                        if( !liveManager->RunAction( PCB_ACTIONS::zoneFillAll, request ) )
                        {
                            failed.errorMessage = "The queued zone fill action was not accepted.";
                            completion( failed );
                        }
                    } );
        }
        else
        {
            ZONE_FILL_RESULT result = mgr->GetTool<ZONE_FILLER_TOOL>()->FillAllZonesWithResult(
                    nullptr, nullptr, true );
            applyResult( result );
            onModified();
        }
    }
    else
    {
        std::vector<ZONE*> toFill;
        for( const types::KIID& id : aCtx.Request.zones() )
        {
            std::optional<BOARD_ITEM*> item = getItemById( KIID( id.value() ) );
            if( !item || ( *item )->Type() != PCB_ZONE_T )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( fmt::format( "zone with ID {} not found on the board", id.value() ) );
                return tl::unexpected( e );
            }

            ZONE* zone = static_cast<ZONE*>( *item );
            if( zone->GetIsRuleArea() )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( fmt::format( "zone with ID {} is a rule area and cannot be filled",
                                                  id.value() ) );
                return tl::unexpected( e );
            }

            if( !alg::contains( toFill, zone ) )
                toFill.push_back( zone );
        }

        if( !operationId.empty() )
            m_operationReceipts.emplace( operationId, receipt );

        ZONE_FILL_RESULT result;
        result.generatorRefillOutcome = ZONE_FILLER::OUTCOME::NOT_RUN;
        std::unique_ptr<COMMIT> commit = createCommit();
        bool pushStarted = false;
        bool commitApplied = false;
        try
        {
            ZONE_FILLER filler( board(), commit.get() );
            bool accepted = filler.Fill( toFill );
            result.primaryOutcome = filler.LastOutcome();
            result.outcome = result.primaryOutcome;
            result.generatorRefillRequired = false;
            if( accepted )
            {
                pushStarted = true;
                result.primaryCommitApplied = !commit->Empty();
                commit->Push( _( "Fill Zone(s)" ), SKIP_CONNECTIVITY | ZONE_FILL_OP );
                pushStarted = false;
                commitApplied = result.primaryCommitApplied;
            }
            else
            {
                commit->Revert();
                result.rollbackCompleted = true;
            }
            result.commitStateKnown = true;
            result.cleanupFinished = true;
            if( result.outcome != ZONE_FILLER::OUTCOME::COMPLETED )
            {
                result.errorCode = result.outcome == ZONE_FILLER::OUTCOME::CANCELLED
                        ? "zone_fill_cancelled" : "zone_fill_failed";
                result.errorMessage = "The selected zone fill did not complete successfully.";
            }
            mgr->GetTool<ZONE_FILLER_TOOL>()->PostFillRefresh( frame() == nullptr );
            onModified();
        }
        catch( const std::exception& error )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_failed";
            result.errorMessage = error.what();
            if( pushStarted || commitApplied )
            {
                result.commitStateKnown = !pushStarted;
                result.rollbackCompleted = false;
            }
            else
            {
                try { commit->Revert(); result.rollbackCompleted = true; }
                catch( ... ) { result.commitStateKnown = false; result.rollbackCompleted = false; }
            }
            result.cleanupFinished = true;
        }
        catch( ... )
        {
            result.outcome = ZONE_FILLER::OUTCOME::FAILED;
            result.errorCode = "zone_fill_failed";
            result.errorMessage = "The selected zone fill raised an unknown exception.";
            result.commitStateKnown = !pushStarted;
            result.rollbackCompleted = false;
            if( !pushStarted && !commitApplied )
            {
                try { commit->Revert(); result.rollbackCompleted = true; }
                catch( ... ) { result.commitStateKnown = false; }
            }
            result.cleanupFinished = true;
        }
        applyResult( result );
    }

    return Empty();
}


HANDLER_RESULT<ImportNetlistResponse> API_HANDLER_PCB::handleImportNetlist( const HANDLER_CONTEXT<ImportNetlist>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    const std::string operationId = aCtx.Request.operation_id();
    const std::string fingerprint = aCtx.Request.SerializeAsString();
    if( !operationId.empty() )
    {
        auto existing = m_operationReceipts.find( operationId );
        if( existing != m_operationReceipts.end() )
        {
            if( existing->second->fingerprint != fingerprint || !existing->second->importResponse )
            {
                ApiResponseStatus e;
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "The operation ID is already bound to a different PCB operation" );
                return tl::unexpected( e );
            }
            return *existing->second->importResponse;
        }
    }

    wxFileName netlistPath( project().AbsolutePath( wxString::FromUTF8( aCtx.Request.netlist_path() ) ) );

    if( !netlistPath.IsOk() || !netlistPath.FileExists() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message(
                fmt::format( "netlist file '{}' could not be opened", netlistPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    PCB_CONTEXT*       ctx = pcbContext();
    WX_STRING_REPORTER reporter;

    const bool lookupByTimestamp = aCtx.Request.match_mode() != NetlistMatchMode::NMM_REFERENCE;

    NETLIST netlist;
    netlist.SetFindByTimeStamp( lookupByTimestamp );
    netlist.SetReplaceFootprints( aCtx.Request.update_footprints() );

    if( !ctx->ReadNetlistFromFile( netlistPath.GetFullPath(), netlist, reporter ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "unable to handle netlist file '{}': {}",
                                          netlistPath.GetFullPath().ToStdString(),
                                          reporter.GetMessages().ToStdString() ) );
        return tl::unexpected( e );
    }

    std::unique_ptr<BOARD_NETLIST_UPDATER> updater = ctx->MakeNetlistUpdater();

    updater->SetReporter( &reporter );
    updater->SetIsDryRun( aCtx.Request.dry_run() );
    updater->SetLookupByTimestamp( lookupByTimestamp );
    updater->SetDeleteUnusedFootprints( aCtx.Request.delete_extra_footprints() );
    updater->SetReplaceFootprints( aCtx.Request.update_footprints() );
    updater->SetTransferGroups( aCtx.Request.transfer_groups() );
    updater->SetOverrideLocks( aCtx.Request.override_locks() );
    updater->SetUpdateFields( true );

    const bool success = updater->UpdateNetlist( netlist );

    if( !aCtx.Request.dry_run() && success )
    {
        ctx->OnNetlistChanged( *updater );
        onModified();
    }

    ImportNetlistResponse response;
    response.set_report( reporter.GetMessages().ToUTF8() );
    response.set_error_count( updater->GetErrorCount() );
    response.set_warning_count( updater->GetWarningCount() );
    response.set_new_footprint_count( updater->GetNewFootprintCount() );
    if( !operationId.empty() )
    {
        auto receipt = std::make_shared<PCB_OPERATION_RECEIPT>();
        receipt->operation = "netlist-import";
        receipt->fingerprint = fingerprint;
        receipt->document.CopyFrom( aCtx.Request.board() );
        receipt->request = fingerprint;
        receipt->completed = true;
        receipt->importResponse = response;
        receipt->processEpoch = apiServer().Token();
        m_operationReceipts.emplace( operationId, std::move( receipt ) );
    }
    return response;
}


HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse>
API_HANDLER_PCB::handleReadPcbOperation(
        const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityPcbReadOperation>& aCtx )
{
    using namespace kiapi::automation::v1;
    NativeCapabilityPcbReadOperationResponse response;
    response.set_operation_id( aCtx.Request.operation_id() );
    if( aCtx.Request.operation_id().empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "A PCB operation ID is required" );
        return tl::unexpected( e );
    }

    auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
    if( found == m_operationReceipts.end() )
    {
        response.set_found( false );
        return response;
    }

    response.set_found( true );
    std::lock_guard receiptLock( found->second->mutex );
    if( found->second->manufacturingExport )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_UNIMPLEMENTED );
        error.set_error_message( "This operation ID is a PCB manufacturing export; read it with ReadPcbManufacturingExport" );
        return tl::unexpected( error );
    }
    response.set_operation( found->second->operation );
    response.mutable_document()->CopyFrom( found->second->document );
    response.set_completed( found->second->completed );
    response.set_process_epoch( found->second->processEpoch );
    if( found->second->terminal )
    {
        response.set_terminal( true );
        response.set_zone_fill_outcome( found->second->zoneFillOutcome );
        response.set_primary_fill_outcome( found->second->primaryFillOutcome );
        response.set_generator_refill_outcome( found->second->generatorRefillOutcome );
        response.set_generator_refill_required( found->second->generatorRefillRequired );
        response.set_primary_commit_applied( found->second->primaryCommitApplied );
        response.set_generator_refill_commit_applied( found->second->generatorRefillCommitApplied );
        response.set_rollback_completed( found->second->rollbackCompleted );
        response.set_failure_stage( found->second->failureStage );
        response.set_error_code( found->second->errorCode );
        response.set_error_message( found->second->errorMessage );
        response.set_commit_state_known( found->second->commitStateKnown );
    }
    if( found->second->importResponse )
    {
        response.set_error_count( found->second->importResponse->error_count() );
        response.set_warning_count( found->second->importResponse->warning_count() );
        response.set_new_footprint_count( found->second->importResponse->new_footprint_count() );
        response.set_report( found->second->importResponse->report() );
    }
    return response;
}


HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceipt>
API_HANDLER_PCB::handleRunPcbManufacturingExport(
        const HANDLER_CONTEXT<kiapi::automation::v1::RunPcbManufacturingExport>& aCtx )
{
    using namespace kiapi::automation::v1;
    const RunPcbManufacturingExport& request = aCtx.Request;

    if( !IsOperationId( request.operation_id() ) )
        return tl::unexpected( ManufacturingError( "A canonical operation UUID is required for a PCB manufacturing export" ) );

    std::string payloadReason;
    if( !ValidatePayloadMetadata( request, payloadReason ) )
        return tl::unexpected( ManufacturingError( payloadReason ) );

    const std::string normalizedBytes = request.SerializeAsString();
    NATIVE_STATE_DIGEST requestDigest;
    requestDigest.Append( normalizedBytes );

    auto existing = m_operationReceipts.find( request.operation_id() );
    if( existing != m_operationReceipts.end() )
    {
        std::lock_guard lock( existing->second->mutex );
        if( !existing->second->manufacturingExport
            || existing->second->fingerprint != normalizedBytes )
        {
            return tl::unexpected( ManufacturingError(
                    "The operation ID is already bound to a different PCB operation" ) );
        }

        return *existing->second->manufacturingExport;
    }

    if( request.job_case() == RunPcbManufacturingExport::JOB_NOT_SET )
        return tl::unexpected( ManufacturingError( "Exactly one PCB manufacturing job is required" ) );
    if( request.document().type() != DocumentType::DOCTYPE_PCB
        || request.expected_state().scope() != DocumentLifecycleScope::DLS_PCB
        || !google::protobuf::util::MessageDifferencer::Equivalent(
                request.expected_state().document(), request.document() )
        || request.expected_state().process_epoch() != request.process_epoch()
        || request.process_epoch() != apiServer().Token()
        || request.expected_state().state_sha256().empty() )
    {
        return tl::unexpected( ManufacturingError( "The manufacturing export must bind one explicit PCB, process epoch and lifecycle state" ) );
    }

    if( !NativeManufacturingPublicationAvailable() )
        return tl::unexpected( ManufacturingError(
                "Atomic no-replace directory publication is unavailable on this platform",
                ApiStatusCode::AS_UNIMPLEMENTED ) );

    if( auto valid = validateDocument( request.document() ); !valid )
        return tl::unexpected( valid.error() );

    const fs::path requestedOutputDirectory = fs::u8path( request.output_directory() );
    if( request.output_directory().empty() || !requestedOutputDirectory.is_absolute() )
        return tl::unexpected( ManufacturingError( "outputDirectory must be an absolute path" ) );
    const fs::path outputDirectory = requestedOutputDirectory.lexically_normal();

    std::string pathReason;
    if( ExistingPath( outputDirectory, pathReason ) )
        return tl::unexpected( ManufacturingError( "The output directory must be new: " + pathReason ) );

    const fs::path parent = outputDirectory.parent_path();
    std::error_code parentError;
    if( parent.empty() || !fs::is_directory( fs::symlink_status( parent, parentError ) ) || parentError )
        return tl::unexpected( ManufacturingError( "The output directory parent must already be an accessible directory" ) );

    const fs::path stagingDirectory = parent
            / ( outputDirectory.filename().string() + ".kicad-staging-" + request.operation_id() );
    if( ExistingPath( stagingDirectory, pathReason ) )
        return tl::unexpected( ManufacturingError( "The operation's staging directory already exists: " + pathReason ) );

    HANDLER_CONTEXT<kiapi::automation::v1::ReadDocumentLifecycleState> lifecycleContext;
    lifecycleContext.Request.mutable_document()->CopyFrom( request.document() );
    HANDLER_RESULT<DocumentLifecycleState> before = handleReadLifecycleState( lifecycleContext );
    if( !before )
        return tl::unexpected( before.error() );
    if( !google::protobuf::util::MessageDifferencer::Equivalent( *before, request.expected_state() ) )
        return tl::unexpected( ManufacturingError(
                "The PCB changed before the manufacturing export started", ApiStatusCode::AS_BUSY ) );

    auto receipt = std::make_shared<PCB_OPERATION_RECEIPT>();
    receipt->operation = "pcb-manufacturing-export";
    receipt->fingerprint = normalizedBytes;
    receipt->document.CopyFrom( request.document() );
    receipt->request = normalizedBytes;
    receipt->processEpoch = apiServer().Token();
    receipt->manufacturingExport.emplace();
    PcbManufacturingExportReceipt& retained = *receipt->manufacturingExport;
    retained.set_operation_id( request.operation_id() );
    retained.set_process_epoch( apiServer().Token() );
    retained.mutable_document()->CopyFrom( request.document() );
    retained.set_request_fingerprint_sha256( requestDigest.Hex() );
    retained.set_job_kind( ManufacturingJobKind( request ) );
    retained.set_status( PCB_MFG_EXPORT_ADMITTED );
    retained.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
    retained.set_native_dispatch( false );
    retained.set_terminal( false );
    retained.mutable_expected_state()->CopyFrom( request.expected_state() );
    retained.mutable_observed_before()->CopyFrom( *before );
    retained.set_output_directory( WxPath( outputDirectory ).ToStdString() );
    retained.set_staging_directory( WxPath( stagingDirectory ).ToStdString() );
    retained.set_publication_state( PCB_MFG_PUBLICATION_STAGED );
    // Reserve the bounded manifest capacity before native dispatch. Inventory
    // still fails closed if the byte budget or file count is exceeded.
    retained.mutable_artifacts()->Reserve( 4096 );
    m_operationReceipts.emplace( request.operation_id(), receipt );

    std::error_code createError;
    if( !fs::create_directory( stagingDirectory, createError ) || createError )
    {
        std::lock_guard lock( receipt->mutex );
        retained.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
        retained.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
        retained.set_terminal( true );
        retained.set_error_code( "export_staging_failed" );
        retained.set_error_message( "The owned export staging directory could not be created: "
                                     + createError.message() );
        return retained;
    }

    const bool outputIsDirectory = ManufacturingOutputIsDirectory( request );
    fs::path stagedOutput = stagingDirectory;
    if( !outputIsDirectory )
    {
        const std::string extension = ManufacturingOutputExtension( request );
        stagedOutput /= extension.empty() ? "export" : "export." + extension;
    }
    const wxString stagedOutputPath = WxPath( stagedOutput );

    if( request.job_case() == RunPcbManufacturingExport::kExportRender )
    {
        const RunBoardJobExportRender& render = request.export_render();
        if( render.width() < 64 || render.width() > 2048
            || render.height() < 64 || render.height() > 2048
            || static_cast<uint64_t>( render.width() ) * static_cast<uint64_t>( render.height() ) > 8'388'608ULL )
        {
            std::lock_guard lock( receipt->mutex );
            retained.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
            retained.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
            retained.set_terminal( true );
            retained.set_error_code( "invalid_render_dimensions" );
            retained.set_error_message( "Render dimensions must be 64..2048 pixels with at most 8,388,608 pixels" );
            return retained;
        }
        if( !std::isfinite( render.zoom() ) || render.zoom() <= 0.0 )
        {
            std::lock_guard lock( receipt->mutex );
            retained.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
            retained.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
            retained.set_terminal( true );
            retained.set_error_code( "invalid_render_zoom" );
            retained.set_error_message( "Render zoom must be finite and greater than zero" );
            return retained;
        }
    }

    {
        std::lock_guard lock( receipt->mutex );
        retained.set_status( PCB_MFG_EXPORT_RUNNING );
        retained.set_dispatch_stage( PCB_MFG_DISPATCH_SUBMITTED );
        retained.set_native_dispatch( true );
    }

    HANDLER_RESULT<types::RunJobResponse> jobResult = tl::unexpected(
            ManufacturingError( "The native manufacturing job did not return a terminal response" ) );

    try
    {
    switch( request.job_case() )
    {
    case RunPcbManufacturingExport::kExport3d:
    {
        HANDLER_CONTEXT<RunBoardJobExport3D> child;
        child.Request.CopyFrom( request.export_3d() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExport3D( child );
        break;
    }
    case RunPcbManufacturingExport::kExportRender:
    {
        HANDLER_CONTEXT<RunBoardJobExportRender> child;
        child.Request.CopyFrom( request.export_render() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportRender( child );
        break;
    }
    case RunPcbManufacturingExport::kExportSvg:
    {
        HANDLER_CONTEXT<RunBoardJobExportSvg> child;
        child.Request.CopyFrom( request.export_svg() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportSvg( child );
        break;
    }
    case RunPcbManufacturingExport::kExportDxf:
    {
        HANDLER_CONTEXT<RunBoardJobExportDxf> child;
        child.Request.CopyFrom( request.export_dxf() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportDxf( child );
        break;
    }
    case RunPcbManufacturingExport::kExportPdf:
    {
        HANDLER_CONTEXT<RunBoardJobExportPdf> child;
        child.Request.CopyFrom( request.export_pdf() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportPdf( child );
        break;
    }
    case RunPcbManufacturingExport::kExportPs:
    {
        HANDLER_CONTEXT<RunBoardJobExportPs> child;
        child.Request.CopyFrom( request.export_ps() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportPs( child );
        break;
    }
    case RunPcbManufacturingExport::kExportGerbers:
    {
        HANDLER_CONTEXT<RunBoardJobExportGerbers> child;
        child.Request.CopyFrom( request.export_gerbers() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportGerbers( child );
        break;
    }
    case RunPcbManufacturingExport::kExportDrill:
    {
        HANDLER_CONTEXT<RunBoardJobExportDrill> child;
        child.Request.CopyFrom( request.export_drill() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportDrill( child );
        break;
    }
    case RunPcbManufacturingExport::kExportPosition:
    {
        HANDLER_CONTEXT<RunBoardJobExportPosition> child;
        child.Request.CopyFrom( request.export_position() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportPosition( child );
        break;
    }
    case RunPcbManufacturingExport::kExportGencad:
    {
        HANDLER_CONTEXT<RunBoardJobExportGencad> child;
        child.Request.CopyFrom( request.export_gencad() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportGencad( child );
        break;
    }
    case RunPcbManufacturingExport::kExportIpc2581:
    {
        HANDLER_CONTEXT<RunBoardJobExportIpc2581> child;
        child.Request.CopyFrom( request.export_ipc2581() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportIpc2581( child );
        break;
    }
    case RunPcbManufacturingExport::kExportIpcD356:
    {
        HANDLER_CONTEXT<RunBoardJobExportIpcD356> child;
        child.Request.CopyFrom( request.export_ipc_d356() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportIpcD356( child );
        break;
    }
    case RunPcbManufacturingExport::kExportOdb:
    {
        HANDLER_CONTEXT<RunBoardJobExportODB> child;
        child.Request.CopyFrom( request.export_odb() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportODB( child );
        break;
    }
    case RunPcbManufacturingExport::kExportStats:
    {
        HANDLER_CONTEXT<RunBoardJobExportStats> child;
        child.Request.CopyFrom( request.export_stats() );
        child.Request.mutable_job_settings()->mutable_document()->CopyFrom( request.document() );
        child.Request.mutable_job_settings()->set_output_path( stagedOutputPath.ToStdString() );
        jobResult = handleRunBoardJobExportStats( child );
        break;
    }
    case RunPcbManufacturingExport::JOB_NOT_SET:
        break;
    }
    }
    catch( const std::exception& error )
    {
        std::lock_guard lock( receipt->mutex );
        retained.set_status( PCB_MFG_EXPORT_INDETERMINATE );
        retained.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
        retained.set_terminal( true );
        retained.set_error_code( "native_export_exception" );
        retained.set_error_message( error.what() );
        return retained;
    }
    catch( ... )
    {
        std::lock_guard lock( receipt->mutex );
        retained.set_status( PCB_MFG_EXPORT_INDETERMINATE );
        retained.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
        retained.set_terminal( true );
        retained.set_error_code( "native_export_exception" );
        retained.set_error_message( "The native manufacturing exporter raised an unknown exception" );
        return retained;
    }

    auto setReceipt = [&]( auto&& aUpdate )
    {
        std::lock_guard lock( receipt->mutex );
        aUpdate( *receipt->manufacturingExport );
    };

    if( !jobResult )
    {
        setReceipt( [&]( PcbManufacturingExportReceipt& value )
        {
            value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            value.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
            value.set_terminal( true );
            value.set_error_code( "native_export_error" );
            value.set_error_message( jobResult.error().error_message() );
        } );
        return *receipt->manufacturingExport;
    }

    if( jobResult->status() != types::JobStatus::JS_SUCCESS )
    {
        std::string manifestReason;
        setReceipt( [&]( PcbManufacturingExportReceipt& value )
        {
            value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            value.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
            value.set_terminal( true );
            value.set_error_code( "native_export_failed" );
            value.set_error_message( jobResult->message() );
            InventoryManufacturingArtifacts( stagingDirectory, value, manifestReason );
        } );
        return *receipt->manufacturingExport;
    }

    for( const std::string& output : jobResult->output_path() )
    {
        const fs::path outputPath = fs::u8path( output );
        std::error_code outputError;
        const fs::path canonicalOutput = fs::weakly_canonical( outputPath, outputError );
        const fs::path canonicalStage = fs::weakly_canonical( stagingDirectory, outputError );
        const fs::path relative = canonicalOutput.lexically_relative( canonicalStage );
        if( outputError || relative.empty() || relative.is_absolute()
            || ( relative.generic_string().size() >= 2
                 && relative.generic_string().compare( 0, 2, ".." ) == 0 ) )
        {
            setReceipt( [&]( PcbManufacturingExportReceipt& value )
            {
                value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
                value.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
                value.set_terminal( true );
                value.set_error_code( "native_export_output_escape" );
                value.set_error_message( "The native exporter reported an output outside its owned staging directory" );
            } );
            return *receipt->manufacturingExport;
        }
    }

    HANDLER_RESULT<DocumentLifecycleState> after = handleReadLifecycleState( lifecycleContext );
    if( !after || !google::protobuf::util::MessageDifferencer::Equivalent( *after, request.expected_state() ) )
    {
        setReceipt( [&]( PcbManufacturingExportReceipt& value )
        {
            value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            value.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
            value.set_terminal( true );
            value.set_error_code( "pcb_export_state_changed" );
            value.set_error_message( "The PCB changed while exporting; the staged result was retained and not published" );
        } );
        return *receipt->manufacturingExport;
    }

    std::string manifestReason;
    setReceipt( [&]( PcbManufacturingExportReceipt& value )
    {
        value.mutable_observed_after()->CopyFrom( *after );
        InventoryManufacturingArtifacts( stagingDirectory, value, manifestReason );
    } );
    if( !manifestReason.empty() )
    {
        setReceipt( [&]( PcbManufacturingExportReceipt& value )
        {
            value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            value.set_dispatch_stage( PCB_MFG_DISPATCH_PUBLICATION_FAILED );
            value.set_terminal( true );
            value.set_error_code( "export_manifest_failed" );
            value.set_error_message( manifestReason );
            value.set_publication_state( PCB_MFG_PUBLICATION_FAILED );
        } );
        return *receipt->manufacturingExport;
    }

    PcbManufacturingExportReceipt finalReceipt;
    {
        std::lock_guard lock( receipt->mutex );
        finalReceipt.CopyFrom( retained );
        for( int index = 0; index < finalReceipt.artifacts_size(); ++index )
        {
            PcbManufacturingArtifact* artifact = finalReceipt.mutable_artifacts( index );
            artifact->set_path( ( outputDirectory / fs::u8path( artifact->relative_path() ) ).generic_string() );
        }
        finalReceipt.set_status( PCB_MFG_EXPORT_COMPLETED );
        finalReceipt.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
        finalReceipt.set_terminal( true );
        finalReceipt.set_publication_state( PCB_MFG_PUBLICATION_PUBLISHED );
    }

    std::string publicationReason;
    if( !AtomicPublishNoReplaceDirectory( stagingDirectory, outputDirectory, publicationReason ) )
    {
        setReceipt( [&]( PcbManufacturingExportReceipt& value )
        {
            value.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            value.set_dispatch_stage( PCB_MFG_DISPATCH_PUBLICATION_FAILED );
            value.set_terminal( true );
            value.set_error_code( "export_publication_failed" );
            value.set_error_message( publicationReason );
            value.set_publication_state( PCB_MFG_PUBLICATION_FAILED );
        } );
        return *receipt->manufacturingExport;
    }

    {
        std::lock_guard lock( receipt->mutex );
        retained.Swap( &finalReceipt );
    }
    return *receipt->manufacturingExport;
}


HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceiptResponse>
API_HANDLER_PCB::handleReadPcbManufacturingExport(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbManufacturingExport>& aCtx )
{
    using namespace kiapi::automation::v1;
    PcbManufacturingExportReceiptResponse response;
    if( !IsOperationId( aCtx.Request.operation_id() )
        || aCtx.Request.document().type() != DocumentType::DOCTYPE_PCB
        || aCtx.Request.process_epoch() != apiServer().Token() )
    {
        return tl::unexpected( ManufacturingError( "The manufacturing receipt read requires the exact PCB and process epoch" ) );
    }
    auto found = m_operationReceipts.find( aCtx.Request.operation_id() );
    if( found == m_operationReceipts.end() )
    {
        response.set_found( false );
        return response;
    }
    std::lock_guard lock( found->second->mutex );
    if( !found->second->manufacturingExport )
        return tl::unexpected( ManufacturingError( "The operation ID belongs to a different PCB operation" ) );
    if( !google::protobuf::util::MessageDifferencer::Equivalent(
            found->second->document, aCtx.Request.document() ) )
        return tl::unexpected( ManufacturingError( "The manufacturing receipt belongs to a different PCB document" ) );
    if( found->second->processEpoch != aCtx.Request.process_epoch() )
        return tl::unexpected( ManufacturingError( "The manufacturing receipt belongs to a different native process epoch",
                                                   ApiStatusCode::AS_BUSY ) );
    response.set_found( true );
    response.mutable_receipt()->CopyFrom( *found->second->manufacturingExport );
    return response;
}


HANDLER_RESULT<BoardEditorAppearanceSettings> API_HANDLER_PCB::handleGetBoardEditorAppearanceSettings(
        const HANDLER_CONTEXT<GetBoardEditorAppearanceSettings>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "GetBoardEditorAppearanceSettings" ) )
        return tl::unexpected( *headless );

    BoardEditorAppearanceSettings reply;

    // TODO: might be nice to put all these things in one place and have it derive SERIALIZABLE

    const PCB_DISPLAY_OPTIONS& displayOptions = frame()->GetDisplayOptions();

    reply.set_inactive_layer_display( ToProtoEnum<HIGH_CONTRAST_MODE, InactiveLayerDisplayMode>(
            displayOptions.m_ContrastModeDisplay ) );
    reply.set_net_color_display(
            ToProtoEnum<NET_COLOR_MODE, NetColorDisplayMode>( displayOptions.m_NetColorMode ) );

    reply.set_board_flip( frame()->GetCanvas()->GetView()->IsMirroredX()
                                  ? BoardFlipMode::BFM_FLIPPED_X
                                  : BoardFlipMode::BFM_NORMAL );

    PCBNEW_SETTINGS* editorSettings = frame()->GetPcbNewSettings();

    reply.set_ratsnest_display( ToProtoEnum<RATSNEST_MODE, RatsnestDisplayMode>(
            editorSettings->m_Display.m_RatsnestMode ) );

    return reply;
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSetBoardEditorAppearanceSettings(
        const HANDLER_CONTEXT<SetBoardEditorAppearanceSettings>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "SetBoardEditorAppearanceSettings" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    PCB_DISPLAY_OPTIONS options = frame()->GetDisplayOptions();
    PCBNEW_SETTINGS* editorSettings = frame()->GetPcbNewSettings();
    const BoardEditorAppearanceSettings& newSettings = aCtx.Request.settings();

    options.m_ContrastModeDisplay =
            FromProtoEnum<HIGH_CONTRAST_MODE>( newSettings.inactive_layer_display() );
    options.m_NetColorMode =
            FromProtoEnum<NET_COLOR_MODE>( newSettings.net_color_display() );
    options.m_FlipBoardView = newSettings.board_flip() == BoardFlipMode::BFM_FLIPPED_X;

    editorSettings->m_Display.m_RatsnestMode =
            FromProtoEnum<RATSNEST_MODE>( newSettings.ratsnest_display() );

    frame()->SetDisplayOptions( options );
    frame()->GetCanvas()->GetView()->UpdateAllLayersColor();
    frame()->GetCanvas()->Refresh();

    return Empty();
}


HANDLER_RESULT<BoardPlotSettingsResponse>
API_HANDLER_PCB::handleGetBoardPlotSettings( const HANDLER_CONTEXT<GetBoardPlotSettings>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    const PCB_PLOT_PARAMS& plotOpts = board()->GetPlotOptions();

    BoardPlotSettingsResponse response;
    BoardPlotSettings*        settings = response.mutable_plot_settings();

    board::PackLayerSet( *settings->mutable_layers(), plotOpts.GetLayerSelection() );

    for( PCB_LAYER_ID layer : plotOpts.GetPlotOnAllLayersSequence() )
        settings->add_common_layers( ToProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>( layer ) );

    settings->set_mirror( plotOpts.GetMirror() );
    settings->set_black_and_white( plotOpts.GetBlackAndWhite() );
    settings->set_negative( plotOpts.GetNegative() );
    settings->set_scale( plotOpts.GetScale() );

    settings->set_sketch_pads_on_fab_layers( plotOpts.GetSketchPadsOnFabLayers() );
    settings->set_hide_dnp_footprints_on_fab_layers( plotOpts.GetHideDNPFPsOnFabLayers() );
    settings->set_sketch_dnp_footprints_on_fab_layers( plotOpts.GetSketchDNPFPsOnFabLayers() );
    settings->set_crossout_dnp_footprints_on_fab_layers( plotOpts.GetCrossoutDNPFPsOnFabLayers() );

    settings->set_plot_footprint_values( plotOpts.GetPlotValue() );
    settings->set_plot_reference_designators( plotOpts.GetPlotReference() );
    settings->set_plot_drawing_sheet( plotOpts.GetPlotFrameRef() );
    settings->set_subtract_solder_mask_from_silk( plotOpts.GetSubtractMaskFromSilk() );
    settings->set_plot_pad_numbers( plotOpts.GetPlotPadNumbers() );

    settings->set_drill_marks( ToProtoEnum<DRILL_MARKS, PlotDrillMarks>( plotOpts.GetDrillMarksType() ) );
    settings->set_use_drill_origin( plotOpts.GetUseAuxOrigin() );

    return response;
}


HANDLER_RESULT<Empty> API_HANDLER_PCB::handleSetBoardPlotSettings( const HANDLER_CONTEXT<SetBoardPlotSettings>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    const BoardPlotSettings& settings = aCtx.Request.plot_settings();
    PCB_PLOT_PARAMS          plotOpts = board()->GetPlotOptions();

    plotOpts.SetLayerSelection( board::UnpackLayerSet( settings.layers() ) );

    LSEQ commonLayers;

    for( int layer : settings.common_layers() )
    {
        PCB_LAYER_ID layerId =
                FromProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>( static_cast<board::types::BoardLayer>( layer ) );

        if( !IsPcbLayer( layerId ) )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( fmt::format( "SetBoardPlotSettings contains an invalid layer {}",
                                              magic_enum::enum_name( layerId ) ) );
            return tl::unexpected( e );
        }

        commonLayers.push_back( layerId );
    }

    plotOpts.SetPlotOnAllLayersSequence( commonLayers );

    plotOpts.SetMirror( settings.mirror() );
    plotOpts.SetBlackAndWhite( settings.black_and_white() );
    plotOpts.SetNegative( settings.negative() );
    plotOpts.SetScale( settings.scale() );

    plotOpts.SetSketchPadsOnFabLayers( settings.sketch_pads_on_fab_layers() );
    plotOpts.SetHideDNPFPsOnFabLayers( settings.hide_dnp_footprints_on_fab_layers() );
    plotOpts.SetSketchDNPFPsOnFabLayers( settings.sketch_dnp_footprints_on_fab_layers() );
    plotOpts.SetCrossoutDNPFPsOnFabLayers( settings.crossout_dnp_footprints_on_fab_layers() );

    plotOpts.SetPlotValue( settings.plot_footprint_values() );
    plotOpts.SetPlotReference( settings.plot_reference_designators() );
    plotOpts.SetPlotFrameRef( settings.plot_drawing_sheet() );
    plotOpts.SetSubtractMaskFromSilk( settings.subtract_solder_mask_from_silk() );
    plotOpts.SetPlotPadNumbers( settings.plot_pad_numbers() );

    plotOpts.SetDrillMarksType( FromProtoEnum<DRILL_MARKS>( settings.drill_marks() ) );
    plotOpts.SetUseAuxOrigin( settings.use_drill_origin() );

    board()->SetPlotOptions( plotOpts );

    if( frame() )
        frame()->OnModify();
    else
        onModified();

    return Empty();
}


HANDLER_RESULT<InjectDrcErrorResponse> API_HANDLER_PCB::handleInjectDrcError(
        const HANDLER_CONTEXT<InjectDrcError>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.board() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    SEVERITY severity = FromProtoEnum<SEVERITY>( aCtx.Request.severity() );
    int      layer = severity == RPT_SEVERITY_WARNING ? LAYER_DRC_WARNING : LAYER_DRC_ERROR;
    int      code = severity == RPT_SEVERITY_WARNING ? DRCE_GENERIC_WARNING : DRCE_GENERIC_ERROR;

    std::shared_ptr<DRC_ITEM> drcItem = DRC_ITEM::Create( code );

    drcItem->SetErrorMessage( wxString::FromUTF8( aCtx.Request.message() ) );

    RC_ITEM::KIIDS ids;

    for( const auto& id : aCtx.Request.items() )
        ids.emplace_back( KIID( id.value() ) );

    if( !ids.empty() )
        drcItem->SetItems( ids );

    const auto& pos = aCtx.Request.position();
    VECTOR2I    position( static_cast<int>( pos.x_nm() ), static_cast<int>( pos.y_nm() ) );

    PCB_MARKER* marker = new PCB_MARKER( drcItem, position, layer );

    COMMIT* commit = getCurrentCommit( aCtx.ClientName );
    commit->Add( marker );
    commit->Push( wxS( "API injected DRC marker" ) );
    if( !frame() )
        onModified();

    InjectDrcErrorResponse response;
    response.mutable_marker()->set_value( marker->GetUUID().AsStdString() );

    return response;
}


std::optional<ApiResponseStatus> ValidateUnitsInchMm( types::Units aUnits,
                                                      const std::string& aCommandName )
{
    if( aUnits == types::Units::U_INCH || aUnits == types::Units::U_MM
        || aUnits == types::Units::U_UNKNOWN )
    {
        return std::nullopt;
    }

    ApiResponseStatus e;
    e.set_status( ApiStatusCode::AS_BAD_REQUEST );
    e.set_error_message( fmt::format( "{} supports only inch and mm units", aCommandName ) );
    return e;
}


std::optional<ApiResponseStatus>
ValidatePaginationModeForSingleOrPerFile( kiapi::board::jobs::BoardJobPaginationMode aMode,
                                          const std::string& aCommandName )
{
    if( aMode == kiapi::board::jobs::BoardJobPaginationMode::BJPM_UNKNOWN
        || aMode == kiapi::board::jobs::BoardJobPaginationMode::BJPM_ALL_LAYERS_ONE_PAGE
        || aMode == kiapi::board::jobs::BoardJobPaginationMode::BJPM_EACH_LAYER_OWN_FILE )
    {
        return std::nullopt;
    }

    ApiResponseStatus e;
    e.set_status( ApiStatusCode::AS_BAD_REQUEST );
    e.set_error_message( fmt::format( "{} does not support EACH_LAYER_OWN_PAGE pagination mode",
                                      aCommandName ) );
    return e;
}


std::optional<ApiResponseStatus> ApplyBoardPlotSettings( const BoardPlotSettings& aSettings,
                                                         JOB_EXPORT_PCB_PLOT& aJob )
{
    for( int layer : aSettings.layers() )
    {
        PCB_LAYER_ID layerId = FromProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>(
                static_cast<board::types::BoardLayer>( layer ) );

        if( layerId == PCB_LAYER_ID::UNDEFINED_LAYER )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "Board plot settings contain an invalid layer" );
            return e;
        }

        aJob.m_plotLayerSequence.push_back( layerId );
    }

    for( int layer : aSettings.common_layers() )
    {
        PCB_LAYER_ID layerId = FromProtoEnum<PCB_LAYER_ID, board::types::BoardLayer>(
                static_cast<board::types::BoardLayer>( layer ) );

        if( layerId == PCB_LAYER_ID::UNDEFINED_LAYER )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "Board plot settings contain an invalid common layer" );
            return e;
        }

        aJob.m_plotOnAllLayersSequence.push_back( layerId );
    }

    aJob.m_colorTheme = wxString::FromUTF8( aSettings.color_theme() );
    aJob.m_drawingSheet = wxString::FromUTF8( aSettings.drawing_sheet() );
    aJob.m_variant = wxString::FromUTF8( aSettings.variant() );

    aJob.m_mirror = aSettings.mirror();
    aJob.m_blackAndWhite = aSettings.black_and_white();
    aJob.m_negative = aSettings.negative();
    aJob.m_scale = aSettings.scale();

    aJob.m_sketchPadsOnFabLayers = aSettings.sketch_pads_on_fab_layers();
    aJob.m_hideDNPFPsOnFabLayers = aSettings.hide_dnp_footprints_on_fab_layers();
    aJob.m_sketchDNPFPsOnFabLayers = aSettings.sketch_dnp_footprints_on_fab_layers();
    aJob.m_crossoutDNPFPsOnFabLayers = aSettings.crossout_dnp_footprints_on_fab_layers();

    aJob.m_plotFootprintValues = aSettings.plot_footprint_values();
    aJob.m_plotRefDes = aSettings.plot_reference_designators();
    aJob.m_plotDrawingSheet = aSettings.plot_drawing_sheet();
    aJob.m_subtractSolderMaskFromSilk = aSettings.subtract_solder_mask_from_silk();
    aJob.m_plotPadNumbers = aSettings.plot_pad_numbers();

    aJob.m_drillShapeOption = FromProtoEnum<DRILL_MARKS>( aSettings.drill_marks() );

    aJob.m_useDrillOrigin = aSettings.use_drill_origin();
    aJob.m_checkZonesBeforePlot = aSettings.check_zones_before_plot();

    return std::nullopt;
}


HANDLER_RESULT<types::RunJobResponse> ExecuteBoardJob( PCB_CONTEXT* aContext, JOB& aJob )
{
    types::RunJobResponse response;
    WX_STRING_REPORTER reporter;

    if( !aContext || !aContext->GetKiway() )
    {
        response.set_status( types::JobStatus::JS_ERROR );
        response.set_message( "Internal error" );
        wxCHECK_MSG( false, response, "context missing valid kiway in ExecuteBoardJob?" );
        return response;
    }

    int exitCode = aContext->GetKiway()->ProcessJob( KIWAY::FACE_PCB, &aJob, &reporter );

    for( const JOB_OUTPUT& output : aJob.GetOutputs() )
        response.add_output_path( output.m_outputPath.ToUTF8() );

    if( exitCode == 0 )
    {
        response.set_status( types::JobStatus::JS_SUCCESS );
        return response;
    }

    response.set_status( types::JobStatus::JS_ERROR );
    response.set_message( fmt::format( "Board export job '{}' failed with exit code {}: {}",
                                       aJob.GetType(), exitCode,
                                       reporter.GetMessages().ToStdString() ) );
    return response;
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExport3D(
        const HANDLER_CONTEXT<RunBoardJobExport3D>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_3D job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_format = FromProtoEnum<JOB_EXPORT_PCB_3D::FORMAT>( aCtx.Request.format() );

    job.m_variant = wxString::FromUTF8( aCtx.Request.variant() );
    job.m_3dparams.m_NetFilter = wxString::FromUTF8( aCtx.Request.net_filter() );
    job.m_3dparams.m_ComponentFilter = wxString::FromUTF8( aCtx.Request.component_filter() );

    job.m_hasUserOrigin = aCtx.Request.has_user_origin();
    job.m_3dparams.m_Origin = VECTOR2D( aCtx.Request.origin().x_nm(), aCtx.Request.origin().y_nm() );

    job.m_3dparams.m_Overwrite = aCtx.Request.overwrite();
    job.m_3dparams.m_UseGridOrigin = aCtx.Request.use_grid_origin();
    job.m_3dparams.m_UseDrillOrigin = aCtx.Request.use_drill_origin();
    job.m_3dparams.m_UseDefinedOrigin = aCtx.Request.use_defined_origin() || aCtx.Request.has_user_origin();
    job.m_3dparams.m_UsePcbCenterOrigin = aCtx.Request.use_pcb_center_origin();

    job.m_3dparams.m_IncludeUnspecified = aCtx.Request.include_unspecified();
    job.m_3dparams.m_IncludeDNP = aCtx.Request.include_dnp();
    job.m_3dparams.m_SubstModels = aCtx.Request.substitute_models();

    job.m_3dparams.m_BoardOutlinesChainingEpsilon = aCtx.Request.board_outlines_chaining_epsilon();
    job.m_3dparams.m_BoardOnly = aCtx.Request.board_only();
    job.m_3dparams.m_CutViasInBody = aCtx.Request.cut_vias_in_body();
    job.m_3dparams.m_ExportBoardBody = aCtx.Request.export_board_body();
    job.m_3dparams.m_ExportComponents = aCtx.Request.export_components();
    job.m_3dparams.m_ExportTracksVias = aCtx.Request.export_tracks_and_vias();
    job.m_3dparams.m_ExportPads = aCtx.Request.export_pads();
    job.m_3dparams.m_ExportZones = aCtx.Request.export_zones();
    job.m_3dparams.m_ExportInnerCopper = aCtx.Request.export_inner_copper();
    job.m_3dparams.m_ExportSilkscreen = aCtx.Request.export_silkscreen();
    job.m_3dparams.m_ExportSoldermask = aCtx.Request.export_soldermask();
    job.m_3dparams.m_FuseShapes = aCtx.Request.fuse_shapes();
    job.m_3dparams.m_FillAllVias = aCtx.Request.fill_all_vias();
    job.m_3dparams.m_OptimizeStep = aCtx.Request.optimize_step();
    job.m_3dparams.m_ExtraPadThickness = aCtx.Request.extra_pad_thickness();

    job.m_vrmlUnits = FromProtoEnum<JOB_EXPORT_PCB_3D::VRML_UNITS>( aCtx.Request.vrml_units() );

    job.m_vrmlModelDir = wxString::FromUTF8( aCtx.Request.vrml_model_dir() );
    job.m_vrmlRelativePaths = aCtx.Request.vrml_relative_paths();

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportRender(
        const HANDLER_CONTEXT<RunBoardJobExportRender>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_PCB_RENDER job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_format = FromProtoEnum<JOB_PCB_RENDER::FORMAT>( aCtx.Request.format() );
    job.m_quality = FromProtoEnum<JOB_PCB_RENDER::QUALITY>( aCtx.Request.quality() );
    job.m_bgStyle = FromProtoEnum<JOB_PCB_RENDER::BG_STYLE>( aCtx.Request.background_style() );

    job.m_width = aCtx.Request.width();
    job.m_height = aCtx.Request.height();
    job.m_appearancePreset = aCtx.Request.appearance_preset();
    job.m_useBoardStackupColors = aCtx.Request.use_board_stackup_colors();

    job.m_side = FromProtoEnum<JOB_PCB_RENDER::SIDE>( aCtx.Request.side() );

    job.m_zoom = aCtx.Request.zoom();
    job.m_perspective = aCtx.Request.perspective();

    job.m_rotation = UnpackVector3D( aCtx.Request.rotation() );
    job.m_pan = UnpackVector3D( aCtx.Request.pan() );
    job.m_pivot = UnpackVector3D( aCtx.Request.pivot() );

    job.m_proceduralTextures = aCtx.Request.procedural_textures();
    job.m_floor = aCtx.Request.floor();
    job.m_antiAlias = aCtx.Request.anti_alias();
    job.m_postProcess = aCtx.Request.post_process();

    job.m_lightTopIntensity = UnpackVector3D( aCtx.Request.light_top_intensity() );
    job.m_lightBottomIntensity = UnpackVector3D( aCtx.Request.light_bottom_intensity() );
    job.m_lightCameraIntensity = UnpackVector3D( aCtx.Request.light_camera_intensity() );
    job.m_lightSideIntensity = UnpackVector3D( aCtx.Request.light_side_intensity() );
    job.m_lightSideElevation = aCtx.Request.light_side_elevation();

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<kiapi::automation::v1::PcbRenderInspectionResponse> API_HANDLER_PCB::handlePcbRenderInspection(
        const HANDLER_CONTEXT<kiapi::automation::v1::PcbRenderInspectionRequest>& aCtx )
{
    using namespace kiapi::automation::v1;

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    const RunBoardJobExportRender& request = aCtx.Request.render_request();
    const DocumentLifecycleState& expected = aCtx.Request.expected_state();

    if( !request.has_job_settings() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "PCB render inspection requires render job settings" );
        return tl::unexpected( error );
    }

    HANDLER_RESULT<bool> documentValidation = validateDocument( request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    if( expected.scope() != DocumentLifecycleScope::DLS_PCB
        || !expected.document().Equals( request.job_settings().document() )
        || expected.process_epoch() != apiServer().Token()
        || expected.state_sha256().empty() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BUSY );
        error.set_error_message( "The render inspection lifecycle binding is stale" );
        return tl::unexpected( error );
    }

    HANDLER_CONTEXT<ReadDocumentLifecycleState> lifecycleContext;
    lifecycleContext.Request.mutable_document()->CopyFrom( request.job_settings().document() );
    HANDLER_RESULT<DocumentLifecycleState> before = handleReadLifecycleState( lifecycleContext );

    if( !before )
        return tl::unexpected( before.error() );

    if( !google::protobuf::util::MessageDifferencer::Equivalent( *before, expected ) )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BUSY );
        error.set_error_message( "The PCB changed before the render inspection started" );
        return tl::unexpected( error );
    }

    // Capture the exact unsaved board into an owned detached document before
    // handing it to the job dispatcher. The renderer constructs adapters and
    // may normalize board-derived state; it must never borrow or mutate the
    // human editor's BOARD while the inspection is running.
    std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> inspectionSnapshot;

    try
    {
        inspectionSnapshot = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( *board() );
    }
    catch( const std::exception& error )
    {
        ApiResponseStatus failure;
        failure.set_status( ApiStatusCode::AS_BUSY );
        failure.set_error_message( "The PCB could not be captured for an isolated render inspection: "
                                   + PcbDrcExceptionMessage( error ) );
        return tl::unexpected( failure );
    }

    JOB_PCB_RENDER job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.m_boardSnapshot = &inspectionSnapshot->GetBoard();
    job.m_hasInspectionMetadata = true;
    job.m_sourceBoardSnapshotSha256 = expected.state_sha256();
    job.SetConfiguredOutputPath( wxString::FromUTF8( request.job_settings().output_path() ) );
    job.m_format = FromProtoEnum<JOB_PCB_RENDER::FORMAT>( request.format() );
    job.m_quality = FromProtoEnum<JOB_PCB_RENDER::QUALITY>( request.quality() );
    job.m_bgStyle = FromProtoEnum<JOB_PCB_RENDER::BG_STYLE>( request.background_style() );
    job.m_width = request.width();
    job.m_height = request.height();
    job.m_appearancePreset = request.appearance_preset();
    job.m_useBoardStackupColors = request.use_board_stackup_colors();
    job.m_side = FromProtoEnum<JOB_PCB_RENDER::SIDE>( request.side() );
    job.m_zoom = request.zoom();
    job.m_perspective = request.perspective();
    job.m_rotation = UnpackVector3D( request.rotation() );
    job.m_pan = UnpackVector3D( request.pan() );
    job.m_pivot = UnpackVector3D( request.pivot() );
    job.m_proceduralTextures = request.procedural_textures();
    job.m_floor = request.floor();
    job.m_antiAlias = request.anti_alias();
    job.m_postProcess = request.post_process();
    job.m_lightTopIntensity = UnpackVector3D( request.light_top_intensity() );
    job.m_lightBottomIntensity = UnpackVector3D( request.light_bottom_intensity() );
    job.m_lightCameraIntensity = UnpackVector3D( request.light_camera_intensity() );
    job.m_lightSideIntensity = UnpackVector3D( request.light_side_intensity() );
    job.m_lightSideElevation = request.light_side_elevation();

    RunBoardJobExportRender normalizedRequest = request;
    normalizedRequest.mutable_job_settings()->clear_output_path();
    const std::string normalizedBytes = normalizedRequest.SerializeAsString();
    NATIVE_STATE_DIGEST requestDigest;
    requestDigest.Append( normalizedBytes );
    job.m_effectiveRenderInputSha256 = requestDigest.Hex();

    HANDLER_RESULT<types::RunJobResponse> jobResult = ExecuteBoardJob( pcbContext(), job );

    if( !jobResult )
        return tl::unexpected( jobResult.error() );

    HANDLER_RESULT<DocumentLifecycleState> after = handleReadLifecycleState( lifecycleContext );

    if( !after )
        return tl::unexpected( after.error() );

    if( !google::protobuf::util::MessageDifferencer::Equivalent( *after, expected ) )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BUSY );
        error.set_error_message( "The PCB changed while rendering; the image and metadata were discarded" );
        return tl::unexpected( error );
    }

    PcbRenderInspectionResponse response;
    response.mutable_job()->CopyFrom( *jobResult );
    response.mutable_document()->CopyFrom( *after );
    response.set_process_epoch( after->process_epoch() );
    PcbRenderMetadata& metadata = *response.mutable_metadata();
    metadata.set_width_pixels( job.m_inspectionMetadata.widthPixels );
    metadata.set_height_pixels( job.m_inspectionMetadata.heightPixels );
    for( double value : job.m_inspectionMetadata.viewMatrix ) metadata.add_view_matrix_column_major( value );
    for( double value : job.m_inspectionMetadata.projectionMatrix ) metadata.add_projection_matrix_column_major( value );
    for( double value : job.m_inspectionMetadata.boardNanometresToScene ) metadata.add_board_nanometres_to_scene_column_major( value );
    metadata.set_projection_convention( job.m_inspectionMetadata.projectionConvention );
    metadata.set_near_clip( job.m_inspectionMetadata.nearClip );
    metadata.set_far_clip( job.m_inspectionMetadata.farClip );
    metadata.set_observed_side( job.m_inspectionMetadata.observedSide );
    metadata.set_observed_perspective( job.m_inspectionMetadata.observedPerspective );
    metadata.set_observed_zoom( job.m_inspectionMetadata.observedZoom );
    for( double value : job.m_inspectionMetadata.observedCameraPosition ) metadata.add_observed_camera_position( value );
    for( double value : job.m_inspectionMetadata.observedLookAt ) metadata.add_observed_look_at( value );
    metadata.set_source_board_snapshot_sha256( job.m_inspectionMetadata.sourceBoardSnapshotSha256 );
    metadata.set_effective_render_input_sha256( job.m_inspectionMetadata.effectiveRenderInputSha256 );
    metadata.set_matrices_finite( job.m_inspectionMetadata.matricesFinite );
    metadata.set_matrices_invertible( job.m_inspectionMetadata.matricesInvertible );

    bool complete = job.m_inspectionMetadata.matricesFinite && job.m_inspectionMetadata.matricesInvertible;

    for( const JOB_PCB_RENDER::OBJECT_METADATA& object : job.m_inspectionMetadata.objects )
    {
        PcbRenderObjectMetadata& output = *metadata.add_objects();
        output.set_native_uuid( object.nativeUuid );
        output.set_model_index( object.modelIndex );
        for( double value : object.sceneTransform ) output.add_scene_transform_column_major( value );
        for( double value : object.nativeGeometryBounds ) output.add_native_geometry_bounds( value );
        for( double value : object.projectedConservativeBounds ) output.add_projected_conservative_bounds( value );
        output.set_projected_bounds_available( object.projectedBoundsAvailable );
        output.set_exact_visible_pixels( object.exactVisiblePixels );
        output.set_occlusion_tested( object.occlusionTested );
        output.set_native_geometry_bounds_available( object.nativeGeometryBoundsAvailable );
    }

    for( const JOB_PCB_RENDER::MODEL_METADATA& model : job.m_inspectionMetadata.models )
    {
        PcbRenderModelMetadata& output = *metadata.add_models();
        output.set_native_uuid( model.nativeUuid );
        output.set_model_index( model.modelIndex );
        output.set_requested_identity( model.requestedIdentity );
        output.set_resolved_identity( model.resolvedIdentity );
        output.set_content_sha256( model.contentSha256 );
        output.set_loaded( model.loaded );
        output.set_missing( model.missing );
        output.set_missing_reason( model.missingReason );
        for( double value : model.sceneTransform ) output.add_scene_transform_column_major( value );
        for( double value : model.nativeGeometryBounds ) output.add_native_geometry_bounds( value );
        output.set_native_geometry_bounds_available( model.nativeGeometryBoundsAvailable );
        output.set_secondary_assets_complete( model.secondaryAssetsComplete );
        output.set_secondary_asset_limitation( model.secondaryAssetLimitation );
        complete = complete && model.loaded && !model.missing && !model.contentSha256.empty()
                && model.nativeGeometryBoundsAvailable && model.secondaryAssetsComplete;
    }

    for( const std::string& note : job.m_inspectionMetadata.coverageNotes )
        metadata.add_coverage_notes( note );

    for( const std::string& limitation : job.m_inspectionMetadata.limitations )
        response.add_limitations( limitation );

    // The response deliberately keeps the partial-coverage signal visible:
    // projected bounds and transforms are useful, but exact visible pixels or
    // occlusion depth are not claimed by this renderer hook.
    response.set_metadata_complete( complete );
    return response;
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportSvg(
        const HANDLER_CONTEXT<RunBoardJobExportSvg>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_SVG job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    if( std::optional<ApiResponseStatus> err = ApplyBoardPlotSettings( aCtx.Request.plot_settings(), job ) )
        return tl::unexpected( *err );

    job.m_fitPageToBoard = aCtx.Request.fit_page_to_board();
    job.m_precision = aCtx.Request.precision();

        if( std::optional<ApiResponseStatus> paginationError =
            ValidatePaginationModeForSingleOrPerFile( aCtx.Request.page_mode(),
                                                      "RunBoardJobExportSvg" ) )
    {
        return tl::unexpected( *paginationError );
    }

    job.m_genMode = FromProtoEnum<JOB_EXPORT_PCB_SVG::GEN_MODE>( aCtx.Request.page_mode() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportDxf(
        const HANDLER_CONTEXT<RunBoardJobExportDxf>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_DXF job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    if( std::optional<ApiResponseStatus> err = ApplyBoardPlotSettings( aCtx.Request.plot_settings(), job ) )
        return tl::unexpected( *err );

    job.m_plotGraphicItemsUsingContours = aCtx.Request.plot_graphic_items_using_contours();
    job.m_polygonMode = aCtx.Request.polygon_mode();

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportDxf" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_dxfUnits = FromProtoEnum<JOB_EXPORT_PCB_DXF::DXF_UNITS>( aCtx.Request.units() );

        if( std::optional<ApiResponseStatus> paginationError =
            ValidatePaginationModeForSingleOrPerFile( aCtx.Request.page_mode(),
                                                      "RunBoardJobExportDxf" ) )
    {
        return tl::unexpected( *paginationError );
    }

    job.m_genMode = FromProtoEnum<JOB_EXPORT_PCB_DXF::GEN_MODE>( aCtx.Request.page_mode() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportPdf(
        const HANDLER_CONTEXT<RunBoardJobExportPdf>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_PDF job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    if( std::optional<ApiResponseStatus> err = ApplyBoardPlotSettings( aCtx.Request.plot_settings(), job ) )
        return tl::unexpected( *err );

    job.m_pdfFrontFPPropertyPopups = aCtx.Request.front_footprint_property_popups();
    job.m_pdfBackFPPropertyPopups = aCtx.Request.back_footprint_property_popups();
    job.m_pdfMetadata = aCtx.Request.include_metadata();
    job.m_pdfSingle = aCtx.Request.single_document();
    job.m_pdfBackgroundColor = wxString::FromUTF8( aCtx.Request.background_color() );

    job.m_pdfGenMode = FromProtoEnum<JOB_EXPORT_PCB_PDF::GEN_MODE>( aCtx.Request.page_mode() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportPs(
        const HANDLER_CONTEXT<RunBoardJobExportPs>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_PS job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    if( std::optional<ApiResponseStatus> err = ApplyBoardPlotSettings( aCtx.Request.plot_settings(), job ) )
        return tl::unexpected( *err );

    if( std::optional<ApiResponseStatus> paginationError =
            ValidatePaginationModeForSingleOrPerFile( aCtx.Request.page_mode(),
                                                      "RunBoardJobExportPs" ) )
    {
        return tl::unexpected( *paginationError );
    }

    job.m_genMode = FromProtoEnum<JOB_EXPORT_PCB_PS::GEN_MODE>( aCtx.Request.page_mode() );

    job.m_trackWidthCorrection = aCtx.Request.track_width_correction();
    job.m_XScaleAdjust = aCtx.Request.x_scale_adjust();
    job.m_YScaleAdjust = aCtx.Request.y_scale_adjust();
    job.m_forceA4 = aCtx.Request.force_a4();
    job.m_useGlobalSettings = aCtx.Request.use_global_settings();

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportGerbers(
        const HANDLER_CONTEXT<RunBoardJobExportGerbers>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_GERBERS job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_useBoardPlotParams = aCtx.Request.use_board_plot_params();

    if( !job.m_useBoardPlotParams )
    {
        if( std::optional<ApiResponseStatus> err = ApplyBoardPlotSettings( aCtx.Request.plot_settings(), job ) )
            return tl::unexpected( *err );
    }

    job.m_createJobsFile = aCtx.Request.create_gerber_job_file();
    job.m_includeNetlistAttributes = aCtx.Request.include_netlist_attributes();
    job.m_useX2Format = aCtx.Request.use_x2_format();
    job.m_disableApertureMacros = aCtx.Request.disable_aperture_macros();
    job.m_useProtelFileExtension = aCtx.Request.use_protel_file_extensions();

    switch( aCtx.Request.precision() )
    {
    default:
    case GerberPrecision::GP_5: job.m_precision = 5; break;
    case GerberPrecision::GP_6: job.m_precision = 6; break;
    }

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportDrill(
        const HANDLER_CONTEXT<RunBoardJobExportDrill>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_DRILL job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_format = FromProtoEnum<JOB_EXPORT_PCB_DRILL::DRILL_FORMAT>( aCtx.Request.format() );

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportDrill" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_drillUnits = FromProtoEnum<JOB_EXPORT_PCB_DRILL::DRILL_UNITS>( aCtx.Request.units() );
    job.m_drillOrigin = FromProtoEnum<JOB_EXPORT_PCB_DRILL::DRILL_ORIGIN>( aCtx.Request.origin() );
    job.m_zeroFormat = FromProtoEnum<JOB_EXPORT_PCB_DRILL::ZEROS_FORMAT>( aCtx.Request.zeros_format() );

    if( aCtx.Request.has_excellon() )
    {
        const ExcellonFormatOptions& excellonOptions = aCtx.Request.excellon();

        if( excellonOptions.has_mirror_y() )
            job.m_excellonMirrorY = excellonOptions.mirror_y();

        if( excellonOptions.has_minimal_header() )
            job.m_excellonMinimalHeader = excellonOptions.minimal_header();

        if( excellonOptions.has_combine_pth_npth() )
            job.m_excellonCombinePTHNPTH = excellonOptions.combine_pth_npth();

        if( excellonOptions.has_route_oval_holes() )
            job.m_excellonOvalDrillRoute = excellonOptions.route_oval_holes();
    }

    if( aCtx.Request.map_format() != DrillMapFormat::DMF_UNKNOWN )
    {
        job.m_generateMap = true;
        job.m_mapFormat = FromProtoEnum<JOB_EXPORT_PCB_DRILL::MAP_FORMAT>( aCtx.Request.map_format() );
    }

    job.m_gerberPrecision = aCtx.Request.gerber_precision() == DrillGerberPrecision::DGP_4_5 ? 5 : 6;

    if( aCtx.Request.has_gerber_generate_tenting() )
        job.m_generateTenting = aCtx.Request.gerber_generate_tenting();

    if( aCtx.Request.report_format() != DrillReportFormat::DRF_UNKNOWN )
    {
        job.m_generateReport = true;

        if( aCtx.Request.has_report_filename() )
            job.m_reportPath = wxString::FromUTF8( aCtx.Request.report_filename() );
    }

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportPosition(
        const HANDLER_CONTEXT<RunBoardJobExportPosition>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_POS job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    if( aCtx.Request.has_use_drill_place_file_origin() )
        job.m_useDrillPlaceFileOrigin = aCtx.Request.use_drill_place_file_origin();

    job.m_smdOnly = aCtx.Request.smd_only();
    job.m_excludeFootprintsWithTh = aCtx.Request.exclude_footprints_with_th();
    job.m_excludeDNP = aCtx.Request.exclude_dnp();
    job.m_excludeBOM = aCtx.Request.exclude_from_bom();
    job.m_negateBottomX = aCtx.Request.negate_bottom_x();
    job.m_singleFile = aCtx.Request.single_file();
    job.m_nakedFilename = aCtx.Request.naked_filename();
    if( aCtx.Request.has_include_board_edge_for_gerber() )
        job.m_gerberBoardEdge = aCtx.Request.include_board_edge_for_gerber();

    job.m_variant = wxString::FromUTF8( aCtx.Request.variant() );

    job.m_side = FromProtoEnum<JOB_EXPORT_PCB_POS::SIDE>( aCtx.Request.side() );

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportPosition" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_units = FromProtoEnum<JOB_EXPORT_PCB_POS::UNITS>( aCtx.Request.units() );
    job.m_format = FromProtoEnum<JOB_EXPORT_PCB_POS::FORMAT>( aCtx.Request.format() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportGencad(
        const HANDLER_CONTEXT<RunBoardJobExportGencad>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_GENCAD job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_flipBottomPads = aCtx.Request.flip_bottom_pads();
    job.m_useIndividualShapes = aCtx.Request.use_individual_shapes();
    job.m_storeOriginCoords = aCtx.Request.store_origin_coords();
    job.m_useDrillOrigin = aCtx.Request.use_drill_origin();
    job.m_useUniquePins = aCtx.Request.use_unique_pins();

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportIpc2581(
        const HANDLER_CONTEXT<RunBoardJobExportIpc2581>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_IPC2581 job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_drawingSheet = wxString::FromUTF8( aCtx.Request.drawing_sheet() );
    job.m_variant = wxString::FromUTF8( aCtx.Request.variant() );
    if( aCtx.Request.has_precision() )
        job.m_precision = aCtx.Request.precision();

    job.m_compress = aCtx.Request.compress();
    job.m_colInternalId = wxString::FromUTF8( aCtx.Request.internal_id_column() );
    job.m_colMfgPn = wxString::FromUTF8( aCtx.Request.manufacturer_part_number_column() );
    job.m_colMfg = wxString::FromUTF8( aCtx.Request.manufacturer_column() );
    job.m_colDistPn = wxString::FromUTF8( aCtx.Request.distributor_part_number_column() );
    job.m_colDist = wxString::FromUTF8( aCtx.Request.distributor_column() );
    job.m_bomRev = wxString::FromUTF8( aCtx.Request.bom_revision() );

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportIpc2581" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_units = FromProtoEnum<JOB_EXPORT_PCB_IPC2581::IPC2581_UNITS>( aCtx.Request.units() );
    job.m_version = FromProtoEnum<JOB_EXPORT_PCB_IPC2581::IPC2581_VERSION>( aCtx.Request.version() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportIpcD356(
        const HANDLER_CONTEXT<RunBoardJobExportIpcD356>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_IPCD356 job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportODB(
        const HANDLER_CONTEXT<RunBoardJobExportODB>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_ODB job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_drawingSheet = wxString::FromUTF8( aCtx.Request.drawing_sheet() );
    job.m_variant = wxString::FromUTF8( aCtx.Request.variant() );
    if( aCtx.Request.has_precision() )
        job.m_precision = aCtx.Request.precision();

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportODB" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_units = FromProtoEnum<JOB_EXPORT_PCB_ODB::ODB_UNITS>( aCtx.Request.units() );
    job.m_compressionMode = FromProtoEnum<JOB_EXPORT_PCB_ODB::ODB_COMPRESSION>( aCtx.Request.compression() );

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_PCB::handleRunBoardJobExportStats(
        const HANDLER_CONTEXT<RunBoardJobExportStats>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_PCB_STATS job;
    job.m_filename = pcbContext()->GetCurrentFileName();
    job.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    job.m_format = FromProtoEnum<JOB_EXPORT_PCB_STATS::OUTPUT_FORMAT>( aCtx.Request.format() );

    if( std::optional<ApiResponseStatus> unitError =
            ValidateUnitsInchMm( aCtx.Request.units(), "RunBoardJobExportStats" ) )
    {
        return tl::unexpected( *unitError );
    }

    job.m_units = FromProtoEnum<JOB_EXPORT_PCB_STATS::UNITS>( aCtx.Request.units() );

    job.m_excludeFootprintsWithoutPads = aCtx.Request.exclude_footprints_without_pads();
    job.m_subtractHolesFromBoardArea = aCtx.Request.subtract_holes_from_board_area();
    job.m_subtractHolesFromCopperAreas = aCtx.Request.subtract_holes_from_copper_areas();

    return ExecuteBoardJob( pcbContext(), job );
}


HANDLER_RESULT<CrossProbeAnnounceResponse> API_HANDLER_PCB::handleCrossProbeAnnounce(
        const HANDLER_CONTEXT<CrossProbeAnnounce>& aCtx )
{
    wxLogTrace( traceApi, "Received announce from frame %d at %s",
                aCtx.Request.frame_type(), aCtx.Request.socket_path() );

    CROSS_PROBE_CLIENT::RegisterPeer( static_cast<FRAME_T>( aCtx.Request.frame_type() ),
                                      aCtx.Request.socket_path() );

    CrossProbeAnnounceResponse response;
    response.set_status( CPS_OK );
    return response;
}


HANDLER_RESULT<SyncSelectionResponse> API_HANDLER_PCB::handleSyncSelection( const HANDLER_CONTEXT<SyncSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "SyncSelection" ) )
        return tl::unexpected( *headless );

    SyncSelectionResponse response;

    const CROSS_PROBING_SETTINGS& settings = frame()->GetPcbNewSettings()->m_CrossProbing;

    if( !settings.on_selection && aCtx.Request.context() != SyncSelectionContext::SSC_EXPLICIT )
    {
        response.set_status( CPS_DISABLED );
        response.set_message( "implicit selection sync disabled by user" );
        return response;
    }

    std::vector<BOARD_ITEM*> items =
            kiapi::board::FindItemsFromSyncSelection( board(), aCtx.Request.items() );

    frame()->m_ProbingSchToPcb = true; // recursion guard

    if( aCtx.Request.mode() == SyncSelectionMode::SSM_ITEMS_AND_NETS )
        frame()->GetToolManager()->RunAction( PCB_ACTIONS::syncSelectionWithNets, &items );
    else
        frame()->GetToolManager()->RunAction( PCB_ACTIONS::syncSelection, &items );

    // Update 3D viewer highlighting
    frame()->Update3DView( false, frame()->GetPcbNewSettings()->m_Display.m_Live3DRefresh );

    frame()->m_ProbingSchToPcb = false;

    if( settings.flash_selection )
    {
        wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE) PCB: flash enabled, items=%zu", items.size() );
        if( items.empty() )
        {
            wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE) PCB: nothing to flash" );
        }
        else
        {
            std::vector<BOARD_ITEM*> boardItems;
            std::copy( items.begin(), items.end(), std::back_inserter( boardItems ) );
            frame()->StartCrossProbeFlash( boardItems );
        }
    }
    else
    {
        wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE) PCB: flash disabled" );
    }

    response.set_status( CPS_OK );
    return response;
}


HANDLER_RESULT<HighlightNetsResponse> API_HANDLER_PCB::handleHighlightNets(
        const HANDLER_CONTEXT<HighlightNets>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "HighlightNets" ) )
        return tl::unexpected( *headless );

    HighlightNetsResponse response;
    CROSS_PROBING_SETTINGS& crossProbingSettings = frame()->GetPcbNewSettings()->m_CrossProbing;

    if( aCtx.ClientName == StandaloneCrossProbeClientName
        || aCtx.ClientName == KiwayClientName )
    {
        if( !crossProbingSettings.auto_highlight )
        {
            response.set_status( CPS_DISABLED );
            response.set_message( "net highlight cross-probing disabled by user" );
            return response;
        }
    }

    std::vector<wxString> nets;

    for( const std::string& name : aCtx.Request.net_name() )
        nets.emplace_back( wxString::FromUTF8( name ) );

    frame()->HandleRemoteNetHighlight( nets );

    response.set_status( CPS_OK );
    return response;
}
