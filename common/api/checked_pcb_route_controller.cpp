/* Checked PCB route admission, commit and retry receipts. GPL-3.0-or-later. */
#include <api/checked_pcb_route_controller.h>
#include <api/document_lifecycle_controller.h>
#include <api/common/commands/automation_commands.pb.h>
#include <api/common/commands/editor_commands.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <algorithm>
#include <set>
#include <vector>

namespace
{
using namespace kiapi::automation::v1;
using namespace kiapi::common::commands;
using namespace kiapi::common::types;
using namespace kiapi::board::types;
using google::protobuf::util::MessageDifferencer;
using BoardArc = kiapi::board::types::Arc;

bool Uuid( const std::string& value )
{
    if( value.size() != 36 ) return false;
    bool nonzero = false;
    for( size_t i = 0; i < value.size(); ++i )
    {
        if( i == 8 || i == 13 || i == 18 || i == 23 ) { if( value[i] != '-' ) return false; }
        else
        {
            const char c = value[i];
            if( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
            nonzero |= c != '0';
        }
    }
    return nonzero;
}

bool Digest( const std::string& value )
{
    return value.size() == 64 && std::all_of( value.begin(), value.end(), []( char c )
    { return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ); } );
}

// KiCad's LSET uses the physical board order (front copper, back copper,
// then inner copper), while PadStack's per-layer profiles are serialized from
// an unordered map.  Route equality is about the layer set and each profile,
// so neither repeated-field order nor the default profile that can remain in
// a custom padstack is meaningful state.
int LayerOrder( BoardLayer layer )
{
    if( layer == BL_F_Cu ) return 0;
    if( layer == BL_B_Cu ) return 1;
    if( layer >= BL_In1_Cu && layer <= BL_In30_Cu )
        return 2 + static_cast<int>( layer ) - static_cast<int>( BL_In1_Cu );
    return 1000 + static_cast<int>( layer );
}

bool ZeroVector( const Vector2& value )
{
    return value.x_nm() == 0 && value.y_nm() == 0;
}

bool EmptyCopperLayer( const PadStackLayer& layer )
{
    if( layer.shape() != PSS_CIRCLE || layer.custom_anchor_shape() != PSS_RECTANGLE
        || !ZeroVector( layer.size() ) || !ZeroVector( layer.offset() )
        || layer.corner_rounding_ratio() != 0.0 || layer.chamfer_ratio() != 0.0
        || layer.chamfered_corners().top_left() || layer.chamfered_corners().top_right()
        || layer.chamfered_corners().bottom_left() || layer.chamfered_corners().bottom_right()
        || layer.custom_shapes_size() != 0 || layer.has_zone_settings()
        || !ZeroVector( layer.trapezoid_delta() ) )
        return false;

    return true;
}

API_RESULT Error( const std::string& message )
{
    ApiResponseStatus status; status.set_status( ApiStatusCode::AS_BAD_REQUEST );
    status.set_error_message( message ); return tl::unexpected( status );
}

API_RESULT Pack( const CheckedPcbRouteReceipt& receipt )
{
    ApiResponse response; response.mutable_status()->set_status( ApiStatusCode::AS_OK );
    response.mutable_message()->PackFrom( receipt ); return response;
}

template<typename Route>
bool UniqueProperties( const Route& value )
{
    std::set<std::string> keys;
    for( const auto& property : value.custom_properties() )
        if( !keys.insert( property.key() ).second ) return false;
    return true;
}

bool CandidateType( const google::protobuf::Any& value )
{
    if( value.Is<Track>() ) { Track item; return value.UnpackTo( &item ) && UniqueProperties( item ); }
    if( value.Is<BoardArc>() ) { BoardArc item; return value.UnpackTo( &item ) && UniqueProperties( item ); }
    if( value.Is<Via>() ) { Via item; return value.UnpackTo( &item ) && UniqueProperties( item ); }
    return false;
}


std::string RouteItemId( const google::protobuf::Any& value )
{
    if( value.Is<Track>() )
    {
        Track item;
        if( !value.UnpackTo( &item ) || !item.has_id() ) return {};
        return item.id().value();
    }

    if( value.Is<BoardArc>() )
    {
        BoardArc item;
        if( !value.UnpackTo( &item ) || !item.has_id() ) return {};
        return item.id().value();
    }

    if( value.Is<Via>() )
    {
        Via item;
        if( !value.UnpackTo( &item ) || !item.has_id() ) return {};
        return item.id().value();
    }

    return {};
}

// Compare complete typed state after the narrow native serialization
// normalizations below. Scalar defaults and empty collections remain evidence.
template<typename Route>
void NormalizeBasicRoute( Route& value )
{
    // Native custom properties are a map, so their wire order is not state.
    // Duplicate keys are rejected before normalization and never coalesced.
    std::vector<CustomProperty> properties( value.custom_properties().begin(), value.custom_properties().end() );
    std::sort( properties.begin(), properties.end(), []( const auto& a, const auto& b ) { return a.key() < b.key(); } );
    value.clear_custom_properties();
    for( const auto& property : properties ) value.add_custom_properties()->CopyFrom( property );
    value.clear_parent();
    if( value.locked() == LS_UNKNOWN ) value.set_locked( LS_UNLOCKED );
    auto* net = value.mutable_net();
    if( !net->name().empty() ) net->clear_code();
}

void NormalizeRoute( Track& value ) { NormalizeBasicRoute( value ); }
void NormalizeRoute( BoardArc& value ) { NormalizeBasicRoute( value ); }

void NormalizeRoute( Via& value )
{
    NormalizeBasicRoute( value );
    if( !value.has_teardrop() )
    {
        // PCB_VIA's unchanged TEARDROP_PARAMETERS constructor defaults when
        // Deserialize disables teardrops without receiving their parameters.
        auto* settings = value.mutable_teardrop();
        settings->set_mode( PTM_DISABLED );
        settings->set_allow_multiple_track_segments( true );
        settings->set_prefer_zone_connection( true );
        settings->mutable_max_length()->set_value_nm( 1000000 );
        settings->mutable_max_width()->set_value_nm( 2000000 );
        settings->set_best_length_ratio( 0.5 );
        settings->set_best_width_ratio( 1.0 );
        settings->set_max_track_width_ratio( 0.9 );
    }
    auto* teardrop = value.mutable_teardrop();
    if( teardrop->mode() == PTM_UNKNOWN ) teardrop->set_mode( PTM_DISABLED );
    teardrop->mutable_max_length();
    teardrop->mutable_max_width();
    if( !value.has_pad_stack() ) return;
    auto* stack = value.mutable_pad_stack();
    if( stack->type() == PST_UNKNOWN ) stack->set_type( PST_NORMAL );
    if( stack->unconnected_layer_removal() == ULR_UNKNOWN )
        stack->set_unconnected_layer_removal( ULR_KEEP );
    stack->mutable_angle();
    if( stack->has_drill() && stack->drill().shape() == DS_UNKNOWN )
        stack->mutable_drill()->set_shape( DS_UNDEFINED );
    for( auto& layer : *stack->mutable_copper_layers() )
    {
        layer.mutable_offset();
        if( layer.shape() == PSS_UNKNOWN ) layer.set_shape( PSS_CIRCLE );
        if( layer.custom_anchor_shape() == PSS_UNKNOWN ) layer.set_custom_anchor_shape( PSS_CIRCLE );
    }

    std::vector<BoardLayer> activeLayers;
    activeLayers.reserve( stack->layers_size() );
    for( int rawLayer : stack->layers() )
        activeLayers.push_back( static_cast<BoardLayer>( rawLayer ) );
    std::sort( activeLayers.begin(), activeLayers.end(), []( BoardLayer left, BoardLayer right )
    { return LayerOrder( left ) < LayerOrder( right ); } );
    stack->clear_layers();
    for( BoardLayer layer : activeLayers )
        stack->add_layers( layer );

    std::vector<PadStackLayer> copperLayers( stack->copper_layers().begin(),
                                             stack->copper_layers().end() );
    std::set<int> activeLayerValues;
    for( BoardLayer layer : activeLayers )
        activeLayerValues.insert( static_cast<int>( layer ) );
    copperLayers.erase( std::remove_if( copperLayers.begin(), copperLayers.end(),
                                        [&]( const PadStackLayer& layer )
                                        {
                                            return activeLayerValues.count( static_cast<int>( layer.layer() ) ) == 0
                                                   && EmptyCopperLayer( layer );
                                        } ),
                        copperLayers.end() );
    std::stable_sort( copperLayers.begin(), copperLayers.end(),
                      []( const PadStackLayer& left, const PadStackLayer& right )
                      { return LayerOrder( left.layer() ) < LayerOrder( right.layer() ); } );
    stack->clear_copper_layers();
    for( const PadStackLayer& layer : copperLayers )
        stack->add_copper_layers()->CopyFrom( layer );

    if( !stack->has_zone_settings() )
    {
        auto shape = stack->copper_layers_size() ? stack->copper_layers( 0 ).shape() : PSS_CIRCLE;
        double angle = shape == PSS_OVAL || shape == PSS_RECTANGLE || shape == PSS_ROUNDRECT
                       || shape == PSS_CHAMFEREDRECT ? 90.0 : 45.0;
        auto* settings = stack->mutable_zone_settings();
        settings->set_zone_connection( ZCS_INHERITED );
        settings->mutable_thermal_spokes()->mutable_angle()->set_value_degrees( angle );
    }
    else
    {
        auto* settings = stack->mutable_zone_settings();
        if( settings->zone_connection() == ZCS_UNKNOWN ) settings->set_zone_connection( ZCS_INHERITED );
        if( !settings->has_thermal_spokes() )
            settings->mutable_thermal_spokes()->mutable_angle()->set_value_degrees( 45.0 );
        else
            settings->mutable_thermal_spokes()->mutable_angle();
    }
}

template<typename Route>
bool TypedRouteEquivalent( const google::protobuf::Any& expected,
                           const google::protobuf::Any& actual )
{
    Route left, right;
    if( !expected.UnpackTo( &left ) || !actual.UnpackTo( &right )
        || !UniqueProperties( left ) || !UniqueProperties( right ) ) return false;
    NormalizeRoute( left ); NormalizeRoute( right );
    return MessageDifferencer::Equals( left, right );
}

bool RouteItemEquivalent( const google::protobuf::Any& expected,
                          const google::protobuf::Any& actual )
{
    if( expected.Is<Track>() && actual.Is<Track>() ) return TypedRouteEquivalent<Track>( expected, actual );
    if( expected.Is<BoardArc>() && actual.Is<BoardArc>() ) return TypedRouteEquivalent<BoardArc>( expected, actual );
    if( expected.Is<Via>() && actual.Is<Via>() ) return TypedRouteEquivalent<Via>( expected, actual );
    return false;
}

bool GroupIdentity( const kiapi::board::types::Group& group )
{
    return Uuid( group.id().value() );
}

kiapi::board::types::Group CanonicalGroup( const kiapi::board::types::Group& source )
{
    kiapi::board::types::Group result = source;
    std::vector<std::string> ids;
    for( const auto& id : result.items() ) ids.push_back( id.value() );
    std::sort( ids.begin(), ids.end() );
    result.clear_items();
    for( const std::string& id : ids ) result.add_items()->set_value( id );
    return result;
}

bool GroupMetadataEquivalent( const kiapi::board::types::Group& left,
                              const kiapi::board::types::Group& right )
{
    kiapi::board::types::Group a = left;
    kiapi::board::types::Group b = right;
    a.clear_items();
    b.clear_items();
    return MessageDifferencer::Equals( a, b );
}

bool GroupMembersValid( const kiapi::board::types::Group& group )
{
    std::set<std::string> ids;
    for( const auto& id : group.items() )
        if( !Uuid( id.value() ) || !ids.insert( id.value() ).second ) return false;
    return true;
}

bool GroupChangeValid( const NativeRouteGroupChange& change,
                       const std::set<std::string>& candidateIds,
                       const std::set<std::string>& updateIds,
                       const std::set<std::string>& removedIds )
{
    if( !change.has_expected_before() || !change.has_expected_after()
        || !change.expected_before().has_group() || !change.expected_after().has_group()
        || change.expected_before().kind() != NRGK_ORDINARY
        || change.expected_after().kind() != NRGK_ORDINARY
        || change.expected_before().has_parent_group_id()
        || change.expected_after().has_parent_group_id() )
        return false;
    const auto& before = change.expected_before().group();
    const auto& after = change.expected_after().group();
    if( !GroupIdentity( before ) || !GroupIdentity( after )
        || before.id().value() != after.id().value()
        || !GroupMetadataEquivalent( before, after )
        || !GroupMembersValid( before ) || !GroupMembersValid( after ) )
        return false;
    std::set<std::string> beforeIds, afterIds, addedIds, created;
    for( const auto& id : before.items() ) beforeIds.insert( id.value() );
    for( const auto& id : after.items() ) afterIds.insert( id.value() );
    for( const std::string& id : afterIds )
        if( !beforeIds.contains( id ) ) addedIds.insert( id );
    for( const auto& source : change.created_sources() )
    {
        const std::string createdId = source.created_item_id().value();
        const std::string sourceId = source.source_item_id().value();
        if( !Uuid( createdId ) || !Uuid( sourceId ) || !Uuid( source.source_group_id().value() )
            || source.source_group_id().value() != before.id().value()
            || !candidateIds.contains( createdId ) || !addedIds.contains( createdId )
            || !beforeIds.contains( sourceId )
            || !created.insert( createdId ).second
            || ( !removedIds.contains( sourceId ) && !updateIds.contains( sourceId ) ) )
            return false;
    }
    // Every new direct member must carry exactly one native source identity;
    // unchanged members and removed source members may not be mislabeled.
    return created == addedIds;
}

bool GroupChangesEquivalent( const google::protobuf::RepeatedPtrField<NativeRouteGroupChange>& left,
                              const google::protobuf::RepeatedPtrField<NativeRouteGroupChange>& right )
{
    if( left.size() != right.size() ) return false;
    for( int index = 0; index < left.size(); ++index )
        if( !MessageDifferencer::Equals( left.Get( index ), right.Get( index ) ) ) return false;
    return true;
}

bool GroupChangesValid( const CheckedPcbRouteCommit& request )
{
    std::set<std::string> candidateIds, updateIds, removedIds, groupIds;
    for( const auto& item : request.candidate().items() ) candidateIds.insert( RouteItemId( item ) );
    for( const auto& item : request.replacement_updates().items() ) updateIds.insert( RouteItemId( item ) );
    for( const auto& id : request.removals().item_ids() ) removedIds.insert( id.value() );
    for( const auto& change : request.group_changes() )
    {
        if( !GroupChangeValid( change, candidateIds, updateIds, removedIds )
            || !groupIds.insert( change.expected_before().group().id().value() ).second )
            return false;
    }
    return true;
}

google::protobuf::Any PackGroup( const kiapi::board::types::Group& group )
{
    google::protobuf::Any result;
    result.PackFrom( group );
    return result;
}

kiapi::board::types::Group UnpackGroup( const google::protobuf::Any& value )
{
    kiapi::board::types::Group result;
    if( !value.UnpackTo( &result ) ) result.Clear();
    return result;
}

bool GroupResponseExact( const UpdateItemsResponse& response,
                         const kiapi::common::commands::UpdateItems& request )
{
    if( response.status() != IRS_OK || response.updated_items_size() != request.items_size() ) return false;
    for( int index = 0; index < request.items_size(); ++index )
    {
        const auto& item = response.updated_items( index );
        if( !item.has_status() || item.status().code() != ISC_OK || !item.has_item()
            || !item.item().Is<kiapi::board::types::Group>() ) return false;
        if( !MessageDifferencer::Equals( CanonicalGroup( UnpackGroup( item.item() ) ),
                                         CanonicalGroup( UnpackGroup( request.items( index ) ) ) ) )
            return false;
    }
    return true;
}


bool Valid( const CheckedPcbRouteCommit& request, const std::string& epoch,
            bool requireFileCoverage = true, bool requireCompleteTracking = true )
{
    const auto& candidate = request.candidate(); const auto& state = request.expected_state();
    if( !( request.has_candidate() && request.has_expected_state()
        && candidate.has_header() && candidate.header().has_document()
        && candidate.header().document().type() == DOCTYPE_PCB
        && candidate.header().document().has_board_filename()
        && !candidate.header().document().board_filename().empty()
        && candidate.header().document().has_project()
        && !candidate.header().document().project().name().empty()
        && !candidate.header().document().project().path().empty()
        && !candidate.has_container() && !candidate.header().has_container() && !candidate.header().has_field_mask()
        && ( candidate.items_size() > 0 || request.replacement_updates().items_size() > 0
             || request.removals().item_ids_size() > 0 ) && std::all_of( candidate.items().begin(), candidate.items().end(), CandidateType )
        && Uuid( request.operation_id() ) && Uuid( request.drc_job_id() )
        && Uuid( state.process_epoch() ) && state.process_epoch() == epoch
        && state.scope() == DLS_PCB && state.project_settings_included()
        && state.document().type() == DOCTYPE_PCB
        && MessageDifferencer::Equals( state.document(), candidate.header().document() )
        && state.has_revision() && Uuid( state.native_identity() )
        && Digest( state.state_sha256() ) && state.revision().epoch() == state.native_identity()
        && state.project_settings_included()
        && ( !requireCompleteTracking || state.complete_change_tracking() )
        && ( !requireFileCoverage || DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( state ) ) ) )
        return false;

    std::set<std::string> createdIds, updatedIds, removedIds;
    for( const auto& item : candidate.items() )
    {
        const std::string id = RouteItemId( item );
        if( !CandidateType( item ) || !Uuid( id ) || !createdIds.insert( id ).second ) return false;
    }
    auto headerMatches = [&]( const ItemHeader& header )
    {
        return header.has_document() && !header.has_container() && !header.has_field_mask()
               && MessageDifferencer::Equals( header.document(), candidate.header().document() );
    };
    if( request.has_replacement_updates() )
    {
        const auto& updates = request.replacement_updates();
        if( !updates.has_header() || !headerMatches( updates.header() ) || updates.items_size() == 0 ) return false;
        for( const auto& item : updates.items() )
        {
            const std::string id = RouteItemId( item );
            if( !CandidateType( item ) || !Uuid( id ) || !updatedIds.insert( id ).second || createdIds.contains( id ) )
                return false;
        }
    }
    if( request.has_removals() )
    {
        const auto& removals = request.removals();
        if( !removals.has_header() || !headerMatches( removals.header() ) || removals.item_ids_size() == 0 ) return false;
        for( const auto& item : removals.item_ids() )
            if( !Uuid( item.value() ) || !removedIds.insert( item.value() ).second
                || createdIds.contains( item.value() ) || updatedIds.contains( item.value() ) ) return false;
    }

    if( !GroupChangesValid( request ) ) return false;
    return true;
}
}

const std::vector<std::string>& CHECKED_PCB_ROUTE_CONTROLLER::RequestTypes()
{
    static const std::vector<std::string> types = []
    {
        std::vector<std::string> result{ std::string( CheckedPcbRouteCommit::descriptor()->full_name() ),
            std::string( ReadCheckedPcbRouteReceipt::descriptor()->full_name() ) };
        std::sort( result.begin(), result.end() ); return result;
    }();
    return types;
}

bool CHECKED_PCB_ROUTE_CONTROLLER::Handles( const ApiRequest& request )
{
    std::string type;
    if( !google::protobuf::Any::ParseAnyTypeUrl( request.message().type_url(), &type ) ) return false;
    const auto& types = RequestTypes(); return std::binary_search( types.begin(), types.end(), type );
}

API_RESULT CHECKED_PCB_ROUTE_CONTROLLER::Handle( ApiRequest& envelope, const std::string& epoch,
        const DISPATCH& dispatch )
{
    if( envelope.message().Is<ReadCheckedPcbRouteReceipt>() )
    {
        ReadCheckedPcbRouteReceipt query;
        if( !envelope.message().UnpackTo( &query ) || !query.has_expected_request()
            || !Valid( query.expected_request(), epoch, false, false )
            || query.process_epoch() != epoch || query.operation_id() != query.expected_request().operation_id()
            || !MessageDifferencer::Equals( query.document(), query.expected_request().candidate().header().document() ) )
            return Error( "Require the exact checked route request, document and process epoch to inspect a receipt" );
        auto found = m_receipts.find( query.operation_id() );
        if( found == m_receipts.end() )
        {
            CheckedPcbRouteReceipt result; result.mutable_document()->CopyFrom( query.document() );
            result.set_process_epoch( epoch ); result.set_operation_id( query.operation_id() );
            result.set_state( CheckedPcbRouteReceipt::NOT_FOUND ); return Pack( result );
        }
        if( !MessageDifferencer::Equals( found->second.request, query.expected_request() ) )
            return Error( "Checked route operation ID belongs to a different request" );
        return Pack( found->second.result );
    }

    CheckedPcbRouteCommit request;
    // This route-specific admission compares the complete serialized state and
    // every listed file baseline. The generic complete-change flag remains false
    // until all native mutation owners have universal event proof.
    if( !envelope.message().UnpackTo( &request ) || !Valid( request, epoch, true, false ) )
        return Error( "A checked PCB route requires an exact board state, process identity, DRC job and operation UUID" );
    auto existing = m_receipts.find( request.operation_id() );
    if( existing != m_receipts.end() )
    {
        if( !MessageDifferencer::Equals( existing->second.request, request ) )
            return Error( "Checked route operation ID was already used with another request" );
        auto replay = existing->second.result; replay.set_replayed( true ); return Pack( replay );
    }
    const size_t resultBudget = request.maximum_result_bytes() == 0 ? 4 * 1024 * 1024 : request.maximum_result_bytes();
    if( request.ByteSizeLong() > 4 * 1024 * 1024 || resultBudget < 64 * 1024
        || resultBudget > 4 * 1024 * 1024
        || m_receipts.size() >= 256 )
        return Error( "Checked route receipt capacity exhausted; no mutation was attempted" );
    if( resultBudget < ( request.candidate().ByteSizeLong() + request.replacement_updates().ByteSizeLong()
                        + request.removals().ByteSizeLong() ) * 3
                        + request.expected_state().ByteSizeLong() * 2 + 4096 )
        return Error( "Route candidate is too large for the requested receipt allowance" );
    RECEIPT receipt; receipt.request = request;
    auto& result = receipt.result; result.mutable_document()->CopyFrom( request.candidate().header().document() );
    result.set_process_epoch( epoch ); result.set_operation_id( request.operation_id() );
    result.set_state( CheckedPcbRouteReceipt::INDETERMINATE ); result.set_request_verified( true );
    result.set_error_code( "operation_in_progress" ); m_receipts.emplace( request.operation_id(), receipt );
    auto& stored = m_receipts.at( request.operation_id() ).result;
    stored.mutable_group_changes()->CopyFrom( request.group_changes() );
    for( const auto& change : request.group_changes() )
        stored.add_after_group_states()->CopyFrom( change.expected_after() );

    auto call = [&]( const google::protobuf::Message& message ) -> API_RESULT
    {
        ApiRequest query; query.mutable_header()->CopyFrom( envelope.header() );
        query.mutable_message()->PackFrom( message ); return dispatch( query );
    };
    auto fail = [&]( CheckedPcbRouteReceipt::State state, const char* code, const std::string& message )
    { stored.set_state( state ); stored.set_error_code( code ); stored.set_error_message( message.substr( 0, 2048 ) ); return Pack( stored ); };
    bool commitOpen = false;
    kiapi::common::types::KIID beginId;
    try
    {
    ReadDocumentLifecycleState stateRequest; stateRequest.mutable_document()->CopyFrom( request.candidate().header().document() );
    auto beforeReply = call( stateRequest );
    if( !beforeReply || beforeReply->status().status() != ApiStatusCode::AS_OK )
        return fail( CheckedPcbRouteReceipt::REJECTED, "state_observation_failed", beforeReply ? beforeReply->status().error_message() : beforeReply.error().error_message() );
    DocumentLifecycleState before;
    if( !beforeReply->message().UnpackTo( &before ) || !MessageDifferencer::Equals( before, request.expected_state() ) )
        return fail( CheckedPcbRouteReceipt::REJECTED, "stale_document_state", "The PCB changed since the checked route state" );
    stored.mutable_before()->CopyFrom( before );

    std::map<std::string, kiapi::board::types::Group> groupReadback;
    auto readGroups = [&]( bool beforeState ) -> bool
    {
        if( request.group_changes_size() == 0 ) return true;
        GetItemsById groups;
        groups.mutable_header()->CopyFrom( request.candidate().header() );
        for( const auto& change : request.group_changes() )
            groups.add_items()->CopyFrom( change.expected_before().group().id() );
        auto reply = call( groups );
        GetItemsResponse response;
        if( !reply || reply->status().status() != ApiStatusCode::AS_OK
            || !reply->message().UnpackTo( &response ) || response.status() != IRS_OK
            || response.items_size() != request.group_changes_size() )
            return false;
        std::map<std::string, kiapi::board::types::Group> actual;
        for( const auto& encoded : response.items() )
        {
            if( !encoded.Is<kiapi::board::types::Group>() ) return false;
            auto group = UnpackGroup( encoded );
            if( !GroupIdentity( group ) || !actual.emplace( group.id().value(), std::move( group ) ).second )
                return false;
        }
        for( const auto& change : request.group_changes() )
        {
            const auto& expected = beforeState ? change.expected_before().group() : change.expected_after().group();
            auto found = actual.find( expected.id().value() );
            if( found == actual.end()
                || !MessageDifferencer::Equals( CanonicalGroup( found->second ), CanonicalGroup( expected ) ) )
                return false;
        }
        if( !beforeState ) groupReadback = std::move( actual );
        return true;
    };
    if( !readGroups( true ) )
        return fail( CheckedPcbRouteReceipt::REJECTED, "route_group_before_mismatch",
                     "The checked route group membership does not match the observed board" );

    // Explicit removals may never address footprints, graphics or nonexistent
    // objects. Verify every existing target before opening the transaction.
    if( request.has_removals() || request.has_replacement_updates() )
    {
        GetItemsById existingItems; existingItems.mutable_header()->CopyFrom( request.candidate().header() );
        std::set<std::string> expectedIds;
        for( const auto& item : request.replacement_updates().items() )
        {
            const std::string id = RouteItemId( item ); expectedIds.insert( id );
            existingItems.add_items()->set_value( id );
        }
        for( const auto& id : request.removals().item_ids() )
        { expectedIds.insert( id.value() ); existingItems.add_items()->CopyFrom( id ); }
        auto targetReply = call( existingItems ); GetItemsResponse targets;
        if( !targetReply || targetReply->status().status() != ApiStatusCode::AS_OK
            || !targetReply->message().UnpackTo( &targets ) || targets.status() != IRS_OK
            || targets.items_size() != static_cast<int>( expectedIds.size() ) )
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_existing_targets_unverified", "Every updated or removed route must identify existing board copper" );
        for( const auto& target : targets.items() )
            if( !CandidateType( target ) || expectedIds.erase( RouteItemId( target ) ) != 1 )
                return fail( CheckedPcbRouteReceipt::REJECTED, "route_existing_targets_invalid", "An updated or removed target is not the requested Track, Arc or Via" );
        if( !expectedIds.empty() )
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_existing_targets_missing", "An updated or removed route no longer exists" );
    }

    ApiRequest drcRequestEnvelope;
    drcRequestEnvelope.mutable_header()->CopyFrom( envelope.header() );
    // Always use the replacement-aware read, including additive routes with
    // an empty delete list.  A plain ReadPcbDrcJob would verify only the
    // candidate payload and could therefore accept a job that silently removed
    // unrelated board copper during detached snapshot preparation.
    NativeCapabilityPcbDrcReplacementJob replacementRead;
    replacementRead.mutable_document()->CopyFrom( request.candidate().header().document() );
    replacementRead.set_process_epoch( epoch );
    replacementRead.set_job_id( request.drc_job_id() );
    replacementRead.mutable_expected_candidate_items()->CopyFrom( request.candidate().items() );
    if( request.has_replacement_updates() )
    {
        for( const auto& item : request.replacement_updates().items() )
        {
            replacementRead.add_expected_candidate_items()->CopyFrom( item );
            replacementRead.add_expected_delete_item_ids()->set_value( RouteItemId( item ) );
        }
    }
    for( const auto& id : request.removals().item_ids() )
        replacementRead.add_expected_delete_item_ids()->CopyFrom( id );
    replacementRead.mutable_expected_group_changes()->CopyFrom( request.group_changes() );
    drcRequestEnvelope.mutable_message()->PackFrom( replacementRead );
    auto drcReply = dispatch( drcRequestEnvelope );
    if( !drcReply || drcReply->status().status() != ApiStatusCode::AS_OK )
        return fail( CheckedPcbRouteReceipt::REJECTED, "drc_read_failed", drcReply ? drcReply->status().error_message() : drcReply.error().error_message() );
    PcbDrcJobState drc;
    if( !drcReply->message().UnpackTo( &drc ) || drc.status() != PDRCJS_COMPLETED || !drc.results_fresh()
        || !drc.snapshot_complete() || !drc.candidate_dry_run() || !drc.worker_finished()
        || drc.findings_size() != 0 || !drc.candidate_payload_verified()
        || ( request.group_changes_size() != 0
             && ( !drc.group_payload_verified()
                  || !GroupChangesEquivalent( drc.verified_group_changes(), request.group_changes() )
                  || drc.after_group_states_size() != request.group_changes_size() ) )
        || !MessageDifferencer::Equals( drc.checked_revision(), before.revision() ) )
        return fail( CheckedPcbRouteReceipt::REJECTED, "route_drc_not_proven", "The retained DRC receipt is not a complete, fresh, finding-free exact candidate check" );
    for( int index = 0; index < request.group_changes_size(); ++index )
        if( !MessageDifferencer::Equals( drc.after_group_states( index ), request.group_changes( index ).expected_after() ) )
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_drc_group_not_proven", "The retained DRC receipt did not prove exact grouped-route after-state" );

    BeginCommit begin; begin.mutable_header()->CopyFrom( request.candidate().header() );
    auto beginReply = call( begin );
    if( !beginReply || beginReply->status().status() != ApiStatusCode::AS_OK )
        return fail( CheckedPcbRouteReceipt::REJECTED, "commit_begin_failed", beginReply ? beginReply->status().error_message() : beginReply.error().error_message() );
    BeginCommitResponse beginState;
    commitOpen = true;
    if( !beginReply->message().UnpackTo( &beginState ) )
    {
        EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() ); drop.set_action( CMA_DROP );
        call( drop );
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "commit_begin_result_unknown", "Native commit admission was not parseable; inspect the route receipt" );
    }
    beginId = beginState.id();

    if( request.group_changes_size() > 0 )
    {
        // Stage an exact no-op group update first.  BOARD_COMMIT captures the
        // group image before any route child is removed, so undo can restore
        // the original direct membership without relying on geometry.
        UpdateItems prepareGroups;
        prepareGroups.mutable_header()->CopyFrom( request.candidate().header() );
        for( const auto& change : request.group_changes() )
            prepareGroups.add_items()->PackFrom( change.expected_before().group() );
        auto groupReply = call( prepareGroups );
        UpdateItemsResponse groups;
        if( !groupReply || groupReply->status().status() != ApiStatusCode::AS_OK
            || !groupReply->message().UnpackTo( &groups ) || !GroupResponseExact( groups, prepareGroups ) )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown",
                             "Native PCB rejected the checked route group admission and cleanup was not confirmed" );
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_group_before_rejected",
                         "Native PCB did not preserve the exact checked route group before-state" );
        }
    }

    if( request.has_replacement_updates() )
    {
        UpdateItems updates = request.replacement_updates();
        auto updateReply = call( updates );
        UpdateItemsResponse updated;
        if( !updateReply || updateReply->status().status() != ApiStatusCode::AS_OK
            || !updateReply->message().UnpackTo( &updated )
            || updated.status() != ItemRequestStatus::IRS_OK
            || updated.updated_items_size() != updates.items_size()
            || std::any_of( updated.updated_items().begin(), updated.updated_items().end(),
                    []( const auto& item )
                    {
                        return !item.has_status() || item.status().code() != ItemStatusCode::ISC_OK
                            || !item.has_item();
                    } ) )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Native PCB rejected replacement updates and transaction cleanup was not confirmed" );
            return fail( CheckedPcbRouteReceipt::REJECTED, "replacement_updates_rejected", "Native PCB rejected one or more identity-preserving route updates; transaction was dropped" );
        }

        // UpdateItems is expected to preserve the existing identity. A native
        // response with a different route UUID is unsafe to report as an
        // identity-preserving replacement even if the transaction itself was
        // accepted.
        for( int index = 0; index < updates.items_size(); ++index )
        {
            const std::string expectedId = RouteItemId( updates.items( index ) );
            const auto& actual = updated.updated_items( index );
            const std::string actualId = actual.has_item() ? RouteItemId( actual.item() ) : std::string();
            if( expectedId.empty() || expectedId != actualId
                || !actual.has_item()
                || !RouteItemEquivalent( updates.items( index ), actual.item() ) )
            {
                EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
                drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
                auto dropped = call( drop );
                if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                    return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Native PCB changed replacement geometry and transaction cleanup was not confirmed" );
                return fail( CheckedPcbRouteReceipt::REJECTED, "replacement_geometry_mismatch", "Native PCB returned different replacement geometry or route type; transaction was dropped" );
            }
        }

        stored.mutable_updated()->CopyFrom( updates );
        stored.mutable_updated_result()->CopyFrom( updated );
    }

    if( request.has_removals() )
    {
        auto removalReply = call( request.removals() ); DeleteItemsResponse removed;
        std::set<std::string> expectedIds;
        for( const auto& id : request.removals().item_ids() ) expectedIds.insert( id.value() );
        bool exact = removalReply && removalReply->status().status() == ApiStatusCode::AS_OK
                     && removalReply->message().UnpackTo( &removed ) && removed.status() == IRS_OK
                     && removed.deleted_items_size() == request.removals().item_ids_size();
        if( exact )
            for( const auto& item : removed.deleted_items() )
                if( item.status() != IDS_OK || !item.has_id() || expectedIds.erase( item.id().value() ) != 1 )
                    exact = false;
        if( !exact || !expectedIds.empty() )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Native route removals were not proven and cleanup was not confirmed" );
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_removals_rejected", "Native route removals did not match the requested copper; transaction was dropped" );
        }
        stored.mutable_removals()->CopyFrom( request.removals() );
        stored.mutable_removed_result()->CopyFrom( removed );
    }

    CreateItems create = request.candidate();
    CreateItemsResponse created;
    API_RESULT createReply;
    if( create.items_size() == 0 )
    {
        created.set_status( IRS_OK ); created.mutable_header()->CopyFrom( create.header() );
        ApiResponse empty; empty.mutable_status()->set_status( ApiStatusCode::AS_OK );
        empty.mutable_message()->PackFrom( created ); createReply = empty;
    }
    else
        createReply = call( create );
    if( !createReply || createReply->status().status() != ApiStatusCode::AS_OK || !createReply->message().UnpackTo( &created )
        || created.status() != IRS_OK || created.created_items_size() != create.items_size()
        || std::any_of( created.created_items().begin(), created.created_items().end(),
                []( const auto& item )
                { return !item.has_status() || item.status().code() != ISC_OK || !item.has_item(); } ) )
    {
        EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() ); drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
        auto dropped = call( drop );
        if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
            return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Native PCB rejected route items and transaction cleanup was not confirmed" );
        return fail( CheckedPcbRouteReceipt::REJECTED, "route_items_rejected", "Native PCB rejected one or more route items; transaction was dropped" );
    }
    for( int index = 0; index < create.items_size(); ++index )
    {
        if( !RouteItemEquivalent( create.items( index ), created.created_items( index ).item() ) )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Native PCB returned different route geometry and transaction cleanup was not confirmed" );
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_geometry_mismatch", "Native PCB returned different route geometry or route type; transaction was dropped" );
        }
    }
    if( request.group_changes_size() > 0 )
    {
        UpdateItems applyGroups;
        applyGroups.mutable_header()->CopyFrom( request.candidate().header() );
        for( const auto& change : request.group_changes() )
            applyGroups.add_items()->PackFrom( change.expected_after().group() );
        auto groupReply = call( applyGroups );
        UpdateItemsResponse groups;
        if( !groupReply || groupReply->status().status() != ApiStatusCode::AS_OK
            || !groupReply->message().UnpackTo( &groups ) || !GroupResponseExact( groups, applyGroups ) )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            drop.mutable_id()->CopyFrom( beginState.id() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown",
                             "Native PCB rejected the checked route group after-state and cleanup was not confirmed" );
            return fail( CheckedPcbRouteReceipt::REJECTED, "route_group_after_rejected",
                         "Native PCB did not accept the exact checked route group after-state" );
        }
    }
    EndCommit commit; commit.mutable_header()->CopyFrom( request.candidate().header() ); commit.mutable_id()->CopyFrom( beginState.id() ); commit.set_action( CMA_COMMIT );
    auto commitReply = call( commit );
    if( !commitReply || commitReply->status().status() != ApiStatusCode::AS_OK ) return fail( CheckedPcbRouteReceipt::INDETERMINATE, "commit_result_unknown", "Native route commit may have been applied; inspect its receipt" );
    commitOpen = false;
    stored.mutable_created()->CopyFrom( created );
    auto afterReply = call( stateRequest );
    if( !afterReply || afterReply->status().status() != ApiStatusCode::AS_OK || !afterReply->message().UnpackTo( stored.mutable_after() ) )
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "post_observation_failed", "Route committed but the resulting state could not be observed" );
    if( !MessageDifferencer::Equals( stored.after().document(), before.document() ) || stored.after().process_epoch() != epoch
        || stored.after().revision().sequence() <= before.revision().sequence()
        || !stored.after().project_settings_included() || !Digest( stored.after().state_sha256() )
        || !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( stored.after() ) )
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "post_state_mismatch", "Route commit result did not advance the expected board state" );
    if( !readGroups( false ) )
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "route_group_readback_failed",
                     "Route committed but exact group membership could not be read back" );
    for( int index = 0; index < request.group_changes_size(); ++index )
    {
        auto* state = stored.mutable_after_group_states( index );
        auto found = groupReadback.find( state->group().id().value() );
        if( found == groupReadback.end() )
            return fail( CheckedPcbRouteReceipt::INDETERMINATE, "route_group_readback_failed",
                         "Route committed but a checked group identity disappeared" );
        state->mutable_group()->CopyFrom( CanonicalGroup( found->second ) );
    }
    if( stored.ByteSizeLong() > resultBudget )
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "route_receipt_too_large", "Native route receipt exceeded its bounded result allowance" );
    stored.set_state( CheckedPcbRouteReceipt::COMPLETED ); stored.clear_error_code(); stored.clear_error_message();
    return Pack( stored );
    }
    catch( const std::exception& error )
    {
        if( commitOpen )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() );
            if( beginId.ByteSizeLong() > 0 ) drop.mutable_id()->CopyFrom( beginId );
            drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", error.what() );
            commitOpen = false;
        }
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "checked_route_exception", error.what() );
    }
    catch( ... )
    {
        if( commitOpen )
        {
            EndCommit drop; drop.mutable_header()->CopyFrom( request.candidate().header() ); drop.set_action( CMA_DROP );
            auto dropped = call( drop );
            if( !dropped || dropped->status().status() != ApiStatusCode::AS_OK )
                return fail( CheckedPcbRouteReceipt::INDETERMINATE, "drop_result_unknown", "Unexpected native checked route failure" );
            commitOpen = false;
        }
        return fail( CheckedPcbRouteReceipt::INDETERMINATE, "checked_route_exception", "Unexpected native checked route failure" );
    }
}
