/* Native PCB manufacturing admission and journal inspection. GPL-3.0-or-later. */
#include "pcb_manufacturing_job_manager.h"

#include <api/native_state_digest.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiplatform/io.h>
#include <wx/filename.h>
#include <wx/string.h>
#include <algorithm>
#include <array>
#include <cerrno>
#include <cstdlib>
#include <filesystem>
#include <iterator>
#include <limits>
#include <memory>
#include <set>
#include <stdexcept>
#include <string_view>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

using namespace kiapi::automation::v1;
using kiapi::common::types::DocumentType;
namespace fs = std::filesystem;

namespace
{
constexpr uint64_t MAX_SOURCE_BYTES = 32ULL * 1024 * 1024;
constexpr uint64_t MAX_JOURNAL_BYTES = 64ULL * 1024 * 1024;
constexpr uint32_t JOURNAL_VERSION = 2;

std::string Hash( std::string_view aBytes )
{
    NATIVE_STATE_DIGEST digest;
    digest.Append( aBytes );
    return digest.Hex();
}

wxString WxPath( const fs::path& aPath )
{
    const auto utf8 = aPath.u8string();
    return wxString::FromUTF8( reinterpret_cast<const char*>( utf8.data() ), utf8.size() );
}

bool Missing( const std::error_code& aError )
{
    return aError == std::errc::no_such_file_or_directory;
}

bool ReparsePath( const fs::path& aPath )
{
#ifdef _WIN32
    DWORD attributes = GetFileAttributesW( aPath.c_str() );
    return attributes != INVALID_FILE_ATTRIBUTES && ( attributes & FILE_ATTRIBUTE_REPARSE_POINT );
#else
    (void) aPath;
    return false;
#endif
}

// Check all existing ancestors as well as the target: accepting a link in the
// parent would defeat a no-follow open of the leaf. No path is canonicalized
// through a link and no derived receipt path becomes an authority to delete.
tl::expected<void, std::string> NoLinks( const fs::path& aPath )
{
    fs::path current = aPath.root_path();
    for( const fs::path& part : aPath.relative_path() )
    {
        current /= part;
        std::error_code error;
        const auto status = fs::symlink_status( current, error );
        if( error && !Missing( error ) ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( !error && ( fs::is_symlink( status ) || ReparsePath( current ) ) )
            return tl::unexpected( "manufacturing_journal_link_rejected" );
    }
    return {};
}

tl::expected<bool, std::string> RegularOrMissing( const fs::path& aPath )
{
    if( auto valid = NoLinks( aPath ); !valid ) return tl::unexpected( valid.error() );
    std::error_code error;
    const auto status = fs::symlink_status( aPath, error );
    if( Missing( error ) || ( !error && status.type() == fs::file_type::not_found ) ) return false;
    if( error ) return tl::unexpected( "manufacturing_journal_unavailable" );
    if( !fs::is_regular_file( status ) ) return tl::unexpected( "manufacturing_journal_not_regular" );
    return true;
}

// A bounded descriptor read cannot block on a substituted FIFO or follow a
// substituted leaf symlink. The full journal hash below rejects torn/growing
// records and a valid receipt is exposed only after all semantic checks.
tl::expected<std::string, std::string> ReadBytes( const fs::path& aPath )
{
    if( auto regular = RegularOrMissing( aPath ); !regular ) return tl::unexpected( regular.error() );
    else if( !*regular ) return tl::unexpected( "journal_not_found" );
    std::string bytes;
#ifdef _WIN32
    HANDLE handle = CreateFileW( aPath.c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr );
    if( handle == INVALID_HANDLE_VALUE ) return tl::unexpected( "manufacturing_journal_unavailable" );
    struct HANDLE_CLOSE { HANDLE value; ~HANDLE_CLOSE() { CloseHandle( value ); } } close{ handle };
    BY_HANDLE_FILE_INFORMATION information;
    if( !GetFileInformationByHandle( handle, &information )
        || ( information.dwFileAttributes & ( FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY ) ) )
        return tl::unexpected( "manufacturing_journal_not_regular" );
    const uint64_t length = ( uint64_t( information.nFileSizeHigh ) << 32 ) | information.nFileSizeLow;
    if( length == 0 || length > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
    std::array<char, 4096> buffer;
    DWORD count;
    while( true )
    {
        if( !ReadFile( handle, buffer.data(), DWORD( buffer.size() ), &count, nullptr ) )
            return tl::unexpected( "manufacturing_journal_unavailable" );
        if( count == 0 ) break;
        if( bytes.size() + count > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
        bytes.append( buffer.data(), count );
    }
#else
    int descriptor = ::open( aPath.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK );
    if( descriptor < 0 ) return tl::unexpected( "manufacturing_journal_unavailable" );
    struct CLOSE { int descriptor; ~CLOSE() { ::close( descriptor ); } } close{ descriptor };
    struct stat information;
    if( ::fstat( descriptor, &information ) != 0 || !S_ISREG( information.st_mode ) )
        return tl::unexpected( "manufacturing_journal_not_regular" );
    if( information.st_size <= 0 || uint64_t( information.st_size ) > MAX_JOURNAL_BYTES )
        return tl::unexpected( "manufacturing_journal_oversized" );
    std::array<char, 4096> buffer;
    while( true )
    {
        ssize_t count = ::read( descriptor, buffer.data(), buffer.size() );
        if( count < 0 && errno == EINTR ) continue;
        if( count < 0 ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( count == 0 ) break;
        if( bytes.size() + size_t( count ) > MAX_JOURNAL_BYTES )
            return tl::unexpected( "manufacturing_journal_oversized" );
        bytes.append( buffer.data(), size_t( count ) );
    }
#endif
    return bytes;
}

// Cooperating native instances cannot replace one another's admission record.
// OS locks release after a crash. Reads need no lock and create no files.
tl::expected<std::unique_ptr<KIPLATFORM::IO::FILE_LOCK>, std::string> AcquireOperationLock(
        const fs::path& aDirectory, const std::string& aOperation )
{
    const fs::path path = aDirectory / ( aOperation + ".mfg-lock" );
    if( auto regular = RegularOrMissing( path ); !regular ) return tl::unexpected( regular.error() );
    auto lock = std::make_unique<KIPLATFORM::IO::FILE_LOCK>();
    bool created = false;
    if( lock->Acquire( WxPath( path ), created ) != KIPLATFORM::IO::FILE_LOCK::STATE::HELD )
        return tl::unexpected( "manufacturing_journal_busy" );
    if( auto regular = RegularOrMissing( path ); !regular || !*regular )
        return tl::unexpected( "manufacturing_journal_unavailable" );
    return lock;
}

std::string JournalHash( PcbManufacturingJournalRecord aRecord )
{
    aRecord.mutable_receipt()->clear_durable_journal_sha256();
    return Hash( aRecord.SerializeAsString() );
}
}

PCB_MANUFACTURING_JOB_MANAGER::PCB_MANUFACTURING_JOB_MANAGER( const wxString& aDirectory ) :
        m_journalDirectory( aDirectory )
{
    // Construction is read-only. First admission creates the dedicated root
    // only after target identity and live lifecycle validation have succeeded.
}

PCB_MANUFACTURING_JOB_MANAGER::~PCB_MANUFACTURING_JOB_MANAGER()
{
    // A manager owns every worker it starts.  Move the handles out while the
    // registry is locked, then join without holding m_mutex: a worker may be
    // finishing a durable receipt and therefore needs the same mutex.
    std::vector<std::pair<std::string, std::shared_ptr<WORKER>>> workers;
    {
        std::lock_guard lock( m_mutex );
        for( auto& entry : m_workers )
            workers.emplace_back( entry.first, std::move( entry.second ) );
        m_workers.clear();
    }
    for( auto& entry : workers )
    {
        if( entry.second && entry.second->thread.joinable() )
            entry.second->thread.join();
        // The object is being destroyed, so failure to rewrite a receipt is
        // intentionally ignored.  A normal caller uses Join and receives the
        // durable worker_joined evidence before releasing the manager.
        auto journal = read( entry.first );
        if( journal && journal->receipt().worker_started()
            && journal->receipt().worker_finished() && !journal->receipt().worker_joined() )
            (void) MarkWorkerJoined( entry.first, journal->receipt().process_epoch() );
    }
}

wxString PCB_MANUFACTURING_JOB_MANAGER::JournalDirectory( const wxString& aSettingsDirectory )
{
    wxFileName root;
    root.AssignDir( aSettingsDirectory );
    root.AppendDir( wxS( "pcb-manufacturing-jobs" ) );
    return root.GetPath();
}

std::string PCB_MANUFACTURING_JOB_MANAGER::pathFor( const std::string& aId ) const
{
    if( !IsUuid( aId ) ) throw std::invalid_argument( "invalid_manufacturing_operation_id" );
    return ( fs::u8path( m_journalDirectory.ToStdString() ) / ( aId + ".mfg-journal" ) ).generic_string();
}

tl::expected<bool, std::string> PCB_MANUFACTURING_JOB_MANAGER::requireDirectory( bool aCreate ) const
{
    const fs::path directory = fs::u8path( m_journalDirectory.ToStdString() );
    if( !directory.is_absolute() || directory != directory.lexically_normal() )
        return tl::unexpected( "invalid_manufacturing_journal_directory" );
    if( auto valid = NoLinks( directory ); !valid ) return tl::unexpected( valid.error() );
    std::error_code error;
    auto status = fs::symlink_status( directory, error );
    if( Missing( error ) || ( !error && status.type() == fs::file_type::not_found ) )
    {
        if( !aCreate ) return false;
        fs::create_directories( directory, error );
        if( error ) return tl::unexpected( "manufacturing_journal_unavailable" );
        if( auto valid = NoLinks( directory ); !valid ) return tl::unexpected( valid.error() );
        status = fs::symlink_status( directory, error );
    }
    if( error || !fs::is_directory( status ) ) return tl::unexpected( "manufacturing_journal_unavailable" );
    return true;
}

bool PCB_MANUFACTURING_JOB_MANAGER::IsUuid( const std::string& aValue )
{
    if( aValue.size() != 36 || aValue == "00000000-0000-0000-0000-000000000000" ) return false;
    for( size_t index = 0; index < aValue.size(); ++index )
    {
        const char c = aValue[index];
        if( index == 8 || index == 13 || index == 18 || index == 23 )
        { if( c != '-' ) return false; }
        else if( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
    }
    return true;
}

bool PCB_MANUFACTURING_JOB_MANAGER::IsDigest( const std::string& aValue )
{
    return aValue.size() == 64 && std::all_of( aValue.begin(), aValue.end(), []( char c )
             { return ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ); } );
}

bool PCB_MANUFACTURING_JOB_MANAGER::HasCompleteSourceSnapshot(
        const kiapi::automation::v1::DocumentLifecycleState& aState )
{
    if( !aState.disk_baseline_checked() || aState.native_files_size() == 0
        || aState.native_files_size() != aState.file_baselines_size() )
        return false;

    for( int index = 0; index < aState.native_files_size(); ++index )
    {
        const auto& baseline = aState.file_baselines( index );
        if( baseline.path() != aState.native_files( index ) || !baseline.baseline_known()
            || !baseline.current_known() || baseline.status() != NFBS_UNCHANGED )
            return false;
        if( baseline.baseline_exists() && !IsDigest( baseline.baseline_sha256() ) )
            return false;
        if( baseline.current_exists() && !IsDigest( baseline.current_sha256() ) )
            return false;
    }
    return true;
}

bool PCB_MANUFACTURING_JOB_MANAGER::SameDocument(
        const kiapi::common::types::DocumentSpecifier& aLeft,
        const kiapi::common::types::DocumentSpecifier& aRight )
{
    return google::protobuf::util::MessageDifferencer::Equals( aLeft, aRight );
}

bool PCB_MANUFACTURING_JOB_MANAGER::ValidDocument( const kiapi::common::types::DocumentSpecifier& aDocument )
{
    const fs::path project = fs::u8path( aDocument.project().path() );
    const fs::path board = fs::u8path( aDocument.board_filename() );
    return aDocument.type() == DocumentType::DOCTYPE_PCB && !aDocument.project().name().empty()
        && project.is_absolute() && !board.empty() && board == board.filename()
        && board.extension() == ".kicad_pcb";
}

bool PCB_MANUFACTURING_JOB_MANAGER::ValidState( const DocumentLifecycleState& aState )
{
    if( !ValidDocument( aState.document() ) || aState.scope() != DLS_PCB
        || !IsUuid( aState.process_epoch() ) || !IsUuid( aState.native_identity() )
        || !aState.has_revision() || !IsUuid( aState.revision().epoch() )
        || !IsDigest( aState.state_sha256() ) || !aState.project_settings_included()
        || aState.native_files_size() == 0 || aState.native_files_size() > 4096
        || aState.file_baselines_size() != aState.native_files_size() ) return false;
    std::set<std::string> files;
    for( int index = 0; index < aState.native_files_size(); ++index )
    {
        const std::string& file = aState.native_files( index );
        if( !fs::u8path( file ).is_absolute() || !files.insert( file ).second
            || aState.file_baselines( index ).path() != file ) return false;
    }
    return true;
}

std::string PCB_MANUFACTURING_JOB_MANAGER::JobKind( const Request& aRequest )
{
    switch( aRequest.job_case() )
    {
    case Request::kExport3D: return "3d";
    case Request::kExportRender: return "render";
    case Request::kExportSvg: return "svg";
    case Request::kExportDxf: return "dxf";
    case Request::kExportPdf: return "pdf";
    case Request::kExportPs: return "ps";
    case Request::kExportGerbers: return "gerbers";
    case Request::kExportDrill: return "drill";
    case Request::kExportPosition: return "position";
    case Request::kExportGencad: return "gencad";
    case Request::kExportIpc2581: return "ipc2581";
    case Request::kExportIpcD356: return "ipc-d356";
    case Request::kExportOdb: return "odb";
    case Request::kExportStats: return "stats";
    default: return {};
    }
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::ValidateRequest( const Request& aRequest )
{
    if( !IsUuid( aRequest.operation_id() ) || !IsUuid( aRequest.process_epoch() )
        || !ValidDocument( aRequest.document() ) || !ValidState( aRequest.expected_state() )
        || !SameDocument( aRequest.document(), aRequest.expected_state().document() )
        || aRequest.process_epoch() != aRequest.expected_state().process_epoch()
        || !fs::u8path( aRequest.output_directory() ).is_absolute() || JobKind( aRequest ).empty()
        || aRequest.ByteSizeLong() > MAX_SOURCE_BYTES )
        return tl::unexpected( "invalid_manufacturing_manager_request" );
    return {};
}

void PCB_MANUFACTURING_JOB_MANAGER::SetUnsupported( Receipt& aReceipt )
{
    aReceipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
    aReceipt.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
    aReceipt.set_terminal( true );
    aReceipt.set_worker_finished( true );
    aReceipt.set_recovery_state( PCB_MFG_RECOVERY_UNSUBMITTED );
    aReceipt.set_error_code( "export_adapter_unavailable" );
    aReceipt.set_error_message( "The detached exporter is unavailable; the admitted request was not dispatched." );
    aReceipt.set_publication_state( PCB_MFG_PUBLICATION_UNSPECIFIED );
    aReceipt.clear_staging_directory();
    aReceipt.clear_artifacts();
    aReceipt.clear_output_manifest_sha256();
    aReceipt.set_output_manifest_complete( false );
    aReceipt.set_publication_committed( false );
}

void PCB_MANUFACTURING_JOB_MANAGER::SetDispatchSubmitted( Receipt& aReceipt )
{
    // native_dispatch records durable dispatch intent.  It does not claim
    // that the adapter has already produced an artifact.
    aReceipt.set_status( PCB_MFG_EXPORT_RUNNING );
    aReceipt.set_dispatch_stage( PCB_MFG_DISPATCH_SUBMITTED );
    aReceipt.set_native_dispatch( true );
    aReceipt.set_export_adapter_available( true );
    aReceipt.set_terminal( false );
    aReceipt.set_worker_finished( false );
    aReceipt.set_recovery_state( PCB_MFG_RECOVERY_ACTIVE );
    aReceipt.set_publication_state( PCB_MFG_PUBLICATION_STAGED );
    aReceipt.set_publication_committed( false );
    aReceipt.set_snapshot_complete( false );
    aReceipt.clear_error_code();
    aReceipt.clear_error_message();
    aReceipt.clear_observed_after();
    aReceipt.clear_artifacts();
    aReceipt.clear_output_manifest_sha256();
    aReceipt.set_output_manifest_complete( false );
}

void PCB_MANUFACTURING_JOB_MANAGER::SetDispatchIndeterminate(
        Receipt& aReceipt, const std::string& aErrorCode, const std::string& aErrorMessage )
{
    // The callback may have run before reporting an error.  Preserve the
    // dispatch intent and close the journal without claiming output, cleanup,
    // publication or a verified post-export lifecycle state.
    aReceipt.set_status( PCB_MFG_EXPORT_INDETERMINATE );
    aReceipt.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
    aReceipt.set_native_dispatch( true );
    aReceipt.set_export_adapter_available( true );
    aReceipt.set_terminal( true );
    aReceipt.set_worker_finished( true );
    aReceipt.set_recovery_state( PCB_MFG_RECOVERY_UNKNOWN );
    aReceipt.set_error_code( aErrorCode );
    aReceipt.set_error_message( aErrorMessage );
    aReceipt.set_publication_state( PCB_MFG_PUBLICATION_UNSPECIFIED );
    aReceipt.set_publication_committed( false );
    aReceipt.set_snapshot_complete( false );
    aReceipt.clear_observed_after();
    aReceipt.clear_staging_directory();
    aReceipt.clear_artifacts();
    aReceipt.clear_output_manifest_sha256();
    aReceipt.set_output_manifest_complete( false );
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::ValidateRecord( const JournalRecord& aRecord )
{
    if( aRecord.format_version() != JOURNAL_VERSION || aRecord.journal_revision() == 0
        || !aRecord.has_request() || !aRecord.has_receipt() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( auto valid = ValidateRequest( aRecord.request() ); !valid )
        return tl::unexpected( "invalid_manufacturing_journal" );
    const Request& request = aRecord.request();
    const Receipt& receipt = aRecord.receipt();
    if( receipt.worker_joined()
        && ( !receipt.worker_started() || !receipt.worker_finished() ) )
        return tl::unexpected( "invalid_manufacturing_journal" );
    const bool sameState = google::protobuf::util::MessageDifferencer::Equals(
            request.expected_state(), receipt.observed_before() )
        && google::protobuf::util::MessageDifferencer::Equals( request.expected_state(), receipt.expected_state() );
    if( receipt.operation_id() != request.operation_id() || receipt.process_epoch() != request.process_epoch()
        || !SameDocument( receipt.document(), request.document() ) || !sameState
        || receipt.request_fingerprint_sha256() != Hash( request.SerializeAsString() )
        || receipt.job_kind() != JobKind( request ) || receipt.output_directory() != request.output_directory()
        || aRecord.source_board().empty() || aRecord.source_board().size() > MAX_SOURCE_BYTES
        || aRecord.source_board().rfind( "(kicad_pcb", 0 ) != 0
        || receipt.source_snapshot_sha256() != Hash( aRecord.source_board() ) )
        return tl::unexpected( "invalid_manufacturing_journal" );

    if( receipt.status() == PCB_MFG_EXPORT_RUNNING || receipt.status() == PCB_MFG_EXPORT_COMPLETED
        || receipt.status() == PCB_MFG_EXPORT_FAILED || receipt.status() == PCB_MFG_EXPORT_INDETERMINATE )
        return ValidateDispatchedReceipt( aRecord, receipt );

    if( receipt.snapshot_complete() || receipt.export_adapter_available()
        || receipt.native_dispatch() || receipt.publication_committed()
        || receipt.publication_state() != PCB_MFG_PUBLICATION_UNSPECIFIED
        || !receipt.staging_directory().empty() || receipt.artifacts_size() != 0
        || receipt.output_manifest_complete() || !receipt.output_manifest_sha256().empty()
        || receipt.has_observed_after() || receipt.dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( receipt.status() == PCB_MFG_EXPORT_ADMITTED )
    {
        if( receipt.terminal() || receipt.worker_finished() || receipt.cancellation_requested()
            || receipt.recovery_state() != PCB_MFG_RECOVERY_ACTIVE || !receipt.error_code().empty()
            || !receipt.error_message().empty() ) return tl::unexpected( "invalid_manufacturing_journal" );
    }
    else if( receipt.status() == PCB_MFG_EXPORT_UNSUBMITTED )
    {
        if( !receipt.terminal() || !receipt.worker_finished() || receipt.error_message().empty() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        if( receipt.recovery_state() == PCB_MFG_RECOVERY_UNSUBMITTED )
        {
            if( receipt.cancellation_requested()
                || ( receipt.error_code() != "export_adapter_unavailable"
                     && receipt.error_code() != "manufacturing_worker_lost_before_dispatch"
                     && receipt.error_code() != "manufacturing_worker_exception_before_dispatch" ) )
                return tl::unexpected( "invalid_manufacturing_journal" );
        }
        else if( receipt.recovery_state() == PCB_MFG_RECOVERY_CANCELLED )
        {
            if( !receipt.cancellation_requested() || receipt.error_code() != "export_cancelled_before_dispatch" )
                return tl::unexpected( "invalid_manufacturing_journal" );
        }
        else return tl::unexpected( "invalid_manufacturing_journal" );
    }
    else return tl::unexpected( "manufacturing_journal_phase_unsupported" );
    return {};
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::ValidateDispatchedReceipt(
        const JournalRecord& aRecord, const Receipt& aReceipt )
{
    const Request& request = aRecord.request();
    if( !aReceipt.native_dispatch() || !aReceipt.export_adapter_available()
        || ( aReceipt.snapshot_complete()
             && !HasCompleteSourceSnapshot( aRecord.receipt().observed_before() ) )
        || aReceipt.source_snapshot_sha256() != Hash( aRecord.source_board() )
        || aReceipt.operation_id() != request.operation_id()
        || aReceipt.process_epoch() != request.process_epoch()
        || !SameDocument( aReceipt.document(), request.document() )
        || aReceipt.request_fingerprint_sha256() != Hash( request.SerializeAsString() )
        || aReceipt.job_kind() != JobKind( request )
        || aReceipt.output_directory() != request.output_directory()
        || !google::protobuf::util::MessageDifferencer::Equals(
                aReceipt.expected_state(), request.expected_state() )
        || !google::protobuf::util::MessageDifferencer::Equals(
                aReceipt.observed_before(), request.expected_state() ) )
        return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );

    if( aReceipt.has_observed_after()
        && ( !ValidState( aReceipt.observed_after() )
             || !google::protobuf::util::MessageDifferencer::Equals(
                     aReceipt.observed_after(), request.expected_state() ) ) )
        return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );

    switch( aReceipt.status() )
    {
    case PCB_MFG_EXPORT_RUNNING:
        if( aReceipt.terminal() || aReceipt.worker_finished()
            || aReceipt.dispatch_stage() != PCB_MFG_DISPATCH_SUBMITTED
            || ( aReceipt.cancellation_requested()
                 ? aReceipt.recovery_state() != PCB_MFG_RECOVERY_CANCEL_REQUESTED
                 : aReceipt.recovery_state() != PCB_MFG_RECOVERY_ACTIVE )
            || aReceipt.worker_joined()
            || aReceipt.has_observed_after() || aReceipt.publication_committed()
            || aReceipt.publication_state() == PCB_MFG_PUBLICATION_PUBLISHED
            || !aReceipt.error_code().empty() || !aReceipt.error_message().empty() )
            return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );
        break;
    case PCB_MFG_EXPORT_COMPLETED:
        if( !aReceipt.terminal() || !aReceipt.worker_finished()
            || aReceipt.dispatch_stage() != PCB_MFG_DISPATCH_TERMINAL
            || !aReceipt.has_observed_after()
            || aReceipt.publication_state() != PCB_MFG_PUBLICATION_PUBLISHED
            || !aReceipt.publication_committed() || !aReceipt.output_manifest_complete()
            || !IsDigest( aReceipt.output_manifest_sha256() ) || aReceipt.artifacts_size() == 0
            || !aReceipt.error_code().empty() || !aReceipt.error_message().empty() )
            return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );
        break;
    case PCB_MFG_EXPORT_FAILED:
    case PCB_MFG_EXPORT_INDETERMINATE:
        if( !aReceipt.terminal() || !aReceipt.worker_finished()
            || aReceipt.dispatch_stage() != PCB_MFG_DISPATCH_TERMINAL
            || aReceipt.error_code().empty() || aReceipt.error_message().empty()
            || aReceipt.publication_committed() )
            return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );
        break;
    default:
        return tl::unexpected( "invalid_manufacturing_dispatch_receipt" );
    }
    return {};
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::JournalRecord, std::string>
PCB_MANUFACTURING_JOB_MANAGER::read( const std::string& aId ) const
{
    if( !IsUuid( aId ) ) return tl::unexpected( "invalid_manufacturing_operation_id" );
    if( auto root = requireDirectory( false ); !root ) return tl::unexpected( root.error() );
    else if( !*root ) return tl::unexpected( "journal_not_found" );
    auto bytes = ReadBytes( fs::u8path( pathFor( aId ) ) );
    if( !bytes ) return tl::unexpected( bytes.error() );
    JournalRecord result;
    if( !result.ParseFromString( *bytes ) || result.request().operation_id() != aId
        || !IsDigest( result.receipt().durable_journal_sha256() )
        || JournalHash( result ) != result.receipt().durable_journal_sha256() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    if( auto valid = ValidateRecord( result ); !valid ) return tl::unexpected( valid.error() );
    return result;
}

tl::expected<void, std::string> PCB_MANUFACTURING_JOB_MANAGER::write( JournalRecord& aRecord ) const
{
    if( auto valid = ValidateRecord( aRecord ); !valid ) return tl::unexpected( valid.error() );
    if( auto root = requireDirectory( true ); !root ) return tl::unexpected( root.error() );
    const fs::path path = fs::u8path( pathFor( aRecord.request().operation_id() ) );
    if( auto regular = RegularOrMissing( path ); !regular ) return tl::unexpected( regular.error() );
    aRecord.mutable_receipt()->set_durable_journal_sha256( JournalHash( aRecord ) );
    const std::string bytes = aRecord.SerializeAsString();
    if( bytes.size() > MAX_JOURNAL_BYTES ) return tl::unexpected( "manufacturing_journal_oversized" );
    wxString error;
    if( !KIPLATFORM::IO::AtomicWriteFile( WxPath( path ), bytes.data(), bytes.size(), &error ) )
        return tl::unexpected( "manufacturing_journal_write_unconfirmed: " + error.ToStdString() );
    auto persisted = read( aRecord.request().operation_id() );
    if( !persisted || persisted->SerializeAsString() != bytes )
        return tl::unexpected( "manufacturing_journal_write_unconfirmed" );
    aRecord.Swap( &*persisted );
    return {};
}

bool PCB_MANUFACTURING_JOB_MANAGER::TestAdmissionBarrier()
{
    const char* value = std::getenv( "KICAD_MANUFACTURING_TEST_BARRIER" );
    return value && std::string( value ) == "after-admission";
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Start( const Request& aRequest, const std::string& aEpoch,
                                    const CAPTURE_SOURCE& aCapture, bool aLeaveAdmitted )
{
    std::lock_guard lock( m_mutex );
    if( auto valid = ValidateRequest( aRequest ); !valid ) return tl::unexpected( valid.error() );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch )
        return tl::unexpected( "stale_process_epoch" );
    if( auto prior = read( aRequest.operation_id() ); prior )
    {
        if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
            return tl::unexpected( "manufacturing_operation_reused" );
        return prior->receipt();
    }
    else if( prior.error() != "journal_not_found" ) return tl::unexpected( prior.error() );
    if( !aCapture ) return tl::unexpected( "manufacturing_capture_unavailable" );
    auto captured = aCapture( aRequest.document() );
    if( !captured ) return tl::unexpected( captured.error() );
    if( !ValidState( captured->before ) || !ValidState( captured->after )
        || !google::protobuf::util::MessageDifferencer::Equals( captured->before, aRequest.expected_state() )
        || !google::protobuf::util::MessageDifferencer::Equals( captured->before, captured->after ) )
        return tl::unexpected( "pcb_export_state_changed" );
    if( captured->sourceBoard.empty() || captured->sourceBoard.size() > MAX_SOURCE_BYTES
        || captured->sourceBoard.rfind( "(kicad_pcb", 0 ) != 0 )
        return tl::unexpected( "manufacturing_capture_incomplete" );
    if( auto root = requireDirectory( true ); !root ) return tl::unexpected( root.error() );
    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    // A different cooperating process may have admitted while capture ran.
    if( auto prior = read( aRequest.operation_id() ); prior )
    {
        if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
            return tl::unexpected( "manufacturing_operation_reused" );
        return prior->receipt();
    }
    else if( prior.error() != "journal_not_found" ) return tl::unexpected( prior.error() );

    JournalRecord journal;
    journal.set_format_version( JOURNAL_VERSION );
    journal.mutable_request()->CopyFrom( aRequest );
    journal.set_source_board( captured->sourceBoard );
    journal.set_journal_revision( 1 );
    Receipt& receipt = *journal.mutable_receipt();
    receipt.set_operation_id( aRequest.operation_id() );
    receipt.set_process_epoch( aEpoch );
    receipt.mutable_document()->CopyFrom( aRequest.document() );
    receipt.set_request_fingerprint_sha256( Hash( aRequest.SerializeAsString() ) );
    receipt.set_source_snapshot_sha256( Hash( captured->sourceBoard ) );
    receipt.set_job_kind( JobKind( aRequest ) );
    receipt.set_status( PCB_MFG_EXPORT_ADMITTED );
    receipt.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
    receipt.mutable_expected_state()->CopyFrom( aRequest.expected_state() );
    receipt.mutable_observed_before()->CopyFrom( captured->before );
    receipt.set_output_directory( aRequest.output_directory() );
    receipt.set_recovery_state( PCB_MFG_RECOVERY_ACTIVE );
    // No staging directory, source dependency set or exporter exists yet.
    receipt.set_publication_state( PCB_MFG_PUBLICATION_UNSPECIFIED );
    if( auto persisted = write( journal ); !persisted ) return tl::unexpected( persisted.error() );
    // Production callers that have an owner-thread adapter dispatch the
    // admitted journal immediately after this return.  The default preserves
    // the fail-closed admission-only contract, and the test barrier remains a
    // deterministic way to exercise the recovery seam without an adapter.
    if( !aLeaveAdmitted && !TestAdmissionBarrier() )
    {
        SetUnsupported( *journal.mutable_receipt() );
        journal.set_journal_revision( 2 );
        if( auto persisted = write( journal ); !persisted ) return tl::unexpected( persisted.error() );
    }
    return journal.receipt();
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Dispatch( const Request& aRequest, const std::string& aEpoch,
                                          const DISPATCH_ADAPTER& aAdapter,
                                          const CANCELLATION& aCancellation, bool aDetachedWorker )
{
    if( !aAdapter ) return tl::unexpected( "manufacturing_dispatch_adapter_unavailable" );

    // The operation lock protects admission and the submitted transition. A
    // callback must run without the C++ mutex because owner-thread exporters
    // may observe unrelated manager state, but the operation-file lease stays
    // held across the callback so another process cannot replace the journal.
    std::unique_lock lock( m_mutex );
    if( auto valid = ValidateRequest( aRequest ); !valid ) return tl::unexpected( valid.error() );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch )
        return tl::unexpected( "stale_process_epoch" );
    auto prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
        return tl::unexpected( "manufacturing_operation_reused" );
    if( prior->receipt().terminal() || prior->receipt().status() != PCB_MFG_EXPORT_ADMITTED
        || prior->receipt().dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "manufacturing_dispatch_not_admitted" );

    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
        return tl::unexpected( "manufacturing_operation_reused" );
    if( prior->receipt().terminal() || prior->receipt().status() != PCB_MFG_EXPORT_ADMITTED
        || prior->receipt().dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "manufacturing_dispatch_not_admitted" );

    // Cancellation that wins before dispatch intent is persisted remains a
    // safe pre-dispatch cancellation.  The worker may have been created, but
    // no adapter has been called and the operation can be terminally closed.
    if( aCancellation && aCancellation->IsRequested() )
    {
        JournalRecord cancelled = *prior;
        Receipt& receipt = *cancelled.mutable_receipt();
        receipt.set_cancellation_requested( true );
        receipt.set_recovery_state( PCB_MFG_RECOVERY_CANCELLED );
        receipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
        receipt.set_terminal( true );
        receipt.set_worker_finished( true );
        receipt.set_error_code( "export_cancelled_before_dispatch" );
        receipt.set_error_message( "The worker observed cancellation before native dispatch; no exporter ran." );
        if( cancelled.journal_revision() == std::numeric_limits<uint64_t>::max() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        cancelled.set_journal_revision( cancelled.journal_revision() + 1 );
        if( auto persisted = write( cancelled ); !persisted ) return tl::unexpected( persisted.error() );
        return cancelled.receipt();
    }

    DISPATCH_CONTEXT context;
    context.request.CopyFrom( aRequest );
    context.sourceBoard = prior->source_board();
    context.sourceSnapshotSha256 = prior->receipt().source_snapshot_sha256();
    context.sourceState.CopyFrom( prior->receipt().observed_before() );
    context.cancellation = aCancellation;
    context.detachedWorker = aDetachedWorker;

    // Record dispatch intent before entering native code.  If the process
    // exits or the callback loses its reply after this write, the journal is
    // RUNNING/SUBMITTED and the operation cannot be cancelled or dispatched
    // again by treating it as a fresh admission.
    JournalRecord submitted = *prior;
    SetDispatchSubmitted( *submitted.mutable_receipt() );
    submitted.mutable_receipt()->set_worker_started(
            aDetachedWorker || submitted.receipt().worker_started() );
    if( submitted.journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    submitted.set_journal_revision( submitted.journal_revision() + 1 );
    if( auto persisted = write( submitted ); !persisted ) return tl::unexpected( persisted.error() );

    auto persistIndeterminate = [&]( const std::string& aCode,
                                     const std::string& aMessage )
            -> tl::expected<Receipt, std::string>
    {
        if( !lock.owns_lock() ) lock.lock();
        auto current = read( aRequest.operation_id() );
        if( !current ) return tl::unexpected( current.error() );
        if( current->request().SerializeAsString() != aRequest.SerializeAsString()
            || current->receipt().status() != PCB_MFG_EXPORT_RUNNING
            || current->receipt().dispatch_stage() != PCB_MFG_DISPATCH_SUBMITTED )
            return tl::unexpected( "manufacturing_dispatch_race" );

        JournalRecord candidate = *current;
        SetDispatchIndeterminate( *candidate.mutable_receipt(), aCode, aMessage );
        if( aCode.rfind( "export_cancelled", 0 ) == 0 )
        {
            candidate.mutable_receipt()->set_cancellation_requested( true );
            candidate.mutable_receipt()->set_recovery_state( PCB_MFG_RECOVERY_CANCELLED );
        }
        candidate.mutable_receipt()->set_snapshot_complete(
                aDetachedWorker && HasCompleteSourceSnapshot( current->receipt().observed_before() ) );
        candidate.mutable_receipt()->set_worker_started(
                aDetachedWorker || current->receipt().worker_started() );
        if( candidate.journal_revision() == std::numeric_limits<uint64_t>::max() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        candidate.set_journal_revision( candidate.journal_revision() + 1 );
        if( auto persisted = write( candidate ); !persisted ) return tl::unexpected( persisted.error() );
        return candidate.receipt();
    };

    lock.unlock();
    tl::expected<Receipt, std::string> dispatched = tl::unexpected( "manufacturing_dispatch_failed" );
    if( aCancellation && aCancellation->IsRequested() )
        return persistIndeterminate( "export_cancelled_before_native_dispatch",
                                     "The worker was cancelled after dispatch intent but before native code ran." );
    try
    {
        dispatched = aAdapter( context );
    }
    catch( const std::exception& error )
    {
        return persistIndeterminate( "manufacturing_dispatch_exception",
                                     "The native adapter raised after dispatch intent: "
                                     + std::string( error.what() ) );
    }
    catch( ... )
    {
        return persistIndeterminate( "manufacturing_dispatch_exception",
                                     "The native adapter raised an unknown exception after dispatch intent" );
    }

    // An adapter error is terminally indeterminate. The callback may have
    // reached native code before returning its error, so the journal must not
    // remain ADMITTED and must not permit a second dispatch attempt.
    if( aCancellation && aCancellation->IsRequested() )
        return persistIndeterminate( "export_cancelled_after_dispatch",
                                     "The worker was cancelled after native dispatch; output cannot be assumed safe." );

    if( !dispatched )
        return persistIndeterminate( "manufacturing_dispatch_indeterminate",
                                     "The native adapter failed after dispatch intent: " + dispatched.error() );

    if( !lock.owns_lock() ) lock.lock();
    auto current = read( aRequest.operation_id() );
    if( !current ) return tl::unexpected( current.error() );
    if( current->request().SerializeAsString() != aRequest.SerializeAsString()
        || current->receipt().status() != PCB_MFG_EXPORT_RUNNING
        || current->receipt().dispatch_stage() != PCB_MFG_DISPATCH_SUBMITTED )
        return tl::unexpected( "manufacturing_dispatch_race" );

    JournalRecord candidate = *current;
    candidate.mutable_receipt()->CopyFrom( *dispatched );
    if( aDetachedWorker )
    {
        candidate.mutable_receipt()->set_worker_started( true );
        candidate.mutable_receipt()->set_worker_finished( true );
        candidate.mutable_receipt()->set_snapshot_complete(
                HasCompleteSourceSnapshot( current->receipt().observed_before() ) );
    }
    if( aDetachedWorker && candidate.receipt().status() == PCB_MFG_EXPORT_RUNNING )
    {
        lock.unlock();
        return persistIndeterminate( "manufacturing_worker_exited_running",
                                     "The manager worker exited while the adapter still reported RUNNING." );
    }
    if( auto valid = ValidateDispatchedReceipt( candidate, candidate.receipt() ); !valid )
    {
        lock.unlock();
        return persistIndeterminate( "invalid_manufacturing_dispatch_receipt", valid.error() );
    }
    if( candidate.journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    candidate.set_journal_revision( candidate.journal_revision() + 1 );
    if( auto persisted = write( candidate ); !persisted ) return tl::unexpected( persisted.error() );
    return candidate.receipt();
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::DispatchAsync( const Request& aRequest,
                                               const std::string& aEpoch,
                                               const DISPATCH_ADAPTER& aAdapter )
{
    if( !aAdapter ) return tl::unexpected( "manufacturing_dispatch_adapter_unavailable" );

    auto worker = std::make_shared<WORKER>();
    worker->cancellation = std::make_shared<CANCELLATION_TOKEN>();
    Receipt admitted;

    // Admission of the worker is itself journaled before the thread is made
    // visible.  This gives cancellation and Join a stable operation identity,
    // while the copied context below prevents a worker from observing a live
    // board or mutable request after this critical section.
    std::unique_lock lock( m_mutex );
    if( auto valid = ValidateRequest( aRequest ); !valid ) return tl::unexpected( valid.error() );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch )
        return tl::unexpected( "stale_process_epoch" );
    auto prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
        return tl::unexpected( "manufacturing_operation_reused" );
    if( prior->receipt().terminal() || prior->receipt().status() != PCB_MFG_EXPORT_ADMITTED
        || prior->receipt().dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "manufacturing_dispatch_not_admitted" );
    if( !HasCompleteSourceSnapshot( prior->receipt().observed_before() ) )
        return tl::unexpected( "manufacturing_dependency_snapshot_incomplete" );
    if( m_workers.find( aRequest.operation_id() ) != m_workers.end() )
        return tl::unexpected( "manufacturing_worker_already_started" );

    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ),
                                       aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->request().SerializeAsString() != aRequest.SerializeAsString() )
        return tl::unexpected( "manufacturing_operation_reused" );
    if( prior->receipt().terminal() || prior->receipt().status() != PCB_MFG_EXPORT_ADMITTED
        || prior->receipt().dispatch_stage() != PCB_MFG_DISPATCH_ADMITTED )
        return tl::unexpected( "manufacturing_dispatch_not_admitted" );

    JournalRecord workerAdmission = *prior;
    workerAdmission.mutable_receipt()->set_worker_started( true );
    if( workerAdmission.journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    workerAdmission.set_journal_revision( workerAdmission.journal_revision() + 1 );
    if( auto persisted = write( workerAdmission ); !persisted )
        return tl::unexpected( persisted.error() );
    admitted = workerAdmission.receipt();

    try
    {
        Request request = aRequest;
        const std::string epoch = aEpoch;
        worker->thread = std::thread(
                [this, worker, request = std::move( request ), epoch,
                 adapter = aAdapter]() mutable
                {
                    // Dispatch catches adapter exceptions and durably turns
                    // them into an indeterminate terminal receipt.  No
                    // exception is allowed to escape the thread entrypoint.
                    try
                    {
                        auto result = Dispatch( request, epoch, adapter, worker->cancellation, true );
                        if( !result )
                            (void) FinalizeWorkerIndeterminate(
                                    request.operation_id(), epoch, "manufacturing_worker_dispatch_failed",
                                    "The manager worker could not complete its dispatch boundary: "
                                    + result.error() );
                    }
                    catch( const std::exception& error )
                    {
                        (void) FinalizeWorkerIndeterminate(
                                request.operation_id(), epoch, "manufacturing_worker_exception",
                                "The manager worker raised after admission: " + std::string( error.what() ) );
                    }
                    catch( ... )
                    {
                        (void) FinalizeWorkerIndeterminate(
                                request.operation_id(), epoch, "manufacturing_worker_exception",
                                "The manager worker raised an unknown exception after admission" );
                    }
                } );
    }
    catch( const std::exception& )
    {
        // The worker was never started.  Restore the admitted record so a
        // caller can retry with a new worker without violating dispatch
        // uncertainty semantics.
        JournalRecord restored = workerAdmission;
        restored.mutable_receipt()->set_worker_started( false );
        restored.set_journal_revision( workerAdmission.journal_revision() + 1 );
        if( auto persisted = write( restored ); !persisted )
            return tl::unexpected( persisted.error() );
        return tl::unexpected( "manufacturing_worker_start_failed" );
    }
    m_workers.emplace( aRequest.operation_id(), worker );
    lock.unlock();
    return admitted;
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::MarkWorkerJoined( const std::string& aOperationId,
                                                  const std::string& aProcessEpoch )
{
    std::lock_guard lock( m_mutex );
    auto journal = read( aOperationId );
    if( !journal ) return tl::unexpected( journal.error() );
    if( journal->receipt().process_epoch() != aProcessEpoch )
        return tl::unexpected( "stale_process_epoch" );
    if( !journal->receipt().worker_started() )
        return tl::unexpected( "manufacturing_worker_not_started" );
    if( !journal->receipt().worker_finished() )
        return tl::unexpected( "manufacturing_worker_not_finished" );
    if( journal->receipt().worker_joined() ) return journal->receipt();
    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aOperationId );
    if( !lease ) return tl::unexpected( lease.error() );
    journal = read( aOperationId );
    if( !journal ) return tl::unexpected( journal.error() );
    if( !journal->receipt().worker_finished() )
        return tl::unexpected( "manufacturing_worker_not_finished" );
    journal->mutable_receipt()->set_worker_joined( true );
    if( journal->journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    journal->set_journal_revision( journal->journal_revision() + 1 );
    if( auto persisted = write( *journal ); !persisted ) return tl::unexpected( persisted.error() );
    return journal->receipt();
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::FinalizeWorkerIndeterminate(
        const std::string& aOperationId, const std::string& aProcessEpoch,
        const std::string& aErrorCode, const std::string& aErrorMessage )
{
    std::lock_guard lock( m_mutex );
    auto journal = read( aOperationId );
    if( !journal ) return tl::unexpected( journal.error() );
    if( journal->receipt().process_epoch() != aProcessEpoch )
        return tl::unexpected( "stale_process_epoch" );
    if( journal->receipt().terminal() ) return journal->receipt();
    Receipt& receipt = *journal->mutable_receipt();
    if( receipt.status() == PCB_MFG_EXPORT_RUNNING
        && receipt.dispatch_stage() == PCB_MFG_DISPATCH_SUBMITTED )
    {
        SetDispatchIndeterminate( receipt, aErrorCode, aErrorMessage );
        receipt.set_worker_started( true );
        receipt.set_snapshot_complete( HasCompleteSourceSnapshot( receipt.observed_before() ) );
    }
    else if( receipt.status() == PCB_MFG_EXPORT_ADMITTED
             && receipt.dispatch_stage() == PCB_MFG_DISPATCH_ADMITTED )
    {
        receipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
        receipt.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
        receipt.set_terminal( true );
        receipt.set_worker_finished( true );
        receipt.set_recovery_state( PCB_MFG_RECOVERY_UNSUBMITTED );
        receipt.set_error_code( "manufacturing_worker_exception_before_dispatch" );
        receipt.set_error_message( aErrorMessage );
    }
    else return tl::unexpected( "manufacturing_worker_state_unsupported" );
    if( journal->journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    journal->set_journal_revision( journal->journal_revision() + 1 );
    if( auto persisted = write( *journal ); !persisted ) return tl::unexpected( persisted.error() );
    return journal->receipt();
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Join( const std::string& aOperationId,
                                     const std::string& aProcessEpoch )
{
    if( !IsUuid( aOperationId ) || !IsUuid( aProcessEpoch ) )
        return tl::unexpected( "invalid_manufacturing_join_request" );
    std::shared_ptr<WORKER> worker;
    {
        std::lock_guard lock( m_mutex );
        auto found = m_workers.find( aOperationId );
        if( found == m_workers.end() )
        {
            auto journal = read( aOperationId );
            if( journal && journal->receipt().terminal() ) return journal->receipt();
            return tl::unexpected( "manufacturing_worker_not_found" );
        }
        worker = found->second;
        if( worker->thread.joinable()
            && worker->thread.get_id() == std::this_thread::get_id() )
            return tl::unexpected( "manufacturing_worker_self_join" );
    }
    if( worker && worker->thread.joinable() ) worker->thread.join();
    auto joined = MarkWorkerJoined( aOperationId, aProcessEpoch );
    if( !joined ) return joined;
    std::lock_guard lock( m_mutex );
    m_workers.erase( aOperationId );
    return joined;
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Cancel( const CancelPcbManufacturingExport& aRequest,
                                     const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() ) )
        return tl::unexpected( "invalid_manufacturing_cancel_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    auto prior = read( aRequest.operation_id() );
    if( !prior ) return tl::unexpected( prior.error() );
    if( prior->receipt().process_epoch() != aEpoch
        || !SameDocument( prior->receipt().document(), aRequest.document() ) )
        return tl::unexpected( "manufacturing_cancel_identity_mismatch" );
    if( prior->receipt().terminal() ) return prior->receipt();
    const bool admitted = prior->receipt().status() == PCB_MFG_EXPORT_ADMITTED
        && prior->receipt().dispatch_stage() == PCB_MFG_DISPATCH_ADMITTED;
    const bool running = prior->receipt().status() == PCB_MFG_EXPORT_RUNNING
        && prior->receipt().dispatch_stage() == PCB_MFG_DISPATCH_SUBMITTED;
    if( !admitted && !running ) return tl::unexpected( "manufacturing_cancel_requires_pre_dispatch" );
    if( running && aRequest.before_native_dispatch_only() )
        return tl::unexpected( "manufacturing_cancel_requires_pre_dispatch" );
    if( running )
    {
        auto worker = m_workers.find( aRequest.operation_id() );
        if( worker == m_workers.end() || !worker->second || !worker->second->cancellation )
            return tl::unexpected( "manufacturing_worker_unavailable" );
        worker->second->cancellation->Request();
    }
    auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ), aRequest.operation_id() );
    if( !lease ) return tl::unexpected( lease.error() );
    auto journal = read( aRequest.operation_id() );
    if( !journal ) return tl::unexpected( journal.error() );
    if( journal->receipt().terminal() ) return journal->receipt();
    if( running && ( journal->receipt().status() != PCB_MFG_EXPORT_RUNNING
                     || journal->receipt().dispatch_stage() != PCB_MFG_DISPATCH_SUBMITTED ) )
        return journal->receipt();
    Receipt& receipt = *journal->mutable_receipt();
    if( running )
    {
        // The worker owns the terminal transition.  Persisting the request
        // here makes cancellation observable immediately and gives the
        // adapter a release/acquire token to poll before it returns.
        receipt.set_cancellation_requested( true );
        receipt.set_recovery_state( PCB_MFG_RECOVERY_CANCEL_REQUESTED );
        if( journal->journal_revision() == std::numeric_limits<uint64_t>::max() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        journal->set_journal_revision( journal->journal_revision() + 1 );
        if( auto persisted = write( *journal ); !persisted ) return tl::unexpected( persisted.error() );
        return journal->receipt();
    }
    receipt.set_cancellation_requested( true );
    receipt.set_recovery_state( PCB_MFG_RECOVERY_CANCELLED );
    receipt.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
    receipt.set_terminal( true );
    receipt.set_worker_finished( true );
    receipt.set_error_code( "export_cancelled_before_dispatch" );
    receipt.set_error_message( "The admitted request was cancelled before native dispatch; no exporter ran." );
    if( journal->journal_revision() == std::numeric_limits<uint64_t>::max() )
        return tl::unexpected( "invalid_manufacturing_journal" );
    journal->set_journal_revision( journal->journal_revision() + 1 );
    if( auto persisted = write( *journal ); !persisted ) return tl::unexpected( persisted.error() );
    return journal->receipt();
}

tl::expected<PcbManufacturingExportReceiptResponse, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Read( const ReadPcbManufacturingExportJob& aRequest,
                                   const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() )
        || !IsDigest( aRequest.request_fingerprint_sha256() ) )
        return tl::unexpected( "invalid_manufacturing_read_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    PcbManufacturingExportReceiptResponse response;
    response.set_reconciliation_supported( true );
    response.set_observing_process_epoch( aEpoch );
    auto journal = read( aRequest.operation_id() );
    if( !journal )
    {
        if( journal.error() == "journal_not_found" ) return response;
        return tl::unexpected( journal.error() );
    }
    const Receipt& receipt = journal->receipt();
    if( receipt.process_epoch() != aEpoch || !SameDocument( receipt.document(), aRequest.document() )
        || receipt.request_fingerprint_sha256() != aRequest.request_fingerprint_sha256() )
        return tl::unexpected( "manufacturing_read_identity_mismatch" );
    response.set_found( true );
    response.set_request_fingerprint_verified( true );
    response.set_journal_verified( true );
    response.mutable_receipt()->CopyFrom( receipt );
    return response;
}

tl::expected<PcbManufacturingExportReceiptResponse, std::string>
PCB_MANUFACTURING_JOB_MANAGER::Reconcile( const ReconcilePcbManufacturingExport& aRequest,
                                        const std::string& aEpoch )
{
    std::lock_guard lock( m_mutex );
    if( !IsUuid( aRequest.operation_id() ) || !ValidDocument( aRequest.document() )
        || !IsDigest( aRequest.request_fingerprint_sha256() )
        || aRequest.allow_prior_process_epoch() != !aRequest.prior_process_epoch().empty()
        || ( aRequest.allow_prior_process_epoch() && !IsUuid( aRequest.prior_process_epoch() ) ) )
        return tl::unexpected( "invalid_manufacturing_reconcile_request" );
    if( !IsUuid( aEpoch ) || aRequest.process_epoch() != aEpoch ) return tl::unexpected( "stale_process_epoch" );
    PcbManufacturingExportReceiptResponse response;
    response.set_reconciliation_supported( true );
    response.set_observing_process_epoch( aEpoch );
    auto journal = read( aRequest.operation_id() );
    if( !journal )
    {
        if( journal.error() == "journal_not_found" ) return response;
        return tl::unexpected( journal.error() );
    }
    const Receipt& receipt = journal->receipt();
    const std::string expectedEpoch = aRequest.allow_prior_process_epoch()
        ? aRequest.prior_process_epoch() : aEpoch;
    if( receipt.process_epoch() != expectedEpoch || !SameDocument( receipt.document(), aRequest.document() )
        || receipt.request_fingerprint_sha256() != aRequest.request_fingerprint_sha256() )
        return tl::unexpected( "manufacturing_reconcile_identity_mismatch" );

    // A prior process cannot safely resume a worker or infer whether native
    // code reached the exporter.  Reconcile therefore performs one explicit,
    // identity-bound terminal transition for a prior RUNNING worker.  An
    // admitted worker that never reached dispatch is safely classified as
    // unsubmitted and may be re-admitted by a fresh operation.  Existing
    // terminal receipts remain immutable and are only inspected.
    if( aRequest.allow_prior_process_epoch() && expectedEpoch != aEpoch && !receipt.terminal() )
    {
        auto lease = AcquireOperationLock( fs::u8path( m_journalDirectory.ToStdString() ),
                                           aRequest.operation_id() );
        if( !lease ) return tl::unexpected( lease.error() );
        auto current = read( aRequest.operation_id() );
        if( !current ) return tl::unexpected( current.error() );
        if( current->receipt().process_epoch() != expectedEpoch
            || current->receipt().request_fingerprint_sha256() != aRequest.request_fingerprint_sha256()
            || !SameDocument( current->receipt().document(), aRequest.document() ) )
            return tl::unexpected( "manufacturing_reconcile_identity_mismatch" );

        Receipt& recovered = *current->mutable_receipt();
        if( recovered.status() == PCB_MFG_EXPORT_RUNNING
            && recovered.dispatch_stage() == PCB_MFG_DISPATCH_SUBMITTED )
        {
            recovered.set_status( PCB_MFG_EXPORT_INDETERMINATE );
            recovered.set_dispatch_stage( PCB_MFG_DISPATCH_TERMINAL );
            recovered.set_terminal( true );
            recovered.set_worker_finished( true );
            recovered.set_recovery_state( PCB_MFG_RECOVERY_RECONCILED );
            recovered.set_error_code( "manufacturing_worker_lost_after_restart" );
            recovered.set_error_message(
                    "The admitting process ended while export was dispatched; output must be reconciled manually." );
        }
        else if( recovered.status() == PCB_MFG_EXPORT_ADMITTED
                 && recovered.dispatch_stage() == PCB_MFG_DISPATCH_ADMITTED )
        {
            recovered.set_status( PCB_MFG_EXPORT_UNSUBMITTED );
            recovered.set_dispatch_stage( PCB_MFG_DISPATCH_ADMITTED );
            recovered.set_terminal( true );
            recovered.set_worker_finished( true );
            recovered.set_recovery_state( PCB_MFG_RECOVERY_UNSUBMITTED );
            recovered.set_error_code( "manufacturing_worker_lost_before_dispatch" );
            recovered.set_error_message(
                    "The admitting process ended before native dispatch; no exporter output was assumed." );
        }
        else return tl::unexpected( "manufacturing_reconcile_state_unsupported" );
        recovered.set_worker_joined( false );
        if( current->journal_revision() == std::numeric_limits<uint64_t>::max() )
            return tl::unexpected( "invalid_manufacturing_journal" );
        current->set_journal_revision( current->journal_revision() + 1 );
        if( auto persisted = write( *current ); !persisted ) return tl::unexpected( persisted.error() );
        journal = std::move( current );
        response.set_restart_reconciled( true );
    }

    response.set_found( true );
    response.set_request_fingerprint_verified( true );
    response.set_journal_verified( true );
    response.set_prior_process_epoch_verified( aRequest.allow_prior_process_epoch() );
    response.mutable_receipt()->CopyFrom( journal->receipt() );
    return response;
}

PcbManufacturingManagerCapabilities PCB_MANUFACTURING_JOB_MANAGER::Capabilities(
        const kiapi::common::types::DocumentSpecifier& aDocument, const std::string& aEpoch ) const
{
    PcbManufacturingManagerCapabilities response;
    response.mutable_document()->CopyFrom( aDocument );
    response.set_process_epoch( aEpoch );
    response.set_admission_supported( true );
    response.set_request_journal_supported( true );
    response.set_journal_read_supported( true );
    response.set_cancellation_before_dispatch_supported( true );
    response.set_prior_epoch_journal_inspection_supported( true );
    response.set_synchronous_svg_export_supported( true );
    response.set_manager_worker_boundary_supported( true );
    response.set_cooperative_worker_cancellation_supported( true );
    response.set_worker_join_supported( true );
    response.set_unavailable_reason(
            "synchronous SVG owner-thread dispatch is available; the manager worker boundary is available "
            "for adapters with complete immutable dependency snapshots, while detached production export and "
            "restart publication remain unavailable" );
    return response;
}
