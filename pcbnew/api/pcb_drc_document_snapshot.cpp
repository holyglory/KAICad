/* Detached document inputs for background DRC. GPL-3.0-or-later. */
#include "pcb_drc_document_snapshot.h"
#include <board.h>
#include <board_design_settings.h>
#include <pad.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcb_project_editor_state.h>
#include <pcb_generator.h>
#include <pcb_track.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <richio.h>
#include <zone.h>
#include <stdexcept>

namespace
{
// Every object of the board by identity, except DRC markers, which are not part of
// the design. An identity that more than one object holds is recorded in aShared.
void CollectIdentities( const BOARD& aBoard, std::map<KIID, const BOARD_ITEM*>& aObjects,
                        std::set<KIID>& aShared )
{
    auto add = [&]( const BOARD_ITEM* aItem )
    {
        auto [found, inserted] = aObjects.emplace( aItem->m_Uuid, aItem );
        if( !inserted && found->second != aItem ) aShared.insert( aItem->m_Uuid );
    };
    add( &aBoard );
    for( BOARD_ITEM* item : aBoard.GetItemSet() )
    {
        if( item->Type() == PCB_MARKER_T ) continue;
        add( item );
        item->RunOnChildren( add, RECURSE_MODE::RECURSE );
    }
}

// What makes a teardrop the same teardrop: KiCad derives it from its track and pad or
// via, and writes it without an identity, so its kind, layer, net and outline name it.
std::string TeardropKey( const ZONE& aZone )
{
    return std::to_string( static_cast<int>( aZone.GetTeardropAreaType() ) ) + '|'
           + aZone.GetLayerSet().FmtHex() + '|' + aZone.GetNetname().utf8_string() + '|'
           + aZone.Outline()->GetHash().ToString();
}

// KiCad keeps the zone connection a fill decided for each pad and via layer only in
// memory. A check of the copy must use the same decisions as the open board.
template <typename ITEM>
void CopyZoneLayerOverrides( const ITEM& aSource, ITEM& aCopy )
{
    for( PCB_LAYER_ID layer : LSET::AllCuMask().Seq() )
    {
        const ZONE_LAYER_OVERRIDE value = aSource.GetZoneLayerOverride( layer );
        if( value != ZLO_NONE ) aCopy.SetZoneLayerOverride( layer, value );
    }
}
}

PCB_DRC_DOCUMENT_SNAPSHOT::~PCB_DRC_DOCUMENT_SNAPSHOT() = default;

BOARD& PCB_DRC_DOCUMENT_SNAPSHOT::GetBoard() const { return *m_board; }

std::optional<KIID> PCB_DRC_DOCUMENT_SNAPSHOT::SourceTeardrop( const ZONE& aTeardrop ) const
{
    if( !aTeardrop.IsTeardropArea() ) return std::nullopt;
    const auto found = m_teardrops.find( TeardropKey( aTeardrop ) );
    if( found == m_teardrops.end() ) return std::nullopt;
    return found->second;
}

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

    std::map<KIID, const BOARD_ITEM*> source;
    std::set<KIID> shared;
    CollectIdentities( aBoard, source, shared );

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
    for( const PCB_GENERATOR* generator : aBoard.Generators() )
    {
        auto* copy = dynamic_cast<PCB_GENERATOR*>( board->ResolveItem( generator->m_Uuid, true ) );
        if( !copy ) throw std::runtime_error( "Native snapshot lost a generator identity" );
        if( generator->IsDirty() ) copy->MarkDirty();
        else copy->ClearDirty();
    }

    // The board file omits teardrop identities; give each copied teardrop the identity
    // of the source teardrop it reproduces. One that matches none stays a counted gap.
    for( const ZONE* zone : aBoard.Zones() )
        if( zone->IsTeardropArea() ) result->m_teardrops.emplace( TeardropKey( *zone ), zone->m_Uuid );
    std::multimap<std::string, KIID> unpaired = result->m_teardrops;
    std::map<KIID, const BOARD_ITEM*> parsedObjects;
    std::set<KIID> parsedShared;
    CollectIdentities( *board, parsedObjects, parsedShared );
    for( ZONE* zone : board->Zones() )
    {
        if( !zone->IsTeardropArea() ) continue;
        const auto found = unpaired.find( TeardropKey( *zone ) );
        // An identity another object of the copy already holds is never given twice.
        if( found == unpaired.end() || parsedObjects.contains( found->second ) ) continue;
        zone->SetUuid( found->second );
        unpaired.erase( found );
    }

    // The editor's current variant and the zone connections of the last fill live only
    // in memory; the check reads both (the zone connections are copied below).
    board->SetCurrentVariant( aBoard.GetCurrentVariant() );
    if( board->GetCurrentVariant() != aBoard.GetCurrentVariant() )
        throw std::runtime_error( "Native document snapshot could not select the board's current variant" );

    std::map<KIID, const BOARD_ITEM*> copied;
    std::set<KIID> copiedShared;
    CollectIdentities( *board, copied, copiedShared );
    for( const auto& [id, item] : source )
    {
        result->m_identities.insert( id );
        const auto found = copied.find( id );
        if( found == copied.end() || found->second->Type() != item->Type() )
        {
            ++result->m_gap.missing;
            continue;
        }
        auto* copy = const_cast<BOARD_ITEM*>( found->second );
        if( item->Type() == PCB_PAD_T )
            CopyZoneLayerOverrides( *static_cast<const PAD*>( item ), *static_cast<PAD*>( copy ) );
        else if( item->Type() == PCB_VIA_T )
            CopyZoneLayerOverrides( *static_cast<const PCB_VIA*>( item ), *static_cast<PCB_VIA*>( copy ) );
    }
    for( const auto& [id, item] : copied )
        if( !source.contains( id ) ) ++result->m_gap.unexpected;
    shared.insert( copiedShared.begin(), copiedShared.end() );
    result->m_gap.shared = shared.size();

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
