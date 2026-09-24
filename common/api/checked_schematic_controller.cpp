/* Exact-state admission around the existing native schematic batch. GPL-3.0-or-later. */
#include <api/checked_schematic_controller.h>
#include <api/document_lifecycle_controller.h>
#include <google/protobuf/util/message_differencer.h>
#include <algorithm>
#include <exception>

namespace
{
using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

bool Uuid( const std::string& value )
{
    if( value.size() != 36 ) return false;
    bool nonzero = false;
    for( size_t i = 0; i < value.size(); ++i )
    {
        const char c = value[i];
        if( i == 8 || i == 13 || i == 18 || i == 23 ) { if( c != '-' ) return false; }
        else
        {
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

API_RESULT Error( const std::string& message )
{
    ApiResponseStatus error;
    error.set_status( ApiStatusCode::AS_BAD_REQUEST );
    error.set_error_message( message );
    return tl::unexpected( error );
}

ApiResponse Pack( const CheckedSchematicBatchReceipt& result )
{
    ApiResponse response;
    response.mutable_status()->set_status( ApiStatusCode::AS_OK );
    response.mutable_message()->PackFrom( result );
    return response;
}

bool ValidRequest( const CheckedSchematicBatch& request, const std::string& processEpoch )
{
    const auto& batch = request.batch();
    const auto& state = request.expected_state();
    return request.has_batch() && request.has_expected_state()
            && Uuid( batch.operation_id() ) && Uuid( batch.document_epoch() )
            && batch.operations_size() > 0 && batch.has_expected_revision()
            && batch.document().type() == kiapi::common::types::DOCTYPE_SCHEMATIC
            && batch.document().has_sheet_path() && batch.document().sheet_path().path_size() > 0
            && state.process_epoch() == processEpoch && Uuid( processEpoch )
            && state.scope() == DLS_SCHEMATIC_HIERARCHY && state.project_settings_included()
            && Uuid( state.native_identity() ) && Digest( state.state_sha256() )
            && state.revision().epoch() == batch.document_epoch()
            && MessageDifferencer::Equals( batch.document(), state.document() )
            && MessageDifferencer::Equals( batch.expected_revision(), state.revision() );
}
}

const std::vector<std::string>& CHECKED_SCHEMATIC_CONTROLLER::RequestTypes()
{
    static const std::vector<std::string> types = []()
    {
        std::vector<std::string> names{ std::string( CheckedSchematicBatch::descriptor()->full_name() ),
                                        std::string( ReadCheckedSchematicBatchReceipt::descriptor()->full_name() ),
                                        std::string( ReadCheckedSchematicState::descriptor()->full_name() ) };
        std::sort( names.begin(), names.end() );
        return names;
    }();
    return types;
}

bool CHECKED_SCHEMATIC_CONTROLLER::Handles( const ApiRequest& request )
{
    // Same rule as Any::Is<T>(): the full message name follows the last '/'.
    const std::string& url = request.message().type_url();
    const size_t slash = url.rfind( '/' );
    if( slash == std::string::npos ) return false;
    const auto& types = RequestTypes();
    return std::binary_search( types.begin(), types.end(), url.substr( slash + 1 ) );
}

std::optional<std::string> CHECKED_SCHEMATIC_CONTROLLER::PrepareConnectivityAssertion(
        const ApplySchematicItemBatch& batch,
        const std::function<bool( const kiapi::common::types::SheetPath& )>& loadedPath,
        CONNECTIVITY_ASSERTION& assertion )
{
    assertion = {};
    auto reject = []( int index, const char* message )
    { return fmt::format( "Atomic operation {} rejected: {}", index, message ); };
    int found = -1;
    for( int index = 0; index < batch.operations_size(); ++index )
    {
        if( !batch.operations( index ).has_assert_connectivity() ) continue;
        if( found >= 0 ) return reject( index, "A batch can contain at most one connectivity assertion" );
        found = index;
    }
    if( found < 0 ) return std::nullopt;
    if( found != batch.operations_size() - 1 )
        return reject( found, "A connectivity assertion must be the last operation of its batch" );
    const auto& operation = batch.operations( found );
    if( operation.has_target_document() )
        return reject( found, "A connectivity assertion covers the whole batch and cannot name a target document" );
    if( batch.operation_id().empty() || !batch.has_expected_revision() )
        return reject( found, "A connectivity assertion requires revision and retry identity" );
    for( int index = 0; index < found; ++index )
    {
        const auto kind = batch.operations( index ).operation_case();
        if( kind != SchematicItemOperation::kCreate && kind != SchematicItemOperation::kUpdate
                && kind != SchematicItemOperation::kReplaceLibraryCache )
            return reject( index, "Only create, update and library cache operations can share a batch with a connectivity assertion" );
    }
    const auto& request = operation.assert_connectivity();
    auto known = request;
    known.DiscardUnknownFields();
    if( !MessageDifferencer::Equals( known, request ) )
        return reject( found, "The connectivity assertion contains unsupported fields" );
    if( request.version() != 1 )
        return reject( found, "Only connectivity assertion version 1 is supported" );
    std::map<std::string, bool> loaded;
    std::set<std::string> seen;
    for( const auto& group : request.expected_groups() )
    {
        if( group.pins().empty() )
            return reject( found, "An expected connectivity group cannot be empty" );
        PIN_GROUP keys;
        for( const auto& pin : group.pins() )
        {
            std::string path;
            for( const auto& id : pin.path().path() )
            {
                if( !Uuid( id.value() ) ) { path.clear(); break; }
                path += "/" + id.value();
            }
            if( path.empty() || !Uuid( pin.pin().value() ) )
                return reject( found, "Expected pins require canonical sheet-instance paths and placed pin UUIDs" );
            auto cached = loaded.find( path );
            if( cached == loaded.end() ) cached = loaded.emplace( path, loadedPath( pin.path() ) ).first;
            if( !cached->second )
                return reject( found, "An expected pin path is not a loaded sheet instance" );
            std::string key = path + "#" + pin.pin().value();
            if( !seen.insert( key ).second )
                return reject( found, "An expected pin can appear only once in a connectivity assertion" );
            keys.insert( std::move( key ) );
        }
        assertion.expected.push_back( std::move( keys ) );
    }
    assertion.index = found;
    return std::nullopt;
}

std::optional<std::string> CHECKED_SCHEMATIC_CONTROLLER::CheckConnectivity( const PIN_PARTITION& before,
        const PIN_PARTITION& after, const std::vector<PIN_GROUP>& expected )
{
    std::set<std::string> asserted;
    for( const PIN_GROUP& group : expected ) asserted.insert( group.begin(), group.end() );
    auto touches = [&]( const PIN_GROUP& group )
    {
        return std::any_of( group.begin(), group.end(), [&]( const std::string& pin ) { return asserted.count( pin ) != 0; } );
    };
    PIN_PARTITION required( expected.begin(), expected.end() );
    for( const PIN_GROUP& group : before )
        if( !touches( group ) ) required.insert( group );
    size_t mismatches = 0;
    for( const PIN_GROUP& group : required ) mismatches += after.count( group ) ? 0 : 1;
    for( const PIN_GROUP& group : after ) mismatches += required.count( group ) ? 0 : 1;
    if( mismatches == 0 ) return std::nullopt;

    // Report the first difference deterministically: asserted groups in request order,
    // then untouched native groups that changed, then new groups nobody asserted.
    std::map<std::string, const PIN_GROUP*> home;
    for( const PIN_GROUP& group : after )
        for( const std::string& pin : group ) home.emplace( pin, &group );
    std::string category;
    std::vector<std::string> keys;
    for( const PIN_GROUP& group : expected )
    {
        std::set<const PIN_GROUP*> homes;
        for( const std::string& pin : group )
        {
            auto found = home.find( pin );
            if( found == home.end() ) keys.push_back( pin );
            else homes.insert( found->second );
        }
        if( !keys.empty() ) { category = "missing_pin"; break; }
        const PIN_GROUP* first = home.at( *group.begin() );
        if( homes.size() > 1 )
        {
            category = "unexpected_split";
            for( const std::string& pin : group )
                if( home.at( pin ) != first ) keys.push_back( pin );
            break;
        }
        if( *first != group )
        {
            category = "unexpected_join";
            for( const std::string& pin : *first )
                if( !group.count( pin ) ) keys.push_back( pin );
            break;
        }
    }
    if( category.empty() )
    {
        for( const PIN_GROUP& group : before )
            if( !touches( group ) && !after.count( group ) )
            { category = "unaffected_group_changed"; keys.assign( group.begin(), group.end() ); break; }
    }
    if( category.empty() )
    {
        for( const PIN_GROUP& group : after )
            if( !touches( group ) && !required.count( group ) )
            { category = "unaffected_group_changed"; keys.assign( group.begin(), group.end() ); break; }
    }
    constexpr size_t limit = 2048, reserve = 32;
    std::string message = fmt::format( "{}: expected={} mismatches={} first={}:", CONNECTIVITY_FAILURE,
                                       expected.size(), mismatches, category );
    for( size_t index = 0; index < keys.size(); ++index )
    {
        std::string next = ( index ? "," : "" ) + keys[index];
        if( message.size() + next.size() + reserve > limit )
        {
            message += fmt::format( "{}+{} more", index ? "," : "", keys.size() - index );
            break;
        }
        message += next;
    }
    return message;
}

API_RESULT CHECKED_SCHEMATIC_CONTROLLER::ReadState( ApiRequest& envelope,
        const std::string& processEpoch, const DISPATCH& dispatch )
{
    ReadCheckedSchematicState request;
    if( !envelope.message().UnpackTo( &request ) || request.process_epoch() != processEpoch
            || !Uuid( processEpoch ) || request.document().type() != kiapi::common::types::DOCTYPE_SCHEMATIC
            || request.document().sheet_path().path_size() != 1
            || !Uuid( request.document().sheet_path().path( 0 ).value() ) )
        return Error( "Combined schematic state requires an exact root document and process epoch" );
    auto call = [&]( const google::protobuf::Message& message )
    {
        ApiRequest query; query.mutable_header()->CopyFrom( envelope.header() );
        query.mutable_message()->PackFrom( message ); return dispatch( query );
    };
    ReadDocumentLifecycleState stateQuery; stateQuery.mutable_document()->CopyFrom( request.document() );
    ReadSchematicElectricalState electricalQuery; electricalQuery.mutable_document()->CopyFrom( request.document() );
    // Zero selects legacy projection and drops current project settings. The
    // planner and this combined capture must describe the same supported schema.
    electricalQuery.set_schema_version( 9 );
    try
    {
        auto beforeReply = call( stateQuery );
        if( !beforeReply ) return beforeReply;
        DocumentLifecycleState before;
        if( beforeReply->status().status() != ApiStatusCode::AS_OK || !beforeReply->message().UnpackTo( &before ) )
            return Error( "Initial combined-state observation failed" );
        auto electricalReply = call( electricalQuery );
        if( !electricalReply ) return electricalReply;
        SchematicElectricalState electrical;
        if( electricalReply->status().status() != ApiStatusCode::AS_OK || !electricalReply->message().UnpackTo( &electrical ) )
            return Error( "Combined electrical observation failed" );
        auto afterReply = call( stateQuery );
        if( !afterReply ) return afterReply;
        DocumentLifecycleState after;
        if( afterReply->status().status() != ApiStatusCode::AS_OK || !afterReply->message().UnpackTo( &after )
                || !MessageDifferencer::Equals( before, after )
                || !MessageDifferencer::Equals( before.document(), request.document() )
                || !MessageDifferencer::Equals( electrical.hierarchy().data().document(), request.document() )
                || !MessageDifferencer::Equals( electrical.hierarchy().revision(), before.revision() )
                || before.process_epoch() != processEpoch || before.scope() != DLS_SCHEMATIC_HIERARCHY
                || !before.project_settings_included() || !Uuid( before.native_identity() )
                || !Uuid( before.revision().epoch() ) || !Digest( before.state_sha256() ) )
            return Error( "Schematic state changed during combined capture; discard the observation" );
        CheckedSchematicState result;
        result.mutable_state()->Swap( &after ); result.mutable_electrical()->Swap( &electrical );
        ApiResponse response; response.mutable_status()->set_status( ApiStatusCode::AS_OK );
        response.mutable_message()->PackFrom( result ); return response;
    }
    catch( const std::exception& error ) { return Error( std::string( "Combined-state observation failed: " ) + error.what() ); }
}

API_RESULT CHECKED_SCHEMATIC_CONTROLLER::Handle( ApiRequest& envelope,
        const std::string& processEpoch, const DISPATCH& dispatch )
{
    if( envelope.message().Is<ReadCheckedSchematicState>() )
        return ReadState( envelope, processEpoch, dispatch );
    if( envelope.message().Is<ReadCheckedSchematicBatchReceipt>() )
    {
        ReadCheckedSchematicBatchReceipt query;
        if( !envelope.message().UnpackTo( &query ) || !query.has_expected_request()
                || !ValidRequest( query.expected_request(), processEpoch )
                || query.process_epoch() != processEpoch
                || query.operation_id() != query.expected_request().batch().operation_id()
                || !MessageDifferencer::Equals( query.document(), query.expected_request().batch().document() ) )
            return Error( "Require the exact checked request, document and process epoch to inspect a receipt" );
        auto found = m_receipts.find( query.operation_id() );
        if( found == m_receipts.end() )
        {
            CheckedSchematicBatchReceipt result;
            result.mutable_document()->CopyFrom( query.document() );
            result.set_process_epoch( processEpoch ); result.set_operation_id( query.operation_id() );
            result.set_status( CSBS_NOT_FOUND );
            return Pack( result );
        }
        if( !MessageDifferencer::Equals( found->second.request, query.expected_request() ) )
            return Error( "Checked operation ID belongs to a different request" );
        return Pack( found->second.result );
    }

    CheckedSchematicBatch request;
    if( !envelope.message().UnpackTo( &request ) || !ValidRequest( request, processEpoch ) )
        return Error( "A checked batch requires exact native state, process identity, revision and operation UUID" );
    const auto& batch = request.batch();
    const auto existing = m_receipts.find( batch.operation_id() );
    if( existing != m_receipts.end() )
    {
        if( !MessageDifferencer::Equals( existing->second.request, request ) )
            return Error( "Checked operation ID was already used with another batch or state precondition" );
        return Pack( existing->second.result );
    }

    constexpr size_t budget = 16 * 1024 * 1024;
    constexpr size_t overhead = 16 * 1024;
    const size_t requestBytes = request.ByteSizeLong();
    if( requestBytes > ( budget - overhead ) / 6 || m_receipts.size() >= 4096 )
        return Error( "Checked batch receipt capacity exhausted; no mutation was attempted" );
    const size_t baseReservation = requestBytes * 5 + overhead;
    if( baseReservation + 4096 > budget - m_reservedBytes )
        return Error( "Checked batch receipt capacity exhausted; no mutation was attempted" );
    const size_t availableResult = budget - m_reservedBytes - baseReservation;
    const size_t resultBudget = batch.maximum_result_bytes()
            ? std::min( availableResult, size_t{ batch.maximum_result_bytes() } ) : availableResult;
    const size_t reservation = baseReservation + resultBudget;
    RECEIPT receipt;
    receipt.request = request;
    auto& initial = receipt.result;
    initial.mutable_document()->CopyFrom( batch.document() );
    initial.set_operation_id( batch.operation_id() ); initial.set_process_epoch( processEpoch );
    initial.set_status( CSBS_INDETERMINATE ); initial.set_expected_request_verified( true );
    initial.set_error_code( "operation_in_progress" );
    auto& result = m_receipts.emplace( batch.operation_id(), std::move( receipt ) ).first->second.result;
    m_reservedBytes += reservation;
    struct RESERVATION_GUARD
    {
        size_t& reserved;
        size_t reservation, requestBytes, overhead;
        const CheckedSchematicBatchReceipt& result;
        ~RESERVATION_GUARD()
        {
            // Dispatch is synchronous. Return unused room only after the
            // permanent receipt has its terminal contents, never by eviction.
            const size_t retained = std::min( reservation, requestBytes + result.ByteSizeLong() + overhead );
            reserved -= reservation - retained;
        }
    } reservationGuard{ m_reservedBytes, reservation, requestBytes, overhead, result };
    bool dispatched = false;
    auto fail = [&]( CheckedSchematicBatchStatus status, const char* code, const std::string& message ) -> API_RESULT
    {
        result.set_status( status ); result.set_error_code( code );
        result.set_error_message( message.substr( 0, 2048 ) );
        return Pack( result );
    };
    auto observe = [&]() -> HANDLER_RESULT<DocumentLifecycleState>
    {
        ReadDocumentLifecycleState read;
        read.mutable_document()->CopyFrom( batch.document() );
        ApiRequest query;
        query.mutable_header()->CopyFrom( envelope.header() ); query.mutable_message()->PackFrom( read );
        auto response = dispatch( query );
        if( !response ) return tl::unexpected( response.error() );
        if( response->status().status() != ApiStatusCode::AS_OK ) return tl::unexpected( response->status() );
        DocumentLifecycleState state;
        if( !response->message().UnpackTo( &state ) || state.ByteSizeLong() > requestBytes * 2 + 1024 )
        {
            ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Native state is invalid or exceeds the checked receipt reservation" );
            return tl::unexpected( error );
        }
        return state;
    };
    try
    {
        auto before = observe();
        if( !before ) return fail( CSBS_REJECTED, "observation_failed", before.error().error_message() );
        result.mutable_observed_before()->CopyFrom( *before );
        if( !MessageDifferencer::Equals( request.expected_state(), *before ) )
            return fail( CSBS_REJECTED, "stale_document_state", "Document or disk state changed; no batch was applied" );
        if( !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( *before ) )
            return fail( CSBS_REJECTED, "file_baseline_conflict", "The loaded file baselines are incomplete or differ from disk" );

        // API dispatch is synchronous on the owning GUI thread. Do not yield to
        // other editor events between this observation and the native commit.
        ApiRequest mutation;
        auto limitedBatch = batch;
        limitedBatch.set_maximum_result_bytes( static_cast<uint32_t>( resultBudget ) );
        mutation.mutable_header()->CopyFrom( envelope.header() ); mutation.mutable_message()->PackFrom( limitedBatch );
        const bool asserted = std::any_of( batch.operations().begin(), batch.operations().end(),
                []( const SchematicItemOperation& operation ) { return operation.has_assert_connectivity(); } );
        dispatched = true;
        auto applied = dispatch( mutation );
        if( !applied || applied->status().status() != ApiStatusCode::AS_OK )
        {
            const std::string message = applied ? applied->status().error_message() : applied.error().error_message();
            auto rejectedState = observe();
            if( rejectedState ) result.mutable_observed_after()->CopyFrom( *rejectedState );
            const bool unchanged = rejectedState && MessageDifferencer::Equals( *before, *rejectedState );
            // CN-1 §8.2: only an asserted batch can fail its post-condition, and only an
            // unchanged document proves that nothing of it was committed.
            if( unchanged && asserted && message.rfind( std::string( CONNECTIVITY_FAILURE ) + ":", 0 ) == 0 )
                return fail( CSBS_REJECTED, CONNECTIVITY_FAILURE, message );
            return fail( unchanged ? CSBS_REJECTED : CSBS_INDETERMINATE,
                         unchanged ? "native_batch_rejected" : "native_batch_failed_unverified", message );
        }
        SchematicItemBatchResult nativeResult;
        if( !applied->message().UnpackTo( &nativeResult )
                || nativeResult.ByteSizeLong() > resultBudget
                || nativeResult.revision().epoch() != batch.document_epoch()
                || nativeResult.revision().sequence() < batch.expected_revision().sequence() )
            return fail( CSBS_INDETERMINATE, "invalid_native_result", "Native execution may have occurred; inspect the saved request and document" );
        result.mutable_result()->CopyFrom( nativeResult );
        auto after = observe();
        if( !after ) return fail( CSBS_INDETERMINATE, "post_observation_failed", after.error().error_message() );
        result.mutable_observed_after()->CopyFrom( *after );
        if( after->native_identity() != before->native_identity() || after->process_epoch() != processEpoch
                || !MessageDifferencer::Equals( after->document(), before->document() )
                || !MessageDifferencer::Equals( after->revision(), nativeResult.revision() )
                || after->scope() != before->scope() || !Digest( after->state_sha256() )
                || !after->project_settings_included()
                || !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( *after ) )
            return fail( CSBS_INDETERMINATE, "post_state_mismatch", "The resulting document does not match the committed batch receipt" );
        if( asserted && !nativeResult.connectivity_assertion_verified() )
            return fail( CSBS_INDETERMINATE, "connectivity_assertion_unverified",
                         "The batch committed without proving its asserted pin connections; inspect the document" );
        if( !asserted && nativeResult.connectivity_assertion_verified() )
            return fail( CSBS_INDETERMINATE, "invalid_native_result",
                         "Native execution claimed a connectivity proof the batch did not request" );
        result.set_status( CSBS_COMPLETED ); result.clear_error_code(); result.clear_error_message();
        return Pack( result );
    }
    catch( const std::exception& error )
    {
        return fail( dispatched ? CSBS_INDETERMINATE : CSBS_REJECTED,
                     "checked_batch_exception", error.what() );
    }
    catch( ... )
    {
        return fail( dispatched ? CSBS_INDETERMINATE : CSBS_REJECTED,
                     "checked_batch_exception", "Unexpected native checked-batch failure" );
    }
}
