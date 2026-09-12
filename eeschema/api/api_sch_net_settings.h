/* Declared project net settings and observed label projection. GPL-3.0-or-later. */
#ifndef API_SCH_NET_SETTINGS_H
#define API_SCH_NET_SETTINGS_H

#include <project/net_settings.h>
#include <api/api_utils.h>
#include <schematic/schematic_types.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <settings/json_settings_internals.h>
#include <set>
#include <stdexcept>
#include <cmath>

namespace SCH_NET_SETTINGS
{
using MESSAGE = kiapi::schematic::types::SchematicNetSettings;

inline MESSAGE Capture( NET_SETTINGS& aSettings )
{
    MESSAGE value;
    if( const auto& defaults = aSettings.GetDefaultNetclass() )
        defaults->Serialize( *value.mutable_default_class() );
    for( const auto& [name, netclass] : aSettings.GetNetclasses() )
        netclass->Serialize( *value.add_classes() );
    for( const auto& [net, names] : aSettings.GetNetclassLabelAssignments() )
    {
        auto& assignment = ( *value.mutable_label_assignments() )[net.ToStdString( wxConvUTF8 )];
        for( const auto& name : names ) assignment.add_names( name.ToStdString( wxConvUTF8 ) );
    }
    for( const auto& [matcher, name] : aSettings.GetNetclassPatternAssignments() )
    {
        auto* pattern = value.add_patterns();
        pattern->set_pattern( matcher->GetPattern().ToStdString( wxConvUTF8 ) );
        pattern->set_net_class( name.ToStdString( wxConvUTF8 ) );
    }
    for( const auto& [net, color] : aSettings.GetNetColorAssignments() )
        kiapi::common::PackColor( ( *value.mutable_net_colors() )[net.ToStdString( wxConvUTF8 )], color );
    for( const auto& [chain, name] : aSettings.GetNetChainNetClasses() )
        ( *value.mutable_chain_netclasses() )[chain.ToStdString( wxConvUTF8 )] = name.ToStdString( wxConvUTF8 );
    return value;
}

inline bool Same( const MESSAGE& aLeft, const MESSAGE& aRight )
{
    google::protobuf::util::MessageDifferencer comparison;
    comparison.TreatAsMap( MESSAGE::descriptor()->FindFieldByName( "classes" ),
                          kiapi::common::project::NetClass::descriptor()->FindFieldByName( "name" ) );
    comparison.TreatAsSet( kiapi::schematic::types::SchematicNetClassNames::descriptor()->FindFieldByName( "names" ) );
    return comparison.Compare( aLeft, aRight );
}

inline void ValidateColor( const kiapi::common::types::Color& aColor )
{
    for( double component : { aColor.r(), aColor.g(), aColor.b(), aColor.a() } )
        if( !std::isfinite( component ) || component < 0 || component > 1 )
            throw std::runtime_error( "Net colors require finite RGBA components between zero and one" );
}

inline bool SameDeclared( const MESSAGE& aLeft, const MESSAGE& aRight )
{
    auto left = aLeft;
    auto right = aRight;
    left.clear_label_assignments();
    right.clear_label_assignments();
    return Same( left, right );
}

inline std::shared_ptr<NETCLASS> PrepareClass( const kiapi::common::project::NetClass& aValue,
                                             bool aDefault )
{
    if( aValue.name().empty() || aValue.name().find( '\0' ) != std::string::npos
            || ( aValue.name() == NETCLASS::Default ) != aDefault
            || aValue.type() != kiapi::common::project::NCT_EXPLICIT
            || !aValue.constituents().empty() || !aValue.has_priority() )
        throw std::runtime_error( "Net settings require named explicit classes with declared priorities" );
    auto prepared = std::make_shared<NETCLASS>( wxString::FromUTF8( aValue.name() ), false );
    if( aValue.board().has_color() ) ValidateColor( aValue.board().color() );
    if( aValue.schematic().has_color() ) ValidateColor( aValue.schematic().color() );
    if( !prepared->Deserialize( aValue ) )
        throw std::runtime_error( "Net class contains unsupported native values" );
    kiapi::common::project::NetClass captured;
    prepared->Serialize( captured );
    if( !google::protobuf::util::MessageDifferencer::Equals( captured, aValue ) )
        throw std::runtime_error( "Net class would lose or normalize fields during native reconstruction" );
    return prepared;
}

inline std::unique_ptr<NET_SETTINGS> PrepareDeclared( const MESSAGE& aValue )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() || !aValue.has_default_class() )
        throw std::runtime_error( "Net settings require a captured Default class and no unsupported fields" );
    auto prepared = std::make_unique<NET_SETTINGS>( nullptr, "net_settings" );
    prepared->SetDefaultNetclass( PrepareClass( aValue.default_class(), true ) );
    std::map<wxString, std::shared_ptr<NETCLASS>> classes;
    for( const auto& entry : aValue.classes() )
    {
        auto item = PrepareClass( entry, false );
        if( !classes.emplace( item->GetName(), item ).second )
            throw std::runtime_error( "Declared netclass names must be unique" );
    }
    prepared->SetNetclasses( classes );
    for( const auto& pattern : aValue.patterns() )
        prepared->SetNetclassPatternAssignment( wxString::FromUTF8( pattern.pattern() ),
                                               wxString::FromUTF8( pattern.net_class() ) );
    for( const auto& [net, color] : aValue.net_colors() )
    {
        ValidateColor( color );
        prepared->SetNetColorAssignment( wxString::FromUTF8( net ), kiapi::common::UnpackColor( color ) );
    }
    for( const auto& [chain, netclass] : aValue.chain_netclasses() )
        prepared->SetNetChainNetClass( wxString::FromUTF8( chain ), wxString::FromUTF8( netclass ) );
    if( !SameDeclared( Capture( *prepared ), aValue ) )
        throw std::runtime_error( "Declared net settings do not preserve exact native values and pattern order" );
    // Verify the native persistence boundary, not just the protobuf codec.
    NET_SETTINGS reopened( nullptr, "net_settings" );
    prepared->CopyCurrentStateTo( reopened );
    if( !SameDeclared( Capture( reopened ), aValue ) )
        throw std::runtime_error( "Declared net settings cannot survive native project save/load exactly" );
    return prepared;
}

inline nlohmann::json DesiredState( const NET_SETTINGS& aLive, NET_SETTINGS& aPrepared )
{
    auto desired = aLive.CaptureCurrentState();
    const auto prepared = aPrepared.CaptureCurrentState();
    // Labels are connectivity projections; chain grouping has its own typed
    // owner. Never replace either through the declared net-settings operation.
    for( const char* key : { "classes", "netclass_patterns", "net_colors", "net_chain_netclasses" } )
        desired[key] = prepared.at( key );
    return desired;
}

inline void ApplyPrepared( NET_SETTINGS& aLive, NET_SETTINGS& aPrepared )
{
    const auto before = aLive.CaptureCurrentState();
    const auto after = DesiredState( aLive, aPrepared );
    try
    {
        aLive.ApplyCurrentStateDelta( before, after );
    }
    catch( ... )
    {
        // A rollback can replace class objects too. Do not retain effective
        // composites referring to pre-setter objects after either outcome.
        aLive.ClearAllCaches();
        throw;
    }
    aLive.ClearAllCaches();
}
}
#endif
