/* Owned native DRC input bundle. GPL-3.0-or-later. */
#include "pcb_drc_run_inputs.h"
#include "pcb_drc_document_snapshot.h"
#include "pcb_drc_schematic_input.h"
#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <drc/drc_library_inputs.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_proxy_view_item.h>
#include <progress_reporter.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <pcb_project_editor_state.h>
#include <json_common.h>
#include <router/pns_routing_settings.h>
#include <stdexcept>

PCB_DRC_RUN_INPUTS::~PCB_DRC_RUN_INPUTS() = default;
BOARD& PCB_DRC_RUN_INPUTS::GetBoard() const { return m_document->GetBoard(); }

namespace
{
nlohmann::json ProjectInputs( const BOARD& aBoard )
{
    const PROJECT* project = aBoard.GetProject();
    const auto& settings = aBoard.GetDesignSettings();
    nlohmann::json result = {
        { "project_path", project ? project->GetProjectFullName().ToStdString() : "" },
        { "project", project ? project->GetProjectFile().CaptureCurrentState() : nlohmann::json() },
        { "board_settings", settings.CaptureCurrentState() },
        { "net_settings", settings.m_NetSettings
                ? settings.m_NetSettings->CaptureCurrentState() : nlohmann::json() }
    };
    // Exclusion comments can change on live markers before project settings save.
    result["effective_exclusions"] = nlohmann::json::array();
    for( const auto& exclusion : PCB_PROJECT_EDITOR_STATE::Exclusions( aBoard ) )
        result["effective_exclusions"].push_back( exclusion );
    return result;
}
}

bool PCB_DRC_PROJECT_BASELINE::Unchanged( const BOARD& aBoard ) const
{
    try
    {
        const wxString rulesPath = aBoard.GetDesignRulesPath();
        const bool rulesUnchanged = m_rules.Path().empty() ? rulesPath.empty()
                : m_rules.Check( rulesPath ) == FILE_BASELINE_CHECK::UNCHANGED;
        return rulesUnchanged && m_settings == ProjectInputs( aBoard );
    }
    catch( const std::exception& )
    {
        // Unreadable or unrepresentable inputs are not evidence of freshness.
        return false;
    }
}

PCB_DRC_AUXILIARY_BASELINE PCB_DRC_AUXILIARY_BASELINE::Capture(
        const PCB_DRC_CAPTURE_CONTEXT& aContext )
{
    wxString drawing;
    aContext.drawing.SaveInString( &drawing );
    PCB_DRC_AUXILIARY_BASELINE result;
    result.m_state = {
        { "drawing_identity", aContext.drawingIdentity.AsStdString() },
        { "drawing", drawing.utf8_string() },
        { "allow_empty_drawing", aContext.drawing.VoidListAllowed() },
        { "routing", aContext.routingSettings
                ? aContext.routingSettings->CaptureCurrentState() : nlohmann::json() }
    };
    return result;
}

bool PCB_DRC_AUXILIARY_BASELINE::Unchanged( const PCB_DRC_CAPTURE_CONTEXT& aContext ) const
{
    try { return m_state == Capture( aContext ).m_state; }
    catch( const std::exception& ) { return false; }
}

std::unique_ptr<PCB_DRC_RUN_INPUTS> PCB_DRC_RUN_INPUTS::Capture(
        BOARD& aBoard, const PCB_DRC_CAPTURE_CONTEXT& aContext, PROGRESS_REPORTER* aReporter )
{
    const int revision = aBoard.GetTimeStamp();
    const KIID identity = aBoard.m_Uuid;
    auto result = std::unique_ptr<PCB_DRC_RUN_INPUTS>( new PCB_DRC_RUN_INPUTS );
    result->m_projectBaseline.m_settings = ProjectInputs( aBoard );
    result->m_auxiliaryBaseline = PCB_DRC_AUXILIARY_BASELINE::Capture( aContext );
    if( aContext.routingSettings )
    {
        result->m_routingSettings = std::make_unique<PNS::ROUTING_SETTINGS>(
                nullptr, aContext.routingSettings->GetPath() );
        aContext.routingSettings->CopyCurrentStateTo( *result->m_routingSettings );
    }
    result->m_sourceDrawingIdentity = aContext.drawingIdentity;
    const wxString rulesPath = aBoard.GetDesignRulesPath();
    if( !rulesPath.empty() )
    {
        result->m_rulesBaseline = FILE_CONTENT_BASELINE::Read( rulesPath, &result->m_rulesText );
        if( !result->m_rulesBaseline.Known() )
            throw std::runtime_error( "Custom design rules could not be captured" );
    }
    result->m_projectBaseline.m_rules = result->m_rulesBaseline;
    result->m_document = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( aBoard );
    result->m_libraries = DRC_LIBRARY_INPUTS::Capture( aBoard, aContext.libraries, aReporter );
    if( !result->m_libraries ) return nullptr;
    result->m_drawing = aContext.drawing.CloneForRendering();
    BOARD& copy = result->GetBoard();
    result->m_proxy = std::make_unique<DS_PROXY_VIEW_ITEM>( pcbIUScale, &copy.GetPageSettings(),
            copy.GetProject(), &copy.GetTitleBlock(), &copy.GetProperties() );
    if( aReporter && aReporter->IsCancelled() ) return nullptr;
    if( aBoard.GetTimeStamp() != revision || aBoard.m_Uuid != identity
            || !result->m_auxiliaryBaseline.Unchanged( aContext )
            || !result->m_projectBaseline.Unchanged( aBoard ) )
        throw std::runtime_error( "Native DRC inputs changed during capture" );
    return result;
}

void PCB_DRC_RUN_INPUTS::InitializeEngine( DRC_ENGINE& aEngine ) const
{
    aEngine.InitEngineFromText( m_rulesText, m_rulesBaseline.Known()
            ? m_rulesBaseline.Path() : wxString( "captured implicit design rules" ) );
}

void PCB_DRC_RUN_INPUTS::BindInvocation( DRC_ENGINE& aEngine )
{
    if( !m_drawing ) throw std::logic_error( "Captured DRC input bundle already consumed" );
    aEngine.SetLibraryInputs( m_libraries );
    aEngine.SetDrawingSheet( m_proxy.get() );
    aEngine.SetDrawingSheetModel( std::move( m_drawing ) );
    if( m_schematic ) aEngine.SetSchematicNetlist( &m_schematic->Netlist() );
}

void PCB_DRC_RUN_INPUTS::SetSchematicInput( std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> aInput )
{
    if( !m_drawing || m_schematic || !aInput )
        throw std::logic_error( "Schematic input must be attached once before starting DRC" );
    m_schematic = std::move( aInput );
}

const KIID& PCB_DRC_RUN_INPUTS::CapturedDrawingIdentity() const { return m_proxy->m_Uuid; }

std::string PCB_DRC_RUN_INPUTS::LibraryFingerprint() const
{
    return m_libraries->ContentFingerprint();
}

bool PCB_DRC_RUN_INPUTS::HasLibraryDependencies() const
{
    return m_libraries->Size() != 0;
}

bool PCB_DRC_RUN_INPUTS::RulesUnchanged() const
{
    // An absent project/rule path is explicitly implicit-only, not a failed read.
    return m_rulesBaseline.Path().empty()
            || m_rulesBaseline.Check( m_rulesBaseline.Path() ) == FILE_BASELINE_CHECK::UNCHANGED;
}
