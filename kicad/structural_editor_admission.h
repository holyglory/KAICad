/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_STRUCTURAL_EDITOR_ADMISSION_H
#define KICAD_STRUCTURAL_EDITOR_ADMISSION_H
#include <api/common/types/structural_types.pb.h>
#include <kiid.h>
#include <map>
#include <set>

// Validate the graph the native window will traverse. The compiled engineering
// model remains responsible for electrical/library/quantity validation.
inline bool IsRenderableStructuralDiagram(
        const kiapi::automation::structure::v1::StructuralDiagramData& d )
{
    namespace S = kiapi::automation::structure::v1;
    std::set<std::string> ids;
    auto addId = [&]( const std::string& id )
    {
        return id != "00000000-0000-0000-0000-000000000000"
            && KIID::SniffTest( wxString::FromUTF8( id ) ) && ids.insert( id ).second;
    };
    if( !addId( d.id() ) ) return false;
    std::map<std::string, std::string> parents;
    for( const auto& b : d.blocks() )
    {
        if( !addId( b.id() ) || wxString::FromUTF8( b.name() ).Strip( wxString::both ).empty() ) return false;
        parents.emplace( b.id(), b.has_parent_id() ? b.parent_id() : "" );
    }
    std::set<std::string> verified;
    for( const auto& [id, unused] : parents )
    {
        std::set<std::string> path;
        std::string current = id;
        while( !current.empty() && !verified.count( current ) )
        {
            if( !parents.count( current ) || !path.insert( current ).second ) return false;
            current = parents.at( current );
        }
        verified.insert( path.begin(), path.end() );
    }
    std::map<std::string, std::string> portOwners;
    for( const auto& p : d.ports() )
    {
        if( !addId( p.id() ) || !parents.count( p.block_id() ) ) return false;
        portOwners.emplace( p.id(), p.block_id() );
    }
    std::set<std::string> connections;
    for( const auto& c : d.connections() )
    {
        if( !addId( c.id() ) || !portOwners.count( c.first_port_id() ) || !portOwners.count( c.second_port_id() )
            || !S::StructuralLinkKind_IsValid( c.kind() ) || !S::StructuralLinkDirection_IsValid( c.direction() ) ) return false;
        connections.insert( c.id() );
    }
    const auto owners = ids;
    for( const auto& s : d.statements() )
        if( !addId( s.id() ) || !owners.count( s.target_id() ) || !S::StructuralStatementRole_IsValid( s.role() )
            || ( s.has_strength() && !S::StructuralGuidanceStrength_IsValid( s.strength() ) ) ) return false;
    for( const auto& p : d.properties() ) if( !addId( p.id() ) || !owners.count( p.owner_id() ) ) return false;

    constexpr int64_t LIMIT = 2147483647000LL;
    auto coordinate = []( int64_t value ) { return value >= -LIMIT && value <= LIMIT && value % 100 == 0; };
    auto point = [&]( const S::StructuralPoint& value ) { return coordinate( value.x_nm() ) && coordinate( value.y_nm() ); };
    std::map<std::string, const S::StructuralBlockPlacement*> layouts;
    for( const auto& b : d.presentation().blocks() )
    {
        if( !parents.count( b.block_id() ) || !layouts.emplace( b.block_id(), &b ).second
            || !point( b.position() ) || b.width_nm() <= 0 || b.height_nm() <= 0
            || !coordinate( b.width_nm() ) || !coordinate( b.height_nm() )
            || !coordinate( b.position().x_nm() + b.width_nm() ) || !coordinate( b.position().y_nm() + b.height_nm() )
            || ( b.has_fill_rgb() && b.fill_rgb() > 0xffffff ) ) return false;
    }
    std::set<std::string> placedPorts;
    for( const auto& p : d.presentation().ports() )
    {
        if( !portOwners.count( p.port_id() ) || !placedPorts.insert( p.port_id() ).second
            || !layouts.count( portOwners.at( p.port_id() ) ) || !S::StructuralPortSide_IsValid( p.side() )
            || p.offset_nm() < 0 || !coordinate( p.offset_nm() ) ) return false;
        const auto* b = layouts.at( portOwners.at( p.port_id() ) );
        if( p.offset_nm() > ( p.side() == S::SPS_LEFT || p.side() == S::SPS_RIGHT ? b->height_nm() : b->width_nm() ) ) return false;
    }
    std::set<std::string> placedLinks;
    for( const auto& c : d.presentation().connections() )
    {
        if( !connections.count( c.connection_id() ) || !placedLinks.insert( c.connection_id() ).second
            || ( c.has_label() && !point( c.label() ) ) ) return false;
        for( const auto& p : c.waypoints() ) if( !point( p ) ) return false;
    }
    return true;
}
#endif
