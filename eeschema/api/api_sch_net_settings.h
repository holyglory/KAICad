/* Declared project net settings and observed label projection. GPL-3.0-or-later. */
#ifndef API_SCH_NET_SETTINGS_H
#define API_SCH_NET_SETTINGS_H

#include <project/net_settings.h>
#include <api/api_utils.h>
#include <schematic/schematic_types.pb.h>
#include <google/protobuf/util/message_differencer.h>

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
    return google::protobuf::util::MessageDifferencer::Equals( aLeft, aRight );
}

inline bool SameDeclared( const MESSAGE& aLeft, const MESSAGE& aRight )
{
    auto left = aLeft;
    auto right = aRight;
    left.clear_label_assignments();
    right.clear_label_assignments();
    return Same( left, right );
}
}
#endif
