/* Pure projection of PCB editor-owned project state. GPL-3.0-or-later. */
#ifndef KICAD_PCB_PROJECT_EDITOR_STATE_H
#define KICAD_PCB_PROJECT_EDITOR_STATE_H

#include <board.h>
#include <board_design_settings.h>
#include <pcb_marker.h>
#include <project/board_project_settings_params.h>
#include <project/project_file.h>
#include <json_common.h>
#include <cmath>
#include <stdexcept>

namespace PCB_PROJECT_EDITOR_STATE
{
inline std::set<DRC_EXCLUSION, DRC_EXCLUSION_COMPARE> Exclusions( const BOARD& board )
{
    // A declaration without a currently instantiated marker is still owned data.
    auto exclusions = board.GetDesignSettings().m_DrcExclusions;
    for( const PCB_MARKER* marker : board.Markers() )
    {
        if( !marker->GetRCItem() ) continue;
        const auto current = DRC_EXCLUSION::FromMarker( *marker );
        exclusions.erase( current );
        if( marker->IsExcluded() ) exclusions.insert( current );
    }
    return exclusions;
}

inline nlohmann::json Capture( const BOARD& board, const PROJECT_FILE& project,
                               std::vector<LAYER_PRESET> presets, std::vector<VIEWPORT> viewports )
{
    for( const auto& viewport : viewports )
        if( !std::isfinite( viewport.rect.GetX() ) || !std::isfinite( viewport.rect.GetY() )
                || !std::isfinite( viewport.rect.GetWidth() ) || !std::isfinite( viewport.rect.GetHeight() ) )
            throw std::runtime_error( "Named PCB viewports must have finite persisted coordinates" );
    JSON_SETTINGS scratch( "", SETTINGS_LOC::NONE, 0, false, false, false );
    PARAM_LAYER_PRESET layers( "board.layer_presets", &presets );
    PARAM_VIEWPORT views( "board.viewports", &viewports );
    layers.StoreStrict( &scratch ); views.StoreStrict( &scratch );
    nlohmann::json result = project.CaptureCurrentState();
    result["board"]["layer_presets"] = *scratch.GetJson( "board.layer_presets" );
    result["board"]["viewports"] = *scratch.GetJson( "board.viewports" );
    nlohmann::json exclusions = nlohmann::json::array();
    for( const auto& exclusion : Exclusions( board ) ) exclusions.push_back( exclusion );
    // Reuse the owning nested settings path, rather than inventing another schema.
    std::string pointer;
    for( char c : board.GetDesignSettings().GetPath() ) pointer += c == '.' ? '/' : c;
    result[nlohmann::json::json_pointer( "/" + pointer + "/drc_exclusions" )] = std::move( exclusions );
    return result;
}
}
#endif
