/* Detached document inputs for background DRC. GPL-3.0-or-later. */
#include "pcb_drc_document_snapshot.h"
#include <board.h>
#include <board_design_settings.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcb_project_editor_state.h>
#include <pcb_generator.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <richio.h>
#include <stdexcept>

PCB_DRC_DOCUMENT_SNAPSHOT::~PCB_DRC_DOCUMENT_SNAPSHOT() = default;

BOARD& PCB_DRC_DOCUMENT_SNAPSHOT::GetBoard() const { return *m_board; }

std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> PCB_DRC_DOCUMENT_SNAPSHOT::Capture( BOARD& aBoard )
{
    // The caller owns an uninterrupted native document checkpoint. Never yield
    // to editor input while serializing/copying this state.
    const KIID identity = aBoard.m_Uuid;
    const int sequence = aBoard.GetTimeStamp();
    if( sequence < 0 ) throw std::runtime_error( "Board revision requires a new document epoch" );
    auto result = std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT>( new PCB_DRC_DOCUMENT_SNAPSHOT );
    result->m_sourceSequence = sequence;
    if( PROJECT* project = aBoard.GetProject() )
    {
        if( aBoard.GetDesignSettings().m_NetSettings != project->GetProjectFile().NetSettings() )
            throw std::runtime_error( "Board and project net settings must have a settled owner before capture" );
        result->m_project = project->CloneForAnalysis();
    }

    PCB_IO_KICAD_SEXPR io;
    STRING_FORMATTER serialized;
    io.FormatBoardToFormatter( &serialized, &aBoard, nullptr, false );
    std::unique_ptr<BOARD_ITEM> parsed( io.Parse( wxString::FromUTF8( serialized.GetString() ) ) );
    auto* board = dynamic_cast<BOARD*>( parsed.get() );
    if( !board ) throw std::runtime_error( "Native document snapshot did not reconstruct a board" );
    result->m_board.reset( board );
    parsed.release();
    board->SetUuid( identity );
    board->SetFileName( aBoard.GetFileName() );
    if( result->m_project ) board->SetProject( result->m_project.get() );

    // Pending native generator work is not serialized into the board file.
    // Carry its exact identity-bound dirty state into the private refill input.
    for( const PCB_GENERATOR* source : aBoard.Generators() )
    {
        auto* copy = dynamic_cast<PCB_GENERATOR*>( board->ResolveItem( source->m_Uuid, true ) );
        if( !copy ) throw std::runtime_error( "Native snapshot lost a generator identity" );
        if( source->IsDirty() ) copy->MarkDirty();
        else copy->ClearDirty();
    }

    auto& sourceSettings = aBoard.GetDesignSettings();
    auto& copiedSettings = board->GetDesignSettings();
    sourceSettings.CopyCurrentStateTo( copiedSettings );
    if( !sourceSettings.m_NetSettings || !copiedSettings.m_NetSettings )
        throw std::runtime_error( "Native document snapshot is missing net settings" );
    sourceSettings.m_NetSettings->CopyCurrentStateTo( *copiedSettings.m_NetSettings );
    // User edits to marker exclusion comments need not have been saved to the
    // project's stored declarations yet. Capture their effective current value.
    copiedSettings.m_DrcExclusions = PCB_PROJECT_EDITOR_STATE::Exclusions( aBoard );

    if( identity != aBoard.m_Uuid || sequence != aBoard.GetTimeStamp() )
        throw std::runtime_error( "Board changed during native document capture" );
    return result;
}
