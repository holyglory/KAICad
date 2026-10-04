/* Exact-state admission around the existing native PCB item transaction. GPL-3.0-or-later. */
#include <api/checked_pcb_item_controller.h>

#include <api/document_lifecycle_controller.h>
#include <api/common/commands/editor_commands.pb.h>
#include <google/protobuf/descriptor.h>
#include <google/protobuf/dynamic_message.h>
#include <google/protobuf/util/message_differencer.h>

#include <algorithm>
#include <exception>
#include <memory>
#include <optional>
#include <set>

namespace
{
using namespace kiapi::automation::v1;
using namespace kiapi::common::commands;
using namespace kiapi::common::types;
using google::protobuf::util::MessageDifferencer;

constexpr size_t MAX_REQUEST_BYTES = 4 * 1024 * 1024;
constexpr size_t MAX_RESULT_BYTES = 4 * 1024 * 1024;
constexpr size_t MIN_RESULT_BYTES = 64 * 1024;
constexpr size_t MAX_RECEIPTS = 256;

bool Uuid( const std::string& value )
{
    if( value.size() != 36 ) return false;

    bool nonzero = false;

    for( size_t i = 0; i < value.size(); ++i )
    {
        if( i == 8 || i == 13 || i == 18 || i == 23 )
        {
            if( value[i] != '-' ) return false;
        }
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
    return value.size() == 64
           && std::all_of( value.begin(), value.end(), []( char c )
           {
               return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' );
           } );
}

API_RESULT Error( const std::string& message )
{
    ApiResponseStatus status;
    status.set_status( ApiStatusCode::AS_BAD_REQUEST );
    status.set_error_message( message );
    return tl::unexpected( status );
}

API_RESULT Pack( const CheckedPcbItemTransactionReceipt& receipt )
{
    ApiResponse response;
    response.mutable_status()->set_status( ApiStatusCode::AS_OK );
    response.mutable_message()->PackFrom( receipt );
    return response;
}

const ItemHeader* HeaderFor( const PcbItemOperation& operation )
{
    if( operation.has_create() )
        return operation.create().has_header() ? &operation.create().header() : nullptr;

    if( operation.has_update() )
        return operation.update().has_header() ? &operation.update().header() : nullptr;

    if( operation.has_delete_() )
        return operation.delete_().has_header() ? &operation.delete_().header() : nullptr;

    return nullptr;
}

const DocumentSpecifier* DocumentFor( const CheckedPcbItemTransaction& request )
{
    if( request.operations_size() == 0 ) return nullptr;

    const ItemHeader* header = HeaderFor( request.operations( 0 ) );
    return header && header->has_document() ? &header->document() : nullptr;
}

bool ValidOperation( const PcbItemOperation& operation, const DocumentSpecifier& document )
{
    const ItemHeader* header = HeaderFor( operation );

    if( !header || !header->has_document()
        || !MessageDifferencer::Equals( header->document(), document ) )
        return false;

    if( operation.has_create() )
        return operation.create().items_size() > 0;

    if( operation.has_update() )
        return operation.update().items_size() > 0;

    if( operation.has_delete_() )
    {
        if( operation.delete_().item_ids_size() == 0 ) return false;

        for( const auto& id : operation.delete_().item_ids() )
            if( !Uuid( id.value() ) ) return false;

        return true;
    }

    return false;
}

// Extract a canonical KIID from Any-packed board items when their protobuf
// schema exposes the standard `id.value` field. Library-definition messages
// with another identifier shape remain subject to the native type validator.
std::optional<std::string> ItemIdentity( const google::protobuf::Any& packed )
{
    std::string type;

    if( !google::protobuf::Any::ParseAnyTypeUrl( packed.type_url(), &type ) ) return std::nullopt;

    const auto* descriptor = google::protobuf::DescriptorPool::generated_pool()->FindMessageTypeByName( type );

    if( !descriptor ) return std::nullopt;

    google::protobuf::DynamicMessageFactory factory;
    const auto* prototype = factory.GetPrototype( descriptor );

    if( !prototype ) return std::nullopt;

    std::unique_ptr<google::protobuf::Message> decoded( prototype->New() );

    if( !packed.UnpackTo( decoded.get() ) ) return std::nullopt;

    const auto* idField = descriptor->FindFieldByName( "id" );

    if( !idField || idField->cpp_type() != google::protobuf::FieldDescriptor::CPPTYPE_MESSAGE )
        return std::nullopt;

    const auto* reflection = decoded->GetReflection();

    if( !reflection->HasField( *decoded, idField ) ) return std::string();

    const auto& idMessage = reflection->GetMessage( *decoded, idField );
    const auto* valueField = idMessage.GetDescriptor()->FindFieldByName( "value" );

    if( !valueField || valueField->cpp_type() != google::protobuf::FieldDescriptor::CPPTYPE_STRING )
        return std::string();

    const std::string value = idMessage.GetReflection()->GetString( idMessage, valueField );
    return Uuid( value ) ? std::optional<std::string>( value ) : std::string();
}

bool Valid( const CheckedPcbItemTransaction& request, const std::string& processEpoch,
            bool requireFileCoverage )
{
    if( !request.has_expected_state() || request.operations_size() == 0
        || !Uuid( request.process_epoch() ) || request.process_epoch() != processEpoch
        || !Uuid( request.operation_id() ) || !Uuid( processEpoch ) )
        return false;

    const auto& state = request.expected_state();
    const DocumentSpecifier* document = DocumentFor( request );

    if( !document || !MessageDifferencer::Equals( *document, state.document() )
        || state.process_epoch() != processEpoch || state.scope() != DLS_PCB
        || state.document().type() != DOCTYPE_PCB
        || state.document().board_filename().empty() || !state.document().has_project()
        || state.document().project().name().empty() || state.document().project().path().empty()
        || !state.project_settings_included() || !state.has_revision()
        || !Uuid( state.native_identity() ) || !Digest( state.state_sha256() )
        || state.revision().epoch() != state.native_identity()
        || ( requireFileCoverage && !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( state ) ) )
        return false;

    std::set<std::string> createdIds, updatedIds, removedIds;

    for( const auto& operation : request.operations() )
    {
        if( !ValidOperation( operation, *document ) ) return false;

        auto checkItems = [&]( const auto& items, std::set<std::string>& local,
                               const std::set<std::string>& otherA,
                               const std::set<std::string>& otherB )
        {
            for( const auto& packed : items )
            {
                const auto identity = ItemIdentity( packed );

                // A standard board item must carry a canonical identity. A
                // library-definition payload has no KIID value and remains
                // subject to the native type-specific validator.
                if( identity && identity->empty() ) return false;

                if( identity && ( !local.insert( *identity ).second
                                  || otherA.contains( *identity ) || otherB.contains( *identity ) ) )
                    return false;
            }

            return true;
        };

        if( operation.has_create()
            && !checkItems( operation.create().items(), createdIds, updatedIds, removedIds ) )
            return false;

        if( operation.has_update()
            && !checkItems( operation.update().items(), updatedIds, createdIds, removedIds ) )
            return false;

        if( operation.has_delete_() )
        {
            for( const auto& id : operation.delete_().item_ids() )
                if( !removedIds.insert( id.value() ).second || createdIds.contains( id.value() )
                    || updatedIds.contains( id.value() ) )
                    return false;
        }
    }

    if( request.maximum_result_bytes() != 0
        && ( request.maximum_result_bytes() < MIN_RESULT_BYTES
             || request.maximum_result_bytes() > MAX_RESULT_BYTES ) )
        return false;

    return request.ByteSizeLong() <= MAX_REQUEST_BYTES;
}

std::string ReplyError( const API_RESULT& reply )
{
    return reply ? std::string() : reply.error().error_message();
}

bool ItemResultIdentityMatches( const google::protobuf::Any& expected,
                                const google::protobuf::Any& actual )
{
    std::string expectedType;
    std::string actualType;

    if( !google::protobuf::Any::ParseAnyTypeUrl( expected.type_url(), &expectedType )
        || !google::protobuf::Any::ParseAnyTypeUrl( actual.type_url(), &actualType )
        || expectedType != actualType )
        return false;

    const auto expectedId = ItemIdentity( expected );
    const auto actualId = ItemIdentity( actual );
    return !expectedId || ( actualId && *expectedId == *actualId );
}

bool CreateResultOk( const CreateItems& request, const CreateItemsResponse& response )
{
    return response.status() == IRS_OK && response.created_items_size() == request.items_size()
           && std::all_of( response.created_items().begin(), response.created_items().end(),
                           []( const ItemCreationResult& result )
                           {
                               return result.has_status() && result.status().code() == ISC_OK
                                      && result.has_item();
                           } )
           && std::equal( response.created_items().begin(), response.created_items().end(),
                          request.items().begin(),
                          []( const ItemCreationResult& result, const google::protobuf::Any& expected )
                          {
                              return result.has_item() && ItemResultIdentityMatches( expected, result.item() );
                          } );
}

bool UpdateResultOk( const UpdateItems& request, const UpdateItemsResponse& response )
{
    return response.status() == IRS_OK && response.updated_items_size() == request.items_size()
           && std::all_of( response.updated_items().begin(), response.updated_items().end(),
                           []( const ItemUpdateResult& result )
                           {
                               return result.has_status() && result.status().code() == ISC_OK
                                      && result.has_item();
                           } )
           && std::equal( response.updated_items().begin(), response.updated_items().end(),
                          request.items().begin(),
                          []( const ItemUpdateResult& result, const google::protobuf::Any& expected )
                          {
                              return result.has_item() && ItemResultIdentityMatches( expected, result.item() );
                          } );
}

bool DeleteResultOk( const DeleteItems& request, const DeleteItemsResponse& response )
{
    if( response.status() != IRS_OK || response.deleted_items_size() != request.item_ids_size() )
        return false;

    std::set<std::string> expected;

    for( const auto& id : request.item_ids() ) expected.insert( id.value() );

    for( const auto& result : response.deleted_items() )
    {
        if( result.status() != IDS_OK || !result.has_id() || !expected.erase( result.id().value() ) )
            return false;
    }

    return expected.empty()
           && std::all_of( response.deleted_items().begin(), response.deleted_items().end(),
                           []( const ItemDeletionResult& result )
                           {
                               return result.status() == IDS_OK && result.has_id();
                           } );
}

// BeginCommit/EndCommit may advance the lifecycle sequence even when the
// staged PCB is dropped. The content digest, document identity and complete
// file baselines are the rollback proof; revision sequence is an observation
// counter and is deliberately ignored here.
bool RollbackEquivalent( const DocumentLifecycleState& left,
                         const DocumentLifecycleState& right )
{
    if( left.process_epoch() != right.process_epoch()
        || !MessageDifferencer::Equals( left.document(), right.document() )
        || left.native_identity() != right.native_identity()
        || left.state_sha256() != right.state_sha256()
        || left.project_settings_included() != right.project_settings_included() )
        return false;

    // A dropped native commit can refresh the lifecycle's revision/file
    // observation metadata even when the board content is unchanged. Those
    // observations are not design mutations and must not turn a confirmed
    // rollback into an indeterminate receipt.
    DocumentLifecycleState normalizedLeft = left;
    DocumentLifecycleState normalizedRight = right;
    normalizedLeft.clear_revision();
    normalizedRight.clear_revision();
    normalizedLeft.clear_native_files();
    normalizedRight.clear_native_files();
    normalizedLeft.clear_file_baselines();
    normalizedRight.clear_file_baselines();
    return MessageDifferencer::Equals( normalizedLeft, normalizedRight );
}
} // namespace

const std::vector<std::string>& CHECKED_PCB_ITEM_CONTROLLER::RequestTypes()
{
    static const std::vector<std::string> types = []
    {
        std::vector<std::string> result{
            std::string( CheckedPcbItemTransaction::descriptor()->full_name() ),
            std::string( ReadCheckedPcbItemTransactionReceipt::descriptor()->full_name() ) };
        std::sort( result.begin(), result.end() );
        return result;
    }();

    return types;
}

bool CHECKED_PCB_ITEM_CONTROLLER::Handles( const ApiRequest& request )
{
    std::string type;

    if( !google::protobuf::Any::ParseAnyTypeUrl( request.message().type_url(), &type ) )
        return false;

    const auto& types = RequestTypes();
    return std::binary_search( types.begin(), types.end(), type );
}

API_RESULT CHECKED_PCB_ITEM_CONTROLLER::Handle( ApiRequest& envelope,
        const std::string& processEpoch, const DISPATCH& dispatch )
{
    if( envelope.message().Is<ReadCheckedPcbItemTransactionReceipt>() )
    {
        ReadCheckedPcbItemTransactionReceipt query;

        if( !envelope.message().UnpackTo( &query ) || !query.has_expected_request()
            || !Valid( query.expected_request(), processEpoch, false )
            || query.process_epoch() != processEpoch
            || query.operation_id() != query.expected_request().operation_id()
            || !DocumentFor( query.expected_request() )
            || !MessageDifferencer::Equals( query.document(), *DocumentFor( query.expected_request() ) ) )
            return Error( "Require the exact checked PCB item request, document and process epoch to inspect a receipt" );

        auto found = m_receipts.find( query.operation_id() );

        if( found == m_receipts.end() )
        {
            CheckedPcbItemTransactionReceipt result;
            result.mutable_document()->CopyFrom( query.document() );
            result.set_process_epoch( processEpoch );
            result.set_operation_id( query.operation_id() );
            result.set_state( CPBTS_NOT_FOUND );
            result.set_phase( CPBTP_UNKNOWN );
            result.set_rollback_state( CPBRS_NOT_ATTEMPTED );
            result.set_post_commit_state( CPBPS_NOT_ATTEMPTED );
            return Pack( result );
        }

        if( !MessageDifferencer::Equals( found->second.request, query.expected_request() ) )
            return Error( "Checked PCB item operation ID belongs to a different request" );

        return Pack( found->second.result );
    }

    CheckedPcbItemTransaction request;

    if( !envelope.message().UnpackTo( &request ) || !Valid( request, processEpoch, true ) )
        return Error( "A checked PCB item transaction requires exact board state, process identity and operation UUID" );

    auto existing = m_receipts.find( request.operation_id() );

    if( existing != m_receipts.end() )
    {
        if( !MessageDifferencer::Equals( existing->second.request, request ) )
            return Error( "Checked PCB item operation ID was already used with another request" );

        auto replay = existing->second.result;
        replay.set_replayed( true );
        return Pack( replay );
    }

    const size_t resultBudget = request.maximum_result_bytes() == 0
            ? MAX_RESULT_BYTES : request.maximum_result_bytes();

    if( m_receipts.size() >= MAX_RECEIPTS || request.ByteSizeLong() > MAX_REQUEST_BYTES
        || resultBudget < request.ByteSizeLong() * 2 + request.expected_state().ByteSizeLong() * 3 + 16 * 1024 )
        return Error( "Checked PCB item receipt capacity exhausted; no mutation was attempted" );

    RECEIPT receipt;
    receipt.request = request;
    auto& result = receipt.result;
    result.mutable_document()->CopyFrom( *DocumentFor( request ) );
    result.set_process_epoch( processEpoch );
    result.set_operation_id( request.operation_id() );
    result.set_state( CPBTS_INDETERMINATE );
    result.set_request_verified( true );
    result.set_phase( CPBTP_UNKNOWN );
    result.set_rollback_state( CPBRS_NOT_ATTEMPTED );
    result.set_post_commit_state( CPBPS_NOT_ATTEMPTED );
    result.set_error_code( "operation_in_progress" );

    m_receipts.emplace( request.operation_id(), std::move( receipt ) );
    auto& stored = m_receipts.at( request.operation_id() ).result;

    auto fail = [&]( CheckedPcbItemTransactionState state,
                     CheckedPcbItemTransactionPhase phase,
                     const char* code, const std::string& message ) -> API_RESULT
    {
        stored.set_state( state );
        stored.set_phase( phase );
        stored.set_error_code( code );
        stored.set_error_message( message.substr( 0, 2048 ) );
        return Pack( stored );
    };

    auto call = [&]( const google::protobuf::Message& message ) -> API_RESULT
    {
        ApiRequest query;
        query.mutable_header()->CopyFrom( envelope.header() );
        query.mutable_message()->PackFrom( message );
        return dispatch( query );
    };

    auto observe = [&]() -> HANDLER_RESULT<DocumentLifecycleState>
    {
        ReadDocumentLifecycleState query;
        query.mutable_document()->CopyFrom( *DocumentFor( request ) );
        API_RESULT reply = call( query );

        if( !reply ) return tl::unexpected( reply.error() );

        if( reply->status().status() != ApiStatusCode::AS_OK )
            return tl::unexpected( reply->status() );

        DocumentLifecycleState state;

        if( !reply->message().UnpackTo( &state ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Native PCB lifecycle state was not parseable" );
            return tl::unexpected( error );
        }

        return state;
    };

    std::optional<DocumentLifecycleState> before;
    kiapi::common::types::KIID beginId;
    bool commitOpen = false;
    bool nativeTouched = false;

    auto rollback = [&]() -> bool
    {
        if( !commitOpen || !before ) return false;

        stored.set_phase( CPBTP_ROLLBACK_REQUESTED );
        stored.set_rollback_state( CPBRS_REQUESTED );

        EndCommit drop;
        drop.mutable_header()->CopyFrom( *HeaderFor( request.operations( 0 ) ) );
        if( beginId.ByteSizeLong() > 0 ) drop.mutable_id()->CopyFrom( beginId );
        drop.set_action( CMA_DROP );

        API_RESULT dropReply = call( drop );
        const bool dropOk = dropReply && dropReply->status().status() == ApiStatusCode::AS_OK;

        if( dropOk ) commitOpen = false;

        auto afterRollback = observe();

        if( afterRollback ) stored.mutable_observed_rollback()->CopyFrom( *afterRollback );

        const bool unchanged = afterRollback && RollbackEquivalent( *before, *afterRollback );

        if( dropOk && unchanged )
        {
            stored.set_phase( CPBTP_ROLLBACK_CONFIRMED );
            stored.set_rollback_state( CPBRS_CONFIRMED );
            return true;
        }

        stored.set_rollback_state( CPBRS_UNCONFIRMED );
        return false;
    };

    auto rejectMutation = [&]( const char* code, const std::string& message ) -> API_RESULT
    {
        if( rollback() )
            return fail( CPBTS_ROLLED_BACK, CPBTP_ROLLBACK_CONFIRMED, code, message );

        return fail( CPBTS_INDETERMINATE, stored.phase(), "rollback_unconfirmed", message );
    };

    try
    {
        auto observed = observe();

        if( !observed )
            return fail( CPBTS_REJECTED, CPBTP_UNKNOWN, "state_observation_failed",
                         observed.error().error_message() );

        before = *observed;
        stored.mutable_observed_before()->CopyFrom( *before );

        if( !MessageDifferencer::Equals( *before, request.expected_state() ) )
            return fail( CPBTS_REJECTED, CPBTP_UNKNOWN, "stale_document_state",
                         "The PCB changed since the checked item state" );

        stored.set_phase( CPBTP_ADMISSION_VERIFIED );

        BeginCommit begin;
        begin.mutable_header()->CopyFrom( *HeaderFor( request.operations( 0 ) ) );
        nativeTouched = true;
        API_RESULT beginReply = call( begin );

        if( !beginReply || beginReply->status().status() != ApiStatusCode::AS_OK )
        {
            // A synchronous native refusal is normally a clean rejection,
            // but an internal dispatch failure does not prove that no staged
            // commit was opened. Reobserve before classifying the result.
            auto afterBeginFailure = observe();
            const bool unchanged = afterBeginFailure && before
                    && MessageDifferencer::Equals( *afterBeginFailure, *before );

            if( afterBeginFailure )
                stored.mutable_observed_after()->CopyFrom( *afterBeginFailure );

            return fail( unchanged ? CPBTS_REJECTED : CPBTS_INDETERMINATE,
                         CPBTP_ADMISSION_VERIFIED, "commit_begin_failed",
                         ReplyError( beginReply ).empty() ? "Native PCB did not open the transaction"
                                                          : ReplyError( beginReply ) );
        }

        commitOpen = true;
        stored.set_phase( CPBTP_STAGED );

        BeginCommitResponse beginState;

        if( !beginReply->message().UnpackTo( &beginState ) )
            return rejectMutation( "commit_begin_result_unknown", "Native PCB transaction admission was not parseable" );

        beginId = beginState.id();

        for( const auto& operation : request.operations() )
        {
            PcbItemOperationResult* operationResult = stored.add_results();

            if( operation.has_create() )
            {
                API_RESULT reply = call( operation.create() );
                CreateItemsResponse native;

                if( !reply || reply->status().status() != ApiStatusCode::AS_OK
                    || !reply->message().UnpackTo( &native ) )
                    return rejectMutation( "create_result_unknown", "Native PCB create response was not confirmed" );

                operationResult->mutable_create()->CopyFrom( native );

                if( !CreateResultOk( operation.create(), native ) )
                    return rejectMutation( "create_rejected", "Native PCB rejected one or more created items" );
            }
            else if( operation.has_update() )
            {
                API_RESULT reply = call( operation.update() );
                UpdateItemsResponse native;

                if( !reply || reply->status().status() != ApiStatusCode::AS_OK
                    || !reply->message().UnpackTo( &native ) )
                    return rejectMutation( "update_result_unknown", "Native PCB update response was not confirmed" );

                operationResult->mutable_update()->CopyFrom( native );

                if( !UpdateResultOk( operation.update(), native ) )
                    return rejectMutation( "update_rejected", "Native PCB rejected one or more updated items" );
            }
            else if( operation.has_delete_() )
            {
                API_RESULT reply = call( operation.delete_() );
                DeleteItemsResponse native;

                if( !reply || reply->status().status() != ApiStatusCode::AS_OK
                    || !reply->message().UnpackTo( &native ) )
                    return rejectMutation( "delete_result_unknown", "Native PCB delete response was not confirmed" );

                operationResult->mutable_delete_()->CopyFrom( native );

                if( !DeleteResultOk( operation.delete_(), native ) )
                    return rejectMutation( "delete_rejected", "Native PCB rejected one or more deleted items" );
            }

            if( stored.ByteSizeLong() > resultBudget )
                return rejectMutation( "receipt_too_large", "The checked PCB item receipt exceeded its result allowance" );
        }

        stored.set_phase( CPBTP_COMMIT_REQUESTED );
        stored.set_post_commit_state( CPBPS_REQUESTED );

        EndCommit commit;
        commit.mutable_header()->CopyFrom( *HeaderFor( request.operations( 0 ) ) );
        commit.mutable_id()->CopyFrom( beginId );
        commit.set_action( CMA_COMMIT );
        commit.set_message( request.description().empty() ? "Checked PCB item transaction" : request.description() );

        API_RESULT commitReply = call( commit );

        if( !commitReply || commitReply->status().status() != ApiStatusCode::AS_OK )
        {
            // A failed or lost commit reply does not identify whether the
            // native transaction settled. Observe first. Only an unchanged
            // board proves it is safe to drop a still-open staged commit;
            // otherwise preserve INDETERMINATE and never guess rollback.
            auto afterCommitFailure = observe();

            if( afterCommitFailure )
                stored.mutable_observed_after()->CopyFrom( *afterCommitFailure );

            bool rolledBack = false;

            if( afterCommitFailure && before
                && MessageDifferencer::Equals( *afterCommitFailure, *before ) )
                rolledBack = rollback();
            else
                commitOpen = false;

            if( rolledBack )
                return fail( CPBTS_ROLLED_BACK, CPBTP_ROLLBACK_CONFIRMED, "commit_result_unknown",
                             "Native PCB commit did not return a reply and the staged transaction was rolled back" );

            return fail( CPBTS_INDETERMINATE, CPBTP_COMMIT_REQUESTED, "commit_result_unknown",
                         ReplyError( commitReply ).empty() ? "Native PCB commit may have been applied"
                                                            : ReplyError( commitReply ) );
        }

        commitOpen = false;

        stored.set_phase( CPBTP_COMMITTED );

        auto after = observe();

        if( !after )
            return fail( CPBTS_INDETERMINATE, CPBTP_COMMITTED, "post_observation_failed",
                         after.error().error_message() );

        stored.mutable_observed_after()->CopyFrom( *after );
        stored.set_post_commit_state( CPBPS_CONFIRMED );
        stored.set_phase( CPBTP_POSTCOMMIT_OBSERVED );

        if( after->process_epoch() != processEpoch
            || !MessageDifferencer::Equals( after->document(), before->document() )
            || after->revision().epoch() != before->revision().epoch()
            || after->revision().sequence() < before->revision().sequence()
            || !after->project_settings_included() || !Digest( after->state_sha256() )
            || !DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines( *after ) )
            return fail( CPBTS_INDETERMINATE, CPBTP_POSTCOMMIT_OBSERVED, "post_state_mismatch",
                         "The resulting PCB state did not match the committed transaction" );

        stored.set_state( CPBTS_COMPLETED );
        stored.set_phase( CPBTP_COMPLETE );
        stored.clear_error_code();
        stored.clear_error_message();
        return Pack( stored );
    }
    catch( const std::exception& error )
    {
        if( commitOpen && rollback() )
            return fail( CPBTS_ROLLED_BACK, CPBTP_ROLLBACK_CONFIRMED, "checked_item_exception", error.what() );

        return fail( nativeTouched ? CPBTS_INDETERMINATE : CPBTS_REJECTED,
                     stored.phase(), "checked_item_exception", error.what() );
    }
    catch( ... )
    {
        if( commitOpen && rollback() )
            return fail( CPBTS_ROLLED_BACK, CPBTP_ROLLBACK_CONFIRMED,
                         "checked_item_exception", "Unexpected native checked PCB item failure" );

        return fail( nativeTouched ? CPBTS_INDETERMINATE : CPBTS_REJECTED,
                     stored.phase(), "checked_item_exception", "Unexpected native checked PCB item failure" );
    }
}
