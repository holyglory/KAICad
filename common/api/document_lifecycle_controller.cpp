/* Checked lifecycle dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#include <api/document_lifecycle_controller.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <wx/filename.h>
#include <file_content_baseline.h>
#include <ki_exception.h>
#include <set>
#include <algorithm>

namespace
{
using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

bool Uuid( const std::string& aValue )
{
    if( aValue.size() != 36 ) return false;
    bool nonzero = false;
    for( size_t i = 0; i < aValue.size(); ++i )
    {
        if( i == 8 || i == 13 || i == 18 || i == 23 ) { if( aValue[i] != '-' ) return false; }
        else
        {
            const char c = aValue[i];
            if( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
            nonzero |= c != '0';
        }
    }
    return nonzero;
}

API_RESULT Error( const std::string& aMessage )
{
    ApiResponseStatus error;
    error.set_status( ApiStatusCode::AS_BAD_REQUEST );
    error.set_error_message( aMessage );
    return tl::unexpected( error );
}

bool Digest( const std::string& value )
{
    return value.size() == 64 && std::all_of( value.begin(), value.end(), []( char c )
            { return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ); } );
}

ApiResponse Pack( const LifecycleOperationResult& aResult )
{
    ApiResponse response;
    response.mutable_status()->set_status( ApiStatusCode::AS_OK );
    response.mutable_message()->PackFrom( aResult );
    return response;
}

bool FileCoverage( const DocumentLifecycleState& aState )
{
    std::set<std::string> paths( aState.native_files().begin(), aState.native_files().end() );
    if( paths.empty() || paths.size() != static_cast<size_t>( aState.native_files_size() )
            || paths.size() != static_cast<size_t>( aState.file_baselines_size() ) ) return false;
    for( const auto& file : aState.file_baselines() )
    {
        if( !paths.erase( file.path() ) || !wxFileName( wxString::FromUTF8( file.path() ) ).IsAbsolute()
                || file.status() != NFBS_UNCHANGED || !file.baseline_known() || !file.current_known()
                || file.baseline_path() != file.path() || file.baseline_exists() != file.current_exists()
                || file.baseline_sha256() != file.current_sha256() || file.baseline_bytes() != file.current_bytes() )
            return false;
        if( file.baseline_exists() ? !Digest( file.baseline_sha256() )
                                  : ( !file.baseline_sha256().empty() || file.baseline_bytes() != 0 ) )
            return false;
    }
    return paths.empty();
}
}

bool DOCUMENT_LIFECYCLE_CONTROLLER::Handles( const ApiRequest& aRequest )
{
    return aRequest.message().Is<kiapi::automation::v1::CheckedSaveDocument>()
            || aRequest.message().Is<kiapi::automation::v1::CheckedCloseDocument>()
            || aRequest.message().Is<kiapi::automation::v1::ReadLifecycleOperation>();
}

void DOCUMENT_LIFECYCLE_CONTROLLER::RememberCleanState( const kiapi::automation::v1::DocumentLifecycleState& state )
{
    if( state.native_content_dirty() || !FileCoverage( state ) || !Digest( state.state_sha256() )
            || !Uuid( state.native_identity() ) || !Uuid( state.revision().epoch() ) ) return;
    m_cleanByScope[state.scope()] = { state.revision().epoch(), state.native_identity(), state.state_sha256() };
}

void DOCUMENT_LIFECYCLE_CONTROLLER::AnnotateCleanState( kiapi::automation::v1::DocumentLifecycleState& state ) const
{
    state.clear_clean_checkpoint_sha256();
    auto checkpoint = m_cleanByScope.find( state.scope() );
    if( checkpoint != m_cleanByScope.end() && checkpoint->second.epoch == state.revision().epoch()
            && checkpoint->second.identity == state.native_identity() )
        state.set_clean_checkpoint_sha256( checkpoint->second.sha );
}

API_RESULT DOCUMENT_LIFECYCLE_CONTROLLER::Handle( ApiRequest& aEnvelope,
        const std::string& aProcessEpoch, const DISPATCH& aDispatch )
{
    using namespace kiapi::automation::v1;
    if( aEnvelope.message().Is<ReadLifecycleOperation>() )
    {
        ReadLifecycleOperation query;
        if( !aEnvelope.message().UnpackTo( &query ) || !Uuid( query.operation_id() )
                || query.process_epoch() != aProcessEpoch ) return Error( "Invalid lifecycle receipt target or process epoch" );
        const auto found = m_receipts.find( query.operation_id() );
        if( found == m_receipts.end() ) return Error( "Lifecycle operation is not known in this process" );
        if( !MessageDifferencer::Equals( found->second.request.document(), query.document() ) )
            return Error( "Lifecycle operation belongs to another document" );
        return Pack( found->second.result );
    }

    CheckedSaveDocument request;
    const bool close = aEnvelope.message().Is<CheckedCloseDocument>();
    bool decoded;
    if( close )
    {
        CheckedCloseDocument source;
        decoded = aEnvelope.message().UnpackTo( &source );
        request.mutable_document()->CopyFrom( source.document() );
        request.set_operation_id( source.operation_id() );
        if( source.has_expected_state() ) request.mutable_expected_state()->CopyFrom( source.expected_state() );
    }
    else decoded = aEnvelope.message().UnpackTo( &request );
    if( !decoded || !Uuid( request.operation_id() )
            || !request.has_expected_state() || request.expected_state().process_epoch() != aProcessEpoch
            || !MessageDifferencer::Equals( request.document(), request.expected_state().document() ) )
        return Error( "Checked save requires an operation UUID and an exact observed document/process target" );

    const auto previous = m_receipts.find( request.operation_id() );
    if( previous != m_receipts.end() )
    {
        if( close != previous->second.close || !MessageDifferencer::Equals( request, previous->second.request ) )
            return Error( "Lifecycle operation ID was already used for another request" );
        return Pack( previous->second.result );
    }

    // Reserve room for both before/after state and errors before dispatching any mutation.
    constexpr size_t budget = 16 * 1024 * 1024;
    const size_t requestSize = request.ByteSizeLong();
    if( requestSize > ( budget - 4096 ) / 3 || m_receipts.size() >= 4096 )
        return Error( "Lifecycle receipt capacity exhausted; no save was attempted" );
    const size_t reservation = requestSize * 3 + 4096;
    if( reservation > budget - m_reservedBytes )
        return Error( "Lifecycle receipt capacity exhausted; no save was attempted" );

    RECEIPT receipt;
    receipt.request = request;
    receipt.close = close;
    receipt.result.mutable_document()->CopyFrom( request.document() );
    receipt.result.set_operation_id( request.operation_id() );
    receipt.result.set_process_epoch( aProcessEpoch );
    receipt.result.set_status( LOS_INDETERMINATE );
    receipt.result.set_error_code( "operation_in_progress" );
    auto& result = m_receipts.emplace( request.operation_id(), std::move( receipt ) ).first->second.result;
    m_reservedBytes += reservation;
    bool attemptedSave = false;
    auto fail = [&]( LifecycleOperationStatus status, const char* code, const std::string& message ) -> API_RESULT
    {
        result.set_status( status ); result.set_error_code( code );
        result.set_error_message( message.substr( 0, 2048 ) );
        return Pack( result );
    };
    auto observe = [&]() -> HANDLER_RESULT<DocumentLifecycleState>
    {
        ReadDocumentLifecycleState query;
        query.mutable_document()->CopyFrom( request.document() );
        ApiRequest envelope;
        envelope.mutable_header()->CopyFrom( aEnvelope.header() );
        envelope.mutable_message()->PackFrom( query );
        auto reply = aDispatch( envelope );
        if( !reply ) return tl::unexpected( reply.error() );
        if( reply->status().status() != ApiStatusCode::AS_OK ) return tl::unexpected( reply->status() );
        DocumentLifecycleState state;
        if( !reply->message().UnpackTo( &state ) || state.ByteSizeLong() > requestSize * 2 + 1024 )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Native lifecycle observation is invalid or exceeded the reserved receipt size" );
            return tl::unexpected( error );
        }
        AnnotateCleanState( state );
        return state;
    };

    try
    {
        auto before = observe();
        if( !before ) return fail( LOS_REJECTED, "observation_failed", before.error().error_message() );
        result.mutable_observed_state()->CopyFrom( *before );
        if( !MessageDifferencer::Equals( request.expected_state(), *before ) )
            return fail( LOS_REJECTED, "stale_document_state", "Document or disk state changed; obtain a fresh observation" );
        if( !Uuid( before->native_identity() ) || !before->has_revision() || !Uuid( before->revision().epoch() )
                || !Digest( before->state_sha256() ) || !before->project_settings_included()
                || ( before->scope() != DLS_SCHEMATIC_HIERARCHY && before->scope() != DLS_PCB ) || !FileCoverage( *before ) )
            return fail( LOS_REJECTED, "file_baseline_conflict", "Loaded file coverage is incomplete or differs from disk; no save was attempted" );

        if( close )
        {
            if( before->native_content_dirty() || before->clean_checkpoint_sha256().empty()
                    || before->clean_checkpoint_sha256() != before->state_sha256() )
                return fail( LOS_REJECTED, "document_not_clean", "Document differs from its native loaded/saved checkpoint; close will not save or discard it" );
            kiapi::common::commands::CloseDocument closing;
            closing.mutable_document()->CopyFrom( request.document() );
            ApiRequest envelope;
            envelope.mutable_header()->CopyFrom( aEnvelope.header() );
            envelope.mutable_message()->PackFrom( closing );
            attemptedSave = true; // Any exception after native close dispatch is an uncertain operation.
            auto closed = aDispatch( envelope );
            if( !closed || closed->status().status() != ApiStatusCode::AS_OK )
                return fail( LOS_FAILED, "native_close_failed", closed ? closed->status().error_message() : closed.error().error_message() );
            result.set_status( LOS_CLOSED ); result.clear_error_code(); result.clear_error_message();
            m_cleanByScope.erase( before->scope() );
            return Pack( result );
        }

        kiapi::common::commands::SaveDocument save;
        save.mutable_document()->CopyFrom( request.document() );
        ApiRequest envelope;
        envelope.mutable_header()->CopyFrom( aEnvelope.header() );
        envelope.mutable_message()->PackFrom( save );
        struct FILE_VERSION { bool exists; uint64_t bytes; std::string sha; };
        std::map<wxString, FILE_VERSION> accepted;
        for( const auto& file : before->file_baselines() )
            accepted.emplace( wxString::FromUTF8( file.path() ),
                              FILE_VERSION{ file.current_exists(), file.current_bytes(), file.current_sha256() } );
        auto locate = [&]( const wxString& path )
        {
            return std::find_if( accepted.begin(), accepted.end(), [&]( const auto& entry )
                    { return FILE_CONTENT_BASELINE::SamePath( entry.first, path ); } );
        };
        FILE_WRITE_OBSERVER writes(
            [&]( const wxString& path )
            {
                auto entry = locate( path );
                if( entry == accepted.end() )
                {
                    const wxString extension = wxFileName( path ).GetExt();
                    if( extension == "kicad_sch" || extension == "kicad_pcb" || extension == "kicad_pro" )
                        THROW_IO_ERROR( "Save attempted a design file outside the observed document: " + path );
                    return; // Local UI settings are not design-state persistence.
                }
                const auto current = FILE_CONTENT_BASELINE::Read( path );
                if( !current.Known() || current.Exists() != entry->second.exists
                        || current.Bytes() != entry->second.bytes || current.Sha256() != entry->second.sha )
                    THROW_IO_ERROR( "File changed during checked save: " + path );
            },
            [&]( const FILE_CONTENT_BASELINE& written )
            {
                auto entry = locate( written.Path() );
                if( entry != accepted.end() && written.Known() )
                    entry->second = FILE_VERSION{ written.Exists(), written.Bytes(), written.Sha256() };
            } );
        attemptedSave = true;
        auto saved = aDispatch( envelope );
        auto after = observe();
        if( after ) result.mutable_observed_state()->CopyFrom( *after );
        if( !saved || saved->status().status() != ApiStatusCode::AS_OK )
            return fail( LOS_FAILED, "native_save_failed", saved ? saved->status().error_message() : saved.error().error_message() );
        if( !after ) return fail( LOS_INDETERMINATE, "saved_state_unavailable", after.error().error_message() );
        if( !MessageDifferencer::Equals( request.document(), after->document() )
                || after->process_epoch() != aProcessEpoch || after->native_identity() != before->native_identity()
                || after->revision().epoch() != before->revision().epoch()
                || after->native_content_dirty() || !FileCoverage( *after ) )
            return fail( LOS_INDETERMINATE, "saved_state_not_confirmed", "Native save returned, but clean matching disk state was not confirmed" );
        RememberCleanState( *after );
        AnnotateCleanState( *after );
        result.mutable_observed_state()->CopyFrom( *after );
        result.set_status( LOS_SAVED ); result.clear_error_code(); result.clear_error_message();
        return Pack( result );
    }
    catch( const std::exception& error )
    {
        return fail( attemptedSave ? LOS_INDETERMINATE : LOS_REJECTED,
                     "native_lifecycle_error", error.what() );
    }
    catch( ... )
    {
        return fail( attemptedSave ? LOS_INDETERMINATE : LOS_REJECTED,
                     "native_lifecycle_error", "Native lifecycle operation raised an unexpected error; inspect the document before further saves" );
    }
}
