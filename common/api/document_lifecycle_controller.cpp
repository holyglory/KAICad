/* Checked lifecycle dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#include <api/document_lifecycle_controller.h>
#include <api/common/commands/capability_commands.pb.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/util/message_differencer.h>
#include <wx/filename.h>
#include <file_content_baseline.h>
#include <ki_exception.h>
#include <kiplatform/io.h>
#include <lockfile.h>
#include <project.h>
#include <project/project_file.h>
#include <optional>
#include <set>
#include <algorithm>
#include <cctype>

namespace
{
using namespace kiapi::automation::v1;
using google::protobuf::util::MessageDifferencer;

// Why native savers did not write a file during the checked save running on this thread.
struct SAVE_PROBLEM_REPORT
{
    DOCUMENT_LIFECYCLE_CONTROLLER::SAVE_PROBLEM kind;
    wxString path;
    wxString reason;
};

thread_local std::vector<SAVE_PROBLEM_REPORT>* activeSaveProblems = nullptr;

std::string Utf8( const wxString& aText )
{
    return aText.ToStdString( wxConvUTF8 );
}

std::string FileName( const std::string& aPath )
{
    return Utf8( wxFileName( wxString::FromUTF8( aPath ) ).GetFullName() );
}

// A reason as a clause of a longer sentence: without surrounding space or a final period.
std::string Clause( std::string aText )
{
    while( !aText.empty() && ( aText.back() == '.' || std::isspace( static_cast<unsigned char>( aText.back() ) ) ) )
        aText.pop_back();

    size_t start = 0;

    while( start < aText.size() && std::isspace( static_cast<unsigned char>( aText[start] ) ) )
        ++start;

    return aText.substr( start );
}

// Protobuf strings must stay valid UTF-8, so never cut through a multi-byte character.
std::string Truncated( const std::string& aText, size_t aLimit )
{
    if( aText.size() <= aLimit )
        return aText;

    size_t end = aLimit;

    while( end > 0 && ( static_cast<unsigned char>( aText[end] ) & 0xC0 ) == 0x80 )
        --end;

    return aText.substr( 0, end );
}

// The first entry whose path (aPathOf) names the file aPath: the same path if there is one,
// otherwise the same file once symbolic links are followed.  Writers name the file they replace,
// and the board editor follows a linked board to its target before it writes (SavePcbFile), so a
// write to the file an observed link points to is a write of that observed file.
template <typename ITER, typename PATH_OF>
ITER FindFile( ITER aBegin, ITER aEnd, const wxString& aPath, PATH_OF aPathOf )
{
    ITER found = std::find_if( aBegin, aEnd, [&]( const auto& aEntry )
                               { return FILE_CONTENT_BASELINE::SamePath( aPathOf( aEntry ), aPath ); } );

    if( found != aEnd )
        return found;

    const wxString target = KIPLATFORM::IO::ResolveSymlinkTarget( aPath );

    return std::find_if( aBegin, aEnd, [&]( const auto& aEntry )
                         {
                             return FILE_CONTENT_BASELINE::SamePath(
                                     KIPLATFORM::IO::ResolveSymlinkTarget( aPathOf( aEntry ) ), target );
                         } );
}

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

// The rendered sheet an agent looks at and the checked state its next batch must name, taken at
// one native checkpoint.  API dispatch runs synchronously on KiCad's GUI thread, so no editor
// event (a keystroke, a pointer edit, an undo) runs between the three reads below; the closing
// state read still refuses the pair if anything the state covers changed while rendering.  A read
// KiCad refuses ends the view with exactly that refusal; a read that returns a reply with a non-OK
// status, or one that is not that read's result, ends it as a bad request.
API_RESULT ReadCheckedView( ApiRequest& aEnvelope, const std::string& aProcessEpoch,
                            const DOCUMENT_LIFECYCLE_CONTROLLER::DISPATCH& aDispatch )
{
    NativeCapabilityReadCheckedView request;
    if( !aEnvelope.message().UnpackTo( &request ) || request.process_epoch() != aProcessEpoch
            || !Uuid( aProcessEpoch ) || request.document().type() != kiapi::common::types::DOCTYPE_SCHEMATIC
            || request.document().sheet_path().path_size() != 1
            || !Uuid( request.document().sheet_path().path( 0 ).value() )
            || request.view().type() != kiapi::common::types::DOCTYPE_SCHEMATIC
            || request.view().sheet_path().path_size() < 1
            || request.view().sheet_path().path( 0 ).value() != request.document().sheet_path().path( 0 ).value()
            || !MessageDifferencer::Equals( request.view().project(), request.document().project() ) )
        return Error( "A checked view requires the exact schematic root, a sheet of that schematic to view "
                      "and the process epoch" );
    auto call = [&]( const google::protobuf::Message& aMessage )
    {
        ApiRequest query;
        query.mutable_header()->CopyFrom( aEnvelope.header() );
        query.mutable_message()->PackFrom( aMessage );
        return aDispatch( query );
    };
    NativeCapabilityCheckedView result;

    ReadCheckedSchematicState checkedQuery;
    checkedQuery.mutable_document()->CopyFrom( request.document() );
    checkedQuery.set_process_epoch( aProcessEpoch );
    // A busy editor, a pending transaction or a change during the combined read is refused with
    // its own status, which the caller sees unchanged.
    API_RESULT checked = call( checkedQuery );
    if( !checked ) return checked;
    if( checked->status().status() != ApiStatusCode::AS_OK || !checked->message().UnpackTo( result.mutable_checked() ) )
        return Error( "The checked schematic state could not be read for the view" );

    CaptureSchematicObservation viewQuery;
    viewQuery.mutable_document()->CopyFrom( request.view() );
    // The schema the checked electrical state uses, so both describe the same object fields.
    viewQuery.set_schema_version( 9 );
    API_RESULT view = call( viewQuery );
    if( !view ) return view;
    if( view->status().status() != ApiStatusCode::AS_OK || !view->message().UnpackTo( result.mutable_view() ) )
        return Error( "The displayed sheet could not be captured for the view" );

    ReadDocumentLifecycleState afterQuery;
    afterQuery.mutable_document()->CopyFrom( request.document() );
    API_RESULT after = call( afterQuery );
    if( !after ) return after;
    DocumentLifecycleState afterState;
    if( after->status().status() != ApiStatusCode::AS_OK || !after->message().UnpackTo( &afterState ) )
        return Error( "The schematic state could not be read again after rendering the view" );

    const auto& revision = result.checked().state().revision();
    if( !MessageDifferencer::Equals( afterState, result.checked().state() )
            || !MessageDifferencer::Equals( result.view().preview().revision(), revision )
            || !MessageDifferencer::Equals( result.view().snapshot().revision(), revision )
            || !MessageDifferencer::Equals( result.view().preview().document(), request.view() )
            || !MessageDifferencer::Equals( result.view().snapshot().data().metadata().document(), request.view() ) )
    {
        ApiResponseStatus changed;
        changed.set_status( ApiStatusCode::AS_NOT_READY );
        changed.set_error_message( "The schematic changed while its view was captured; observe again" );
        return tl::unexpected( changed );
    }
    ApiResponse response;
    response.mutable_status()->set_status( ApiStatusCode::AS_OK );
    response.mutable_message()->PackFrom( result );
    return response;
}
}

const std::vector<std::string>& DOCUMENT_LIFECYCLE_CONTROLLER::RequestTypes()
{
    static const std::vector<std::string> types = []()
    {
        std::vector<std::string> names = {
            std::string( kiapi::automation::v1::CheckedSaveDocument::descriptor()->full_name() ),
            std::string( kiapi::automation::v1::CheckedCloseDocument::descriptor()->full_name() ),
            std::string( kiapi::automation::v1::ReadLifecycleOperation::descriptor()->full_name() ),
            std::string( kiapi::automation::v1::NativeCapabilityReadCheckedView::descriptor()->full_name() )
        };
        std::sort( names.begin(), names.end() );
        return names;
    }();

    return types;
}

bool DOCUMENT_LIFECYCLE_CONTROLLER::Handles( const ApiRequest& aRequest )
{
    // The same type-name parsing as API_HANDLER::Handle, so a request is claimed exactly when
    // its type is in the advertised list.
    std::string typeName;

    if( !google::protobuf::Any::ParseAnyTypeUrl( aRequest.message().type_url(), &typeName ) )
        return false;

    const std::vector<std::string>& types = RequestTypes();
    return std::binary_search( types.begin(), types.end(), typeName );
}

bool DOCUMENT_LIFECYCLE_CONTROLLER::HasUnchangedFileBaselines(
        const kiapi::automation::v1::DocumentLifecycleState& aState )
{
    return FileCoverage( aState );
}

wxString DOCUMENT_LIFECYCLE_CONTROLLER::WriteBlocker( const wxString& aPath )
{
    wxFileName requested( aPath );

    if( aPath.empty() || !requested.IsOk() || !requested.IsAbsolute() )
        return wxS( "its path is not an absolute file path" );

    if( wxDirExists( aPath ) )
        return wxS( "a folder has this name" );

    // The writers replace the file a symbolic link names, beside that file.
    const wxFileName target( KIPLATFORM::IO::ResolveSymlinkTarget( aPath ) );
    const wxString   folder = target.GetPath();

    if( target.FileExists() )
    {
        if( !target.IsFileWritable() )
        {
            if( target.GetFullPath() == requested.GetFullPath() )
                return wxS( "the file is read-only" );

            return wxString::Format( wxS( "the file it links to, '%s', is read-only" ), target.GetFullPath() );
        }

        if( !wxFileName::IsDirWritable( folder ) )
            return wxString::Format( wxS( "its folder '%s' does not allow KiCad to create or replace files" ), folder );

        return wxEmptyString;
    }

    if( wxDirExists( folder ) )
    {
        if( !wxFileName::IsDirWritable( folder ) )
            return wxString::Format( wxS( "its folder '%s' does not allow KiCad to create files" ), folder );

        return wxEmptyString;
    }

    // Writers create one missing folder level, never a deeper tree.
    wxFileName parent( folder, wxEmptyString );

    if( parent.GetDirCount() == 0 )
        return wxString::Format( wxS( "its folder '%s' does not exist" ), folder );

    parent.RemoveLastDir();

    if( !parent.DirExists() )
        return wxString::Format( wxS( "its folder '%s' does not exist and cannot be created" ), folder );

    if( !parent.IsDirWritable() )
        return wxString::Format( wxS( "its folder '%s' does not exist and '%s' does not allow creating it" ),
                                 folder, parent.GetPath() );

    return wxEmptyString;
}


wxString DOCUMENT_LIFECYCLE_CONTROLLER::ReadOnlyProjectReason( const PROJECT& aProject )
{
    if( !aProject.IsReadOnly() )
        return wxEmptyString;

    if( aProject.IsNullProject() )
        return wxS( "no project is open in KiCad, so it writes no project files" );

    const wxString path = aProject.GetProjectFullName();

    // Loading a project marks it read-only when its project file was read-only at that moment
    // (JSON_SETTINGS::LoadFromFile), and KiCad never looks again until the project is reopened.
    if( aProject.GetProjectFile().IsReadOnly() )
    {
        if( WriteBlocker( path ).empty() )
            return wxS( "KiCad opened this project read-only because its project file was read-only when the "
                        "project was opened; the file is writable now, but KiCad only notices that when the "
                        "project is reopened, so reopen the project in KiCad" );

        return wxS( "KiCad opened this project read-only because its project file was read-only when the "
                    "project was opened; make the file writable, then reopen the project in KiCad" );
    }

    // A project whose lock KiCad could not take when it opened it stays read-only until it is
    // reopened (SETTINGS_MANAGER::LoadProject, KICAD_MANAGER_FRAME::ProjectChanged). Look at the
    // lock as it is now, so the reason names what still stands in the way; nothing here guesses at
    // what happened when the project was opened.
    LOCKFILE* lock = aProject.GetProjectLock();

    if( !lock || !lock->Valid() )
    {
        const wxString lockPath = LOCKFILE::LockPathFor( path );
        LOCKFILE       current = LOCKFILE::Inspect( path );
        const wxString user = current.GetUsername();
        const wxString host = current.GetHostname();
        const bool     exists = wxFileName::FileExists( lockPath );
        const wxString owner = user.empty() && host.empty()
                                       ? wxString( wxS( "its lock file does not say who" ) )
                                       : wxString::Format( wxS( "its lock file names user '%s' on computer '%s'" ),
                                                           user, host );

        // Inspect() reports the lock as held while any open lock file holds it. KiCad itself keeps
        // the lock it could not validate (KICAD_MANAGER_FRAME::ProjectChanged), and for another
        // user's record that object holds the lock file's system lock (FILE_LOCK::Acquire succeeds,
        // LOCKFILE refuses the record), so only without such an object does "held" mean that
        // another program holds it.
        if( !lock && !current.Valid() )
        {
            return wxString::Format( wxS( "KiCad opened this project read-only because another program holds its "
                                          "project lock '%s' (%s); close the project there, then reopen it in "
                                          "KiCad" ),
                                     lockPath, owner );
        }

        // KiCad takes a lock by opening its file for writing (FILE_LOCK::Acquire).
        if( exists && !wxFileName::IsFileWritable( lockPath ) )
            return wxString::Format( wxS( "KiCad opened this project read-only because it cannot write its project "
                                          "lock file '%s' (the file is read-only), so it could not take the lock; "
                                          "make the lock file writable or delete it, then reopen the project in "
                                          "KiCad" ),
                                     lockPath );

        // An abandoned lock is taken over only when it is this user's own on this computer: another
        // user's may still be in use on another computer that shares the folder.
        if( exists && !current.IsLockedByMe() )
            return wxString::Format( wxS( "KiCad opened this project read-only because its project lock '%s' belongs "
                                          "to user '%s' on computer '%s', and KiCad never takes over another user's "
                                          "lock because that user may have the project open on another computer; "
                                          "close the project there, or delete the lock file if nobody has the "
                                          "project open, then reopen it in KiCad" ),
                                     lockPath, user, host );

        // KiCad's own lock object could not take a lock that names this user on this computer (or
        // nobody). Whether another KiCad holds it cannot be told from here, since KiCad's object
        // may hold it itself, so the reason names only the record and what to do.
        if( lock && exists )
            return wxString::Format( wxS( "KiCad could not take the project lock '%s' when it opened the project, so "
                                          "it opened the project read-only (%s); close the project in any other "
                                          "KiCad that has it open, then reopen it in KiCad" ),
                                     lockPath, owner );

        return wxString::Format( wxS( "KiCad could not take the project lock '%s' when it opened the project, so it "
                                      "opened the project read-only; nothing holds the lock now, so reopen the "
                                      "project in KiCad" ),
                                 lockPath );
    }

    return wxS( "KiCad holds this project read-only and writes none of its files; reopen the project in KiCad" );
}


void DOCUMENT_LIFECYCLE_CONTROLLER::ReportSaveProblem( SAVE_PROBLEM aKind, const wxString& aPath,
                                                       const wxString& aReason )
{
    // Bounded: savers report at most a few problems per observed file.
    if( activeSaveProblems && activeSaveProblems->size() < 64 )
        activeSaveProblems->push_back( { aKind, aPath, aReason } );
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
    if( aEnvelope.message().Is<NativeCapabilityReadCheckedView>() )
        return ReadCheckedView( aEnvelope, aProcessEpoch, aDispatch );
    if( aEnvelope.message().Is<ReadLifecycleOperation>() )
    {
        ReadLifecycleOperation query;
        if( !aEnvelope.message().UnpackTo( &query ) || !Uuid( query.operation_id() )
                || query.process_epoch() != aProcessEpoch ) return Error( "Invalid lifecycle receipt target or process epoch" );
        const auto found = m_receipts.find( query.operation_id() );
        // No receipt means this process never started the operation: the request did not reach
        // KiCad, or KiCad refused it before starting (for example a malformed request).
        if( found == m_receipts.end() )
            return Error( std::string( UNKNOWN_OPERATION_MARKER )
                          + ": KiCad has no record of starting a save or close with this operation ID in this "
                            "process, so it saved or closed nothing for it" );
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
        result.set_error_message( Truncated( message, 2048 ) );
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

    // Observed files this save replaced, observed files the writer began to replace but left
    // unchanged when the save failed, and every problem the native savers reported.
    std::vector<std::string> writtenFiles;
    std::vector<std::string> interruptedFiles;
    std::vector<SAVE_PROBLEM_REPORT> saveProblems;

    // This controller's own refusal to let a writer replace a file. Savers report the exception
    // it raises as their own write failure, so their reports from that point on describe this
    // refusal rather than a separate cause.
    struct REFUSAL
    {
        std::string code;
        wxString    path;
        wxString    reason;
        std::string next;
        size_t      reportsBefore = 0;
    };
    std::optional<REFUSAL> refusal;
    auto refuse = [&]( const char* aCode, const wxString& aPath, const wxString& aReason, const char* aNext )
    {
        if( !refusal )
            refusal = REFUSAL{ aCode, aPath, aReason, aNext, saveProblems.size() };
    };
    auto recordWrites = [&]()
    {
        result.clear_written_files();

        for( const std::string& path : writtenFiles )
            result.add_written_files( path );
    };

    // A failed save names what reached the disk, what stopped it and why, and whether the editor
    // still holds the work, so the caller can fix the actual cause and save again.
    auto describeSaveFailure = [&]( const DocumentLifecycleState& aBefore, const DocumentLifecycleState* aAfter,
                                    const std::string& aNative ) -> API_RESULT
    {
        using CAUSES = std::vector<std::pair<std::string, std::string>>;
        auto observed = [&]( const std::string& aPath )
        {
            return std::find( aBefore.native_files().begin(), aBefore.native_files().end(), aPath )
                   != aBefore.native_files().end();
        };
        // Results name a document file exactly as the observation spelled it.
        auto observedPath = [&]( const wxString& aPath ) -> std::string
        {
            if( aPath.empty() )
                return {};

            const auto file = FindFile( aBefore.native_files().begin(), aBefore.native_files().end(), aPath,
                                        []( const std::string& aFile ) { return wxString::FromUTF8( aFile ); } );

            return file != aBefore.native_files().end() ? *file : Utf8( aPath );
        };
        auto written = [&]( const std::string& aPath )
        {
            return std::find( writtenFiles.begin(), writtenFiles.end(), aPath ) != writtenFiles.end();
        };
        auto add = []( CAUSES& aCauses, const std::string& aPath, const std::string& aReason )
        {
            const std::string reason = Clause( aReason );

            if( std::none_of( aCauses.begin(), aCauses.end(), [&]( const auto& cause )
                              { return cause.first == aPath && ( !aPath.empty() || cause.second == reason ); } ) )
                aCauses.emplace_back( aPath, reason );
        };
        auto describe = []( const std::string& aPath, const std::string& aReason )
        {
            return aPath.empty() ? aReason : "'" + FileName( aPath ) + "' (" + aPath + "): " + aReason;
        };
        auto list = [&]( const CAUSES& aCauses )
        {
            std::string text;

            for( const auto& [path, reason] : aCauses )
                text += ( text.empty() ? "" : "; " ) + describe( path, reason );

            return text;
        };

        CAUSES blocked, refused, failed, unconfirmed;
        const size_t reports = refusal ? refusal->reportsBefore : saveProblems.size();

        for( size_t i = 0; i < reports; ++i )
        {
            const SAVE_PROBLEM_REPORT& problem = saveProblems[i];
            const std::string path = observedPath( problem.path );

            // The writer failed after it had already replaced this file, for example while
            // flushing its folder to disk: the file is written, so it is not a blocked file.
            if( problem.kind != SAVE_PROBLEM::SAVE_REFUSED && written( path ) )
                add( unconfirmed, path, Utf8( problem.reason ) );
            else
                add( problem.kind == SAVE_PROBLEM::WRITE_BLOCKED ? blocked
                     : problem.kind == SAVE_PROBLEM::SAVE_REFUSED ? refused
                                                                  : failed,
                     path, Utf8( problem.reason ) );
        }

        // A saver that reports nothing, such as the PCB editor, is explained by checking every
        // file it did not write. KiCad's own message is quoted as well.
        const bool unexplained = saveProblems.empty() && !refusal;

        if( unexplained )
        {
            for( const std::string& file : aBefore.native_files() )
            {
                if( written( file ) )
                    continue;

                wxString reason = WriteBlocker( wxString::FromUTF8( file ) );

                if( !reason.empty() )
                    add( blocked, file, Utf8( reason ) );
            }

            // The writer began replacing these files and the save failed before it confirmed
            // them, while they still held their old content. With nothing found that blocks them
            // they are named as the files KiCad could not write, never as blocked files.
            for( const std::string& file : interruptedFiles )
            {
                if( !written( file ) && std::none_of( blocked.begin(), blocked.end(), [&]( const auto& cause )
                                                      { return cause.first == file; } ) )
                {
                    add( failed, file, "the save failed while KiCad was replacing this file, and KiCad gave no "
                                       "system reason" );
                }
            }
        }

        // Only files the file system would not let KiCad write are blocked files; a refusal that
        // writable files would not fix never marks one.
        for( const auto& cause : blocked )
            if( observed( cause.first ) )
                result.add_blocked_files( cause.first );

        std::string cause;
        auto sentence = [&]( const std::string& aText ) { cause += ( cause.empty() ? "" : ". " ) + aText; };

        if( unexplained || ( !refusal && blocked.empty() && refused.empty() && failed.empty() && unconfirmed.empty() ) )
            sentence( "KiCad reported: " + Clause( aNative ) );

        if( refusal )
            sentence( "KiCad stopped before replacing "
                      + describe( observedPath( refusal->path ), Clause( Utf8( refusal->reason ) ) ) );

        if( !refused.empty() )
            sentence( "KiCad refused to save: " + list( refused ) );

        if( !blocked.empty() )
            sentence( "KiCad cannot write " + list( blocked ) );

        if( !failed.empty() )
            sentence( "KiCad could not write " + list( failed ) );

        if( !unconfirmed.empty() )
            sentence( "After replacing it, KiCad reported a failure for " + list( unconfirmed ) );

        const bool blockedFiles = result.blocked_files_size() > 0;
        const std::string code = refusal         ? refusal->code
                                 : blockedFiles  ? "file_not_writable"
                                 : !refused.empty() ? "native_save_refused"
                                                    : "native_save_failed";
        const std::string again = "read a fresh kicad_document_state and save again with a new operation ID.";
        const std::string next =
                refusal ? refusal->next
                : !blocked.empty() && !refused.empty()
                        ? "Fix every cause named above: make the named files and folders writable (or free disk "
                          "space) and resolve what KiCad refused. Then " + again
                : !blocked.empty()
                        ? "Fix what stops KiCad writing the named files (for example make the file or folder "
                          "writable or free disk space), then " + again
                : !refused.empty()
                        ? "Resolve what KiCad refused, as named above; making the document's files writable does "
                          "not help. Then " + again
                : !failed.empty() || !unconfirmed.empty()
                        ? "KiCad found nothing that blocks the named files now, so the cause may have passed (for "
                          "example a full disk or an unavailable network drive): check the disk, then " + again
                        : "Inspect the document in KiCad, then " + again;
        const std::string editor = !aAfter ? "KiCad could not report the editor state afterwards; read "
                                             "kicad_document_state before doing anything else."
                                   : aAfter->native_content_dirty() ? "The editor still holds all unsaved changes."
                                                                    : "The editor reports no unsaved changes.";

        if( writtenFiles.empty() )
        {
            return fail( LOS_FAILED, code.c_str(),
                         "The document was not saved. " + cause + ". KiCad replaced none of the document's files. "
                                 + editor + " " + next );
        }

        std::string done, pending;

        for( const std::string& file : writtenFiles )
            done += ( done.empty() ? "" : ", " ) + FileName( file );

        for( const std::string& file : aBefore.native_files() )
            if( !written( file ) )
                pending += ( pending.empty() ? "" : ", " ) + FileName( file );

        if( pending.empty() )
        {
            return fail( LOS_FAILED, "partial_save",
                         "KiCad wrote every file (" + done + ") but still reported a failure. " + cause + ". "
                                 + editor + " " + next );
        }

        return fail( LOS_FAILED, "partial_save",
                     "Saving stopped part way. " + cause + ". Already written: " + done + ". Not written: " + pending
                             + ". The files on disk now mix old and new content. " + editor
                             + " A successful save writes every file again. " + next );
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
            struct CLOSE_SCOPE
            {
                bool& flag;
                bool previous;
                explicit CLOSE_SCOPE( bool& value ) : flag( value ), previous( value ) { flag = true; }
                ~CLOSE_SCOPE() { flag = previous; }
            } closeScope( m_cleanCloseActive );
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
        struct FILE_VERSION { bool exists; uint64_t bytes; std::string sha; std::string path; };
        std::map<wxString, FILE_VERSION> accepted;
        for( const auto& file : before->file_baselines() )
            accepted.emplace( wxString::FromUTF8( file.path() ),
                              FILE_VERSION{ file.current_exists(), file.current_bytes(), file.current_sha256(), file.path() } );
        auto locate = [&]( const wxString& path )
        {
            return FindFile( accepted.begin(), accepted.end(), path,
                             []( const auto& entry ) -> const wxString& { return entry.first; } );
        };
        // The file the writer was allowed to replace and has not confirmed yet, with its version
        // before. A writer can fail after the replacement itself, for example while flushing the
        // folder to disk, and then never confirms it.
        std::optional<std::pair<wxString, FILE_VERSION>> replacing;
        auto countInterruptedReplacement = [&]()
        {
            if( !replacing )
                return;

            const auto current = FILE_CONTENT_BASELINE::Read( replacing->first );
            const FILE_VERSION& previous = replacing->second;
            auto listed = []( const std::vector<std::string>& aFiles, const std::string& aPath )
            { return std::find( aFiles.begin(), aFiles.end(), aPath ) != aFiles.end(); };

            if( current.Known() && !listed( writtenFiles, previous.path ) )
            {
                if( current.Exists() != previous.exists || current.Bytes() != previous.bytes
                        || current.Sha256() != previous.sha )
                    writtenFiles.push_back( previous.path );
                else if( !listed( interruptedFiles, previous.path ) )
                    interruptedFiles.push_back( previous.path );
            }

            replacing.reset();
        };
        // The writer calls the first function just before it replaces a file and the second once
        // it has. A refusal here is recorded with its own code before the writer sees it fail.
        FILE_WRITE_OBSERVER writes(
            [&]( const wxString& path )
            {
                // A writer that moves on to another file after failing never confirms the last one.
                countInterruptedReplacement();
                auto entry = locate( path );
                if( entry == accepted.end() )
                {
                    const wxString extension = wxFileName( path ).GetExt();
                    if( extension == "kicad_sch" || extension == "kicad_pcb" || extension == "kicad_pro" )
                    {
                        refuse( "save_outside_document", path,
                                wxS( "the save tried to write this design file, which is not one of the observed "
                                     "document's files" ),
                                "Read a fresh kicad_document_state, which lists every file of the document, and save "
                                "again with a new operation ID." );
                        THROW_IO_ERROR( "Save attempted a design file outside the observed document: " + path );
                    }
                    return; // Local UI settings are not design-state persistence.
                }
                const auto current = FILE_CONTENT_BASELINE::Read( path );
                if( !current.Known() )
                {
                    refuse( "file_unreadable_during_save", path,
                            wxS( "KiCad could not read it to confirm that no other program changed it after the save "
                                 "began" ),
                            "Make the file readable again, then read a fresh kicad_document_state and save again "
                            "with a new operation ID." );
                    THROW_IO_ERROR( "File could not be read during checked save: " + path );
                }
                if( current.Exists() != entry->second.exists || current.Bytes() != entry->second.bytes
                        || current.Sha256() != entry->second.sha )
                {
                    refuse( "file_changed_during_save", path,
                            wxS( "another program changed it after the save began, and KiCad keeps that version "
                                 "rather than overwrite it" ),
                            "Read a fresh kicad_document_state, which shows the file as changed on disk, decide "
                            "which version to keep, and save again with a new operation ID." );
                    THROW_IO_ERROR( "File changed during checked save: " + path );
                }
                replacing.emplace( path, entry->second );
            },
            [&]( const FILE_CONTENT_BASELINE& written )
            {
                auto entry = locate( written.Path() );
                if( entry == accepted.end() )
                    return;
                replacing.reset();
                // Called only after the file was replaced, so it counts as written even when its
                // new version is unknown; only a known version can accept a later write.
                if( std::find( writtenFiles.begin(), writtenFiles.end(), entry->second.path ) == writtenFiles.end() )
                    writtenFiles.push_back( entry->second.path );
                if( written.Known() )
                    entry->second = FILE_VERSION{ written.Exists(), written.Bytes(), written.Sha256(), entry->second.path };
            } );
        attemptedSave = true;
        API_RESULT saved;
        {
            struct PROBLEM_SCOPE
            {
                std::vector<SAVE_PROBLEM_REPORT>* previous;
                explicit PROBLEM_SCOPE( std::vector<SAVE_PROBLEM_REPORT>& aProblems ) : previous( activeSaveProblems )
                { activeSaveProblems = &aProblems; }
                ~PROBLEM_SCOPE() { activeSaveProblems = previous; }
            } problemScope( saveProblems );

            try
            {
                saved = aDispatch( envelope );
            }
            catch( ... )
            {
                countInterruptedReplacement();
                throw;
            }

            countInterruptedReplacement();
        }
        auto after = observe();
        if( after ) result.mutable_observed_state()->CopyFrom( *after );
        recordWrites();
        if( !saved || saved->status().status() != ApiStatusCode::AS_OK )
            return describeSaveFailure( *before, after ? &*after : nullptr,
                                        saved ? saved->status().error_message() : saved.error().error_message() );
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
        recordWrites();
        return fail( attemptedSave ? LOS_INDETERMINATE : LOS_REJECTED,
                     "native_lifecycle_error", error.what() );
    }
    catch( ... )
    {
        recordWrites();
        return fail( attemptedSave ? LOS_INDETERMINATE : LOS_REJECTED,
                     "native_lifecycle_error", "Native lifecycle operation raised an unexpected error; inspect the document before further saves" );
    }
}
