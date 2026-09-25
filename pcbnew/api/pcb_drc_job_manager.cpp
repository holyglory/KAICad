/* Native asynchronous PCB DRC job ownership. GPL-3.0-or-later. */
#include "pcb_drc_job_manager.h"
#include "pcb_drc_run_inputs.h"
#include "pcb_drc_schematic_input.h"

#include <board.h>
#include <board_design_settings.h>
#include <footprint.h>
#include <footprint_library_adapter.h>
#include <libraries/library_manager.h>
#include <libraries/library_table.h>
#include <project.h>
#include <project/project_file.h>
#include <filename_resolver.h>
#include <pgm_base.h>
#include <wx/fswatcher.h>
#include <wx/evtloop.h>
#include <wx/log.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <drc/drc_library_inputs.h>
#include <drc/drc_run_scope.h>
#include <pcb_marker.h>
#include <progress_reporter.h>
#include <google/protobuf/util/message_differencer.h>

#include <atomic>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <functional>
#include <map>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <vector>
#include <set>

using namespace kiapi::automation::v1;
using kiapi::common::types::DocumentSpecifier;

namespace
{
bool SameDocument( const DocumentSpecifier& aLeft, const DocumentSpecifier& aRight )
{
    return google::protobuf::util::MessageDifferencer::Equals( aLeft, aRight );
}
}

namespace
{
class JOB_PROGRESS : public PROGRESS_REPORTER
{
public:
    JOB_PROGRESS() = default;

    void SetNumPhases( int ) override { }
    void AddPhases( int ) override { }
    void BeginPhase( int ) override { }
    void AdvancePhase() override { }
    void AdvancePhase( const wxString& aMessage ) override
    {
        std::lock_guard lock( m_mutex );
        m_phase = aMessage.ToStdString();
    }
    void Report( const wxString& aMessage ) override
    {
        std::lock_guard lock( m_mutex );
        m_phase = aMessage.ToStdString();
    }
    void SetCurrentProgress( double aProgress ) override
    { if( std::isfinite( aProgress ) ) m_progress.store( std::clamp( aProgress, 0.0, 0.99 ) ); }
    void SetMaxProgress( int ) override { }
    void AdvanceProgress() override { }
    bool KeepRefreshing( bool = false ) override { return !m_cancelled.load(); }
    void SetTitle( const wxString& ) override { }
    bool IsCancelled() const override { return m_cancelled.load(); }

    void Cancel() { m_cancelled.store( true ); }
    double Progress() const { return m_progress.load(); }
    std::string Phase() const
    {
        std::lock_guard lock( m_mutex );
        return m_phase;
    }

private:
    mutable std::mutex m_mutex;
    std::atomic_bool m_cancelled = false;
    std::atomic<double> m_progress = 0.0;
    std::string m_phase;
};
}

struct PCB_DRC_JOB_MANAGER::JOB
{
    StartPcbDrcJob request;
    std::string id;
    std::string operationId;
    DocumentSpecifier document;
    std::string processEpoch;
    std::string checkedBoardEpoch;
    int checkedSequence = 0;
    bool refillZones = false;
    bool reportAllTrackErrors = false;
    bool testFootprints = false;
    DocumentLifecycleState schematicState;
    PCB_DRC_PROJECT_BASELINE projectBaseline;
    std::string libraryFingerprint;
    std::map<wxString, std::string> libraryFingerprints;
    std::string auxiliaryFingerprint;
    bool hasLibraryDependencies = false;
    bool candidateDryRun = false;
    std::vector<KIID> candidateItemIds;
    std::vector<std::string> inputWarnings;
    PcbDrcJobStatus status = PDRCJS_QUEUED;
    double progress = 0.0;
    std::string phase;
    std::string errorCode;
    std::string errorMessage;
    bool resultsFresh = false;
    bool workerFinished = false;
    bool invalidated = false;
    std::vector<PcbDrcFinding> findings;
    std::unique_ptr<JOB_PROGRESS> reporter;
    std::thread worker;
    mutable std::mutex mutex;
};

namespace
{
constexpr int NATIVE_FILE_EVENTS = wxFSW_EVENT_CREATE | wxFSW_EVENT_DELETE | wxFSW_EVENT_RENAME
                                   | wxFSW_EVENT_MODIFY | wxFSW_EVENT_ATTRIB | wxFSW_EVENT_WARNING
                                   | wxFSW_EVENT_ERROR;

// Normalized absolute path without a trailing separator, so a directory named by a
// notification compares equal to the same directory named by a watch.
wxString NormalizedPath( wxFileName aName )
{
    aName.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );
    wxString path = aName.GetFullPath();
    while( path.length() > 1 && wxFileName::IsPathSeparator( path.Last() ) ) path.RemoveLast();
    return path;
}

wxString NormalizedPath( const wxString& aPath ) { return NormalizedPath( wxFileName( aPath ) ); }

bool Within( const wxString& aPath, const wxString& aDirectory )
{
    return aPath.StartsWith( aDirectory + wxFileName::GetPathSeparator() );
}

std::string ChangeMessage( const std::string& aCode )
{
    if( aCode == "project_inputs_changed" )
        return "Project settings, exclusions or custom rules changed or could not be observed";
    if( aCode == "library_inputs_changed" )
        return "Footprint library inputs changed or could not be observed";
    if( aCode == "auxiliary_inputs_changed" )
        return "Drawing-sheet or router inputs changed or could not be observed";
    return "A native DRC input changed or could not be observed";
}

const char* const LOST_EVENTS_MESSAGE =
        "Native input notifications were lost; start a new job from a fresh capture";
}

// One native file subscription for every receipt of this owner. A single kernel
// queue serves all of them, so an overflow concerns every receipt that relied on it.
// wx delivers these notifications on the owner thread, never to a worker.
struct PCB_DRC_JOB_MANAGER::FILE_EVENTS : wxEvtHandler
{
    struct PATH
    {
        bool tree = false;
        int references = 0;
    };

    PCB_DRC_JOB_MANAGER& owner;
    std::unique_ptr<wxFileSystemWatcher> native;
    std::map<wxString, PATH> paths;
    // Receipts remember the generation they subscribed under; a reset makes their
    // later releases harmless for subscriptions that newer receipts own.
    uint64_t generation = 1;
    bool retired = false;

    explicit FILE_EVENTS( PCB_DRC_JOB_MANAGER& aOwner ) : owner( aOwner )
    {
        Bind( wxEVT_FSWATCHER, &FILE_EVENTS::onEvent, this );
    }

    ~FILE_EVENTS() override { reset(); }

    void reset()
    {
        wxLogNull quiet; // A watch the kernel already dropped is not a user-facing error.
        if( native )
        {
            native->SetOwner( nullptr );
            native.reset();
        }
        DeletePendingEvents();
        paths.clear();
        ++generation;
        retired = false;
    }

    // Safe inside a native notification: the watcher is replaced at the next
    // owner call, never while it is dispatching its own events.
    void retire() { retired = true; }

    bool acquire( const wxString& aDirectory, bool aTree )
    {
        if( retired ) return false; // Replaced by the owner before new receipts subscribe.
        auto found = paths.find( aDirectory );
        if( found != paths.end() )
        {
            if( found->second.tree != aTree ) return false;
            ++found->second.references;
            return true;
        }
        // Failed registration is reported as incomplete coverage on the job, not
        // as a modal error in the editor.
        wxLogNull quiet;
        if( !native )
        {
            native = std::make_unique<wxFileSystemWatcher>();
            native->SetOwner( this );
        }
        const wxFileName directory = wxFileName::DirName( aDirectory );
        if( !( aTree ? native->AddTree( directory, NATIVE_FILE_EVENTS )
                     : native->Add( directory, NATIVE_FILE_EVENTS ) ) )
            return false;
        paths.emplace( aDirectory, PATH{ aTree, 1 } );
        return true;
    }

    void release( const wxString& aDirectory, uint64_t aGeneration )
    {
        if( aGeneration != generation ) return;
        auto found = paths.find( aDirectory );
        if( found == paths.end() || --found->second.references > 0 ) return;
        wxLogNull quiet;
        const wxFileName directory = wxFileName::DirName( aDirectory );
        if( native ) found->second.tree ? native->RemoveTree( directory ) : native->Remove( directory );
        paths.erase( found );
    }

    void onEvent( wxFileSystemWatcherEvent& aEvent ) { owner.fileEvent( aEvent ); }
};

// Native owner-thread subscriptions of one receipt. The worker owns neither this
// object nor any callback into the live board, project, library adapter or editor.
struct PCB_DRC_JOB_MANAGER::INPUT_WATCHER : BOARD_LISTENER
{
    struct FILE_INPUT
    {
        wxString path;
        bool tree = false;              // A library directory: nested entries are inputs.
        std::string code;
        FILE_CONTENT_BASELINE baseline; // Single non-library files.
        std::set<wxString> libraries;   // Library nicknames whose content this path provides.
    };
    using LIBRARY_CONTENT = std::function<std::map<wxString, std::string>( const std::set<wxString>& )>;

    PCB_DRC_JOB_MANAGER& owner;
    BOARD& board;
    std::weak_ptr<JOB> job;
    std::vector<FILE_INPUT> inputs;
    std::set<wxString> subscriptions;
    uint64_t generation = 0;
    LIBRARY_CONTENT libraryContent;
    bool covered = false;

    INPUT_WATCHER( PCB_DRC_JOB_MANAGER& aOwner, BOARD& aBoard ) : owner( aOwner ), board( aBoard )
    {
        board.AddListener( this );
    }

    ~INPUT_WATCHER() override
    {
        if( owner.m_files )
            for( const auto& directory : subscriptions ) owner.m_files->release( directory, generation );
        board.RemoveListener( this );
    }

    void changed()
    {
        if( auto receipt = job.lock() )
            invalidate( receipt, "document_changed", "The live PCB changed after DRC capture" );
    }

    void OnBoardItemAdded( BOARD&, BOARD_ITEM* ) override { changed(); }
    void OnBoardItemsAdded( BOARD&, std::vector<BOARD_ITEM*>& items ) override
    { if( !items.empty() ) changed(); }
    void OnBoardItemRemoved( BOARD&, BOARD_ITEM* ) override { changed(); }
    void OnBoardItemsRemoved( BOARD&, std::vector<BOARD_ITEM*>& items ) override
    { if( !items.empty() ) changed(); }
    void OnBoardItemChanged( BOARD&, BOARD_ITEM* ) override { changed(); }
    void OnBoardItemsChanged( BOARD&, std::vector<BOARD_ITEM*>& items ) override
    { if( !items.empty() ) changed(); }
    void OnBoardNetSettingsChanged( BOARD& ) override { changed(); }
    void OnBoardCompositeUpdate( BOARD&, std::vector<BOARD_ITEM*>& added,
                                std::vector<BOARD_ITEM*>& removed,
                                std::vector<BOARD_ITEM*>& modified ) override
    { if( !added.empty() || !removed.empty() || !modified.empty() ) changed(); }

    void lostEvents()
    {
        covered = false;
        if( auto receipt = job.lock() ) invalidate( receipt, "input_events_lost", LOST_EVENTS_MESSAGE );
    }

    bool subscribe( const wxString& aDirectory, bool aTree )
    {
        if( subscriptions.contains( aDirectory ) ) return true;
        if( !owner.m_files->acquire( aDirectory, aTree ) ) return false;
        subscriptions.insert( aDirectory );
        return true;
    }

    // Observe the nearest existing directory: atomic replacement, deletion and
    // creation of a previously missing input must all reach this receipt.
    // A missing directory chain is watched from its nearest existing ancestor for
    // early invalidation, but it is not complete coverage: entries created inside a
    // new directory would not reach this receipt.
    bool watchParent( const wxString& aPath )
    {
        const wxFileName parent = wxFileName::DirName( wxFileName( aPath ).GetPath() );
        wxFileName directory = parent;
        while( !directory.DirExists() && directory.GetDirCount() ) directory.RemoveLastDir();
        return directory.DirExists() && subscribe( NormalizedPath( directory ), false )
               && parent.DirExists();
    }

    // Notifications name the directory entry that changed, not the target of a
    // symbolic link, so a linked input is not complete coverage.
    static bool linked( const wxString& aPath )
    {
        std::error_code error;
#ifdef __WXMSW__
        const std::filesystem::path path( std::wstring( aPath.wc_str() ) );
#else
        const std::filesystem::path path( aPath.utf8_string() );
#endif
        const auto status = std::filesystem::symlink_status( path, error );
        // A missing input is watched through its directory; that is not a link.
        if( status.type() == std::filesystem::file_type::not_found ) return false;
        return error || status.type() == std::filesystem::file_type::symlink;
    }

    bool addFile( const wxString& aPath, const std::string& aCode )
    {
        if( aPath.empty() ) return true;
        if( !wxFileName( aPath ).IsAbsolute() ) return false;
        FILE_INPUT input;
        input.path = NormalizedPath( aPath );
        input.code = aCode;
        input.baseline = FILE_CONTENT_BASELINE::Read( input.path );
        if( !input.baseline.Known() ) return false;
        inputs.push_back( std::move( input ) );
        return watchParent( inputs.back().path ) && !linked( inputs.back().path );
    }

    bool addLibrary( const wxString& aUri, const std::set<wxString>& aLibraries )
    {
        if( !wxFileName( aUri ).IsAbsolute() ) return false;
        FILE_INPUT input;
        input.path = NormalizedPath( aUri );
        input.code = "library_inputs_changed";
        input.libraries = aLibraries;
        input.tree = wxDirExists( input.path );
        const bool exists = input.tree || wxFileExists( input.path );
        inputs.push_back( input );
        // KiCad library directories are watched as trees. Native AddTree installs
        // notifications; it does not poll their contents. A missing library is
        // watched for its creation, but its later contents would not be.
        return watchParent( input.path ) && ( !input.tree || subscribe( input.path, true ) )
               && exists && !linked( input.path );
    }

    bool matches( const FILE_INPUT& aInput, const wxString& aPath ) const
    {
        return !aPath.empty() && ( aPath == aInput.path || Within( aInput.path, aPath )
                                   || ( aInput.tree && Within( aPath, aInput.path ) ) );
    }

    bool unchanged( const FILE_INPUT& aInput, JOB& aReceipt ) const
    {
        try
        {
            if( aInput.libraries.empty() )
                return aInput.baseline.Check( aInput.path ) == FILE_BASELINE_CHECK::UNCHANGED;
            // Recheck only the libraries this path provides, never every library.
            const auto current = libraryContent( aInput.libraries );
            for( const auto& nickname : aInput.libraries )
            {
                const auto now = current.find( nickname );
                const auto then = aReceipt.libraryFingerprints.find( nickname );
                if( ( now == current.end() ) != ( then == aReceipt.libraryFingerprints.end() )
                    || ( now != current.end() && now->second != then->second ) )
                    return false;
            }
            return true;
        }
        catch( const std::exception& ) { return false; }
    }

    void fileChanged( const wxString& aPath, const wxString& aRenamed )
    {
        auto receipt = job.lock();
        if( !receipt ) return;
        {
            std::lock_guard lock( receipt->mutex );
            if( receipt->invalidated ) return;
        }
        for( const auto& input : inputs )
        {
            if( ( matches( input, aPath ) || matches( input, aRenamed ) ) && !unchanged( input, *receipt ) )
            {
                invalidate( receipt, input.code, ChangeMessage( input.code ) );
                return;
            }
        }
    }
};

void PCB_DRC_JOB_MANAGER::invalidate( const std::shared_ptr<JOB>& job,
                                     const std::string& code, const std::string& message )
{
    std::lock_guard lock( job->mutex );
    if( job->invalidated || ( job->status != PDRCJS_QUEUED && job->status != PDRCJS_RUNNING
                             && job->status != PDRCJS_COMPLETED ) ) return;
    job->invalidated = true;
    job->findings.clear();
    job->resultsFresh = false;
    job->errorCode = code;
    job->errorMessage = message;
    job->reporter->Cancel();
    // Cancellation is requested immediately; STALE acknowledges quiescence.
    if( job->workerFinished ) job->status = PDRCJS_STALE;
}

void PCB_DRC_JOB_MANAGER::EnableNativeEvents( LIBRARY_RESOLVER aResolveLibraries )
{
    m_resolveLibraries = std::move( aResolveLibraries );
    m_eventsEnabled = true;
}

void PCB_DRC_JOB_MANAGER::BoardChanged( const BOARD* board )
{
    for( auto& [id, watch] : m_watches )
        if( &watch->board == board ) watch->changed();
}

void PCB_DRC_JOB_MANAGER::DetachBoard( const BOARD* board )
{
    for( auto it = m_watches.begin(); it != m_watches.end(); )
    {
        if( &it->second->board == board )
        {
            it->second->lostEvents();
            it = m_watches.erase( it );
        }
        else ++it;
    }
}

void PCB_DRC_JOB_MANAGER::InputEventsLost( const BOARD* board )
{
    for( auto& [id, watch] : m_watches )
        if( &watch->board == board ) watch->lostEvents();
}

void PCB_DRC_JOB_MANAGER::fileEvent( wxFileSystemWatcherEvent& event )
{
    const int change = event.GetChangeType();
    bool lost = ( change & ( wxFSW_EVENT_WARNING | wxFSW_EVENT_ERROR ) ) != 0;
#if defined( wxHAS_INOTIFY ) || defined( wxHAVE_FSEVENTS_FILE_NOTIFICATIONS )
    lost |= ( change & wxFSW_EVENT_UNMOUNT ) != 0;
#endif
    const wxString path = event.GetPath().GetFullPath().empty() ? wxString()
                                                                : NormalizedPath( event.GetPath() );
    const wxString renamed = event.GetNewPath().GetFullPath().empty() ? wxString()
                                                                     : NormalizedPath( event.GetNewPath() );
    if( !lost && ( change & ( wxFSW_EVENT_CREATE | wxFSW_EVENT_DELETE | wxFSW_EVENT_RENAME
                              | wxFSW_EVENT_MODIFY | wxFSW_EVENT_ATTRIB ) ) )
    {
        for( auto& [id, watch] : m_watches ) watch->fileChanged( path, renamed );
    }
    // The kernel drops the subscription of a watched directory that is removed or
    // renamed, and nothing would report later changes below it.
    lost |= ( change & ( wxFSW_EVENT_DELETE | wxFSW_EVENT_RENAME ) ) && m_files
            && m_files->paths.contains( path );
    if( lost )
    {
        // Nothing proves that no change was missed: every receipt relying on this
        // queue is stale, and later receipts subscribe again from scratch.
        for( auto& [id, watch] : m_watches )
            if( !watch->subscriptions.empty() ) watch->lostEvents();
        if( m_files ) m_files->retire();
    }
}

void PCB_DRC_JOB_MANAGER::ObserveInputs( BOARD& board, const std::string& epoch,
        const SCHEMATIC_OBSERVER& schematic, const LIBRARY_OBSERVER& libraries )
{
    std::vector<std::shared_ptr<JOB>> receipts;
    {
        std::lock_guard lock( m_mutex );
        for( const auto& [id, job] : m_jobs ) receipts.push_back( job );
    }
    for( const auto& job : receipts )
    {
        {
            std::lock_guard lock( job->mutex );
            if( job->invalidated || job->checkedBoardEpoch != board.m_Uuid.AsStdString() ) continue;
        }
        // Activation/settings notification is a recovery checkpoint, including
        // changes made in another editor of this process. Never revive receipts
        // already invalidated by a change or an overflow.
        state( job, board, epoch, schematic, libraries );
    }
}

void PCB_DRC_JOB_MANAGER::retireWatches()
{
    // Called only outside native callbacks: a stale receipt can never revive, so
    // its board listener and file subscriptions have no further purpose.
    for( auto it = m_watches.begin(); it != m_watches.end(); )
    {
        auto receipt = it->second->job.lock();
        bool retired = !receipt;
        if( receipt )
        {
            std::lock_guard lock( receipt->mutex );
            retired = receipt->invalidated
                      || ( receipt->workerFinished && receipt->status != PDRCJS_COMPLETED );
        }
        if( retired ) it = m_watches.erase( it );
        else ++it;
    }
    if( m_files && m_files->retired ) m_files->reset();
}

std::unique_ptr<PCB_DRC_JOB_MANAGER::INPUT_WATCHER> PCB_DRC_JOB_MANAGER::watchInputs(
        BOARD& board, const PCB_DRC_CAPTURE_CONTEXT& context )
{
    // Only a native event owner installs listeners and detaches before replacing
    // its board. Headless contexts retain the full read-time observation path.
    if( !m_eventsEnabled ) return nullptr;

    // Subscribe before capture: a change after this point is either part of the
    // capture or reaches this receipt as a notification.
    auto watch = std::make_unique<INPUT_WATCHER>( *this, board );
    if( !m_resolveLibraries || !wxEventLoopBase::GetActive() ) return watch;
    if( !m_files ) m_files = std::make_unique<FILE_EVENTS>( *this );
    // After a lost queue every older subscribed receipt is stale; their later
    // releases carry the old generation and cannot touch new subscriptions.
    if( m_files->retired ) m_files->reset();
    watch->generation = m_files->generation;
    // Resolve the current adapter on the owner thread. Project/library reload may
    // replace it while the board and its receipts stay alive.
    watch->libraryContent = [resolver = m_resolveLibraries, &board]( const std::set<wxString>& libraries )
    {
        auto* adapter = resolver( board );
        if( !adapter ) throw std::runtime_error( "Native footprint library owner is unavailable" );
        auto current = DRC_LIBRARY_INPUTS::Capture( board, *adapter, nullptr, &libraries );
        if( !current ) throw std::runtime_error( "Native library observation was cancelled" );
        return current->LibraryFingerprints();
    };
    try
    {
        auto& adapter = context.libraries;
        // Only inputs a new check would read from disk: custom rules, footprint
        // library tables and the libraries of placed footprints. The drawing sheet
        // file is watched because it is the declared source of the in-memory sheet.
        // Project settings are compared in memory on every read; KiCad rewrites the
        // project file on ordinary saves without changing them.
        bool covered = watch->addFile( board.GetDesignRulesPath(), "project_inputs_changed" );
        if( auto* project = board.GetProject() )
        {
            const auto& drawing = project->GetProjectFile().m_BoardDrawingSheetFile;
            if( !drawing.empty() )
            {
                FILENAME_RESOLVER resolver;
                resolver.SetProject( project );
                resolver.SetProgramBase( PgmOrNull() );
                const wxString path = resolver.ResolvePath( drawing, project->GetProjectPath(),
                                                            { board.GetEmbeddedFiles() } );
                covered &= !path.empty() && watch->addFile( path, "auxiliary_inputs_changed" );
            }
        }
        // A project-only owner need not have a global table. The adapter's
        // GlobalTable() asserts that it exists; use the native optional lookup.
        if( auto table = adapter.Manager().Table( adapter.Type(), LIBRARY_TABLE_SCOPE::GLOBAL ) )
            covered &= watch->addFile( ( *table )->Path(), "library_inputs_changed" );
        if( auto table = adapter.ProjectTable() )
            covered &= watch->addFile( ( *table )->Path(), "library_inputs_changed" );
        std::map<wxString, std::set<wxString>> libraries;
        for( const auto* footprint : board.Footprints() )
        {
            const wxString nickname = footprint->GetFPID().GetLibNickname();
            if( nickname.empty() ) continue;
            // A nickname without a row is covered by the library table watches.
            if( auto row = adapter.GetRow( nickname ) )
                libraries[LIBRARY_MANAGER::GetFullURI( *row, true )].insert( nickname );
        }
        for( const auto& [uri, nicknames] : libraries )
            covered &= watch->addLibrary( uri, nicknames );
        watch->covered = covered;
    }
    catch( const std::exception& ) { watch->covered = false; }
    return watch;
}

PCB_DRC_JOB_MANAGER::PCB_DRC_JOB_MANAGER( AUXILIARY_OBSERVER aObserveAuxiliary ) :
        m_observeAuxiliary( std::move( aObserveAuxiliary ) )
{}

PCB_DRC_JOB_MANAGER::~PCB_DRC_JOB_MANAGER()
{
    // Subscriptions go first: none of them may outlive the native watcher, and no
    // native callback may reach a receipt while its worker is being joined.
    m_watches.clear();
    m_files.reset();
    std::vector<std::shared_ptr<JOB>> jobs;
    {
        std::lock_guard lock( m_mutex );
        for( auto& [id, job] : m_jobs ) jobs.push_back( job );
    }
    for( const auto& job : jobs ) job->reporter->Cancel();
    for( const auto& job : jobs )
        if( job->worker.joinable() ) job->worker.join();
}

std::shared_ptr<PCB_DRC_JOB_MANAGER::JOB> PCB_DRC_JOB_MANAGER::find( const std::string& aJobId ) const
{
    std::lock_guard lock( m_mutex );
    auto it = m_jobs.find( aJobId );
    return it == m_jobs.end() ? nullptr : it->second;
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::state(
        const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch,
        const SCHEMATIC_OBSERVER& aObserveSchematic,
        const LIBRARY_OBSERVER& aObserveLibraries ) const
{
    if( !aJob ) return tl::unexpected( "Unknown PCB DRC job" );
    if( aJob->processEpoch != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before reading this DRC job" );
    bool observeInputs;
    {
        std::lock_guard lock( aJob->mutex );
        observeInputs = !aJob->invalidated && ( aJob->status == PDRCJS_QUEUED
                || aJob->status == PDRCJS_RUNNING || aJob->status == PDRCJS_COMPLETED );
    }
    // Immutable source identity is fixed before worker launch. Do not hold the
    // receipt mutex while dispatching a UI-thread observation of another document.
    bool schematicChanged = false;
    if( observeInputs && aJob->testFootprints )
    {
        if( !aObserveSchematic ) schematicChanged = true;
        else
        {
            try
            {
                auto current = aObserveSchematic( aJob->schematicState.document() );
                schematicChanged = !current || !google::protobuf::util::MessageDifferencer::Equals(
                        aJob->schematicState, *current );
            }
            catch( const std::exception& ) { schematicChanged = true; }
        }
    }
    // wx notifications can still be queued when an IPC read arrives. Registration
    // is not a completeness barrier: retain the full content fallback for every
    // live receipt, without pumping unrelated native events or entering the UI.
    const bool projectChanged = observeInputs && !aJob->projectBaseline.Unchanged( aBoard );
    bool auxiliaryChanged = observeInputs;
    if( observeInputs && m_observeAuxiliary )
    {
        try
        {
            auto current = m_observeAuxiliary( aBoard );
            auxiliaryChanged = !current || *current != aJob->auxiliaryFingerprint;
        }
        catch( const std::exception& ) { }
    }
    bool librariesChanged = false;
    if( observeInputs && aJob->hasLibraryDependencies )
    {
        if( !aObserveLibraries ) librariesChanged = true;
        else
        {
            try
            {
                auto fingerprint = aObserveLibraries( aBoard );
                librariesChanged = !fingerprint || *fingerprint != aJob->libraryFingerprint;
            }
            catch( const std::exception& ) { librariesChanged = true; }
        }
    }
    std::lock_guard lock( aJob->mutex );
    if( aJob->reporter && !aJob->workerFinished )
    {
        aJob->progress = std::max( aJob->progress, aJob->reporter->Progress() );
        const std::string phase = aJob->reporter->Phase();
        if( !phase.empty() ) aJob->phase = phase;
    }
    const bool liveChanged = aBoard.m_Uuid.AsStdString() != aJob->checkedBoardEpoch
                             || aBoard.GetTimeStamp() != aJob->checkedSequence;
    if( !aJob->invalidated && ( aJob->status == PDRCJS_RUNNING || aJob->status == PDRCJS_QUEUED
          || aJob->status == PDRCJS_COMPLETED )
        && ( liveChanged || schematicChanged || projectChanged || librariesChanged || auxiliaryChanged ) )
    {
        aJob->invalidated = true;
        if( aJob->workerFinished ) aJob->status = PDRCJS_STALE;
        aJob->resultsFresh = false;
        aJob->findings.clear();
        aJob->errorCode = schematicChanged ? "schematic_changed"
                : ( liveChanged ? "document_changed"
                    : ( projectChanged ? "project_inputs_changed"
                        : ( librariesChanged ? "library_inputs_changed" : "auxiliary_inputs_changed" ) ) );
        aJob->errorMessage = schematicChanged ? "The source schematic changed or could not be observed"
                : ( liveChanged ? "The live PCB changed while DRC was running"
                    : ( projectChanged ? "Project settings, exclusions or custom rules changed or could not be observed"
                        : ( librariesChanged ? "Footprint library inputs changed or could not be observed"
                                             : "Drawing-sheet or router inputs changed or could not be observed" ) ) );
        if( aJob->reporter ) aJob->reporter->Cancel();
    }
    PcbDrcJobState result;
    result.mutable_document()->CopyFrom( aJob->document );
    result.set_job_id( aJob->id );
    result.set_operation_id( aJob->operationId );
    result.mutable_checked_revision()->set_epoch( aJob->checkedBoardEpoch );
    result.mutable_checked_revision()->set_sequence( aJob->checkedSequence );
    result.set_process_epoch( aJob->processEpoch );
    result.set_status( aJob->status );
    result.set_progress( aJob->progress );
    result.set_phase( aJob->phase );
    result.set_error_code( aJob->errorCode );
    result.set_error_message( aJob->errorMessage );
    result.set_results_fresh( aJob->resultsFresh );
    result.set_cancellation_requested( aJob->reporter->IsCancelled() );
    result.set_worker_finished( aJob->workerFinished );
    // Project/rule dependency capture is still incomplete (p23deb822a36256a6).
    result.set_snapshot_complete( false );
    result.set_candidate_dry_run( aJob->candidateDryRun );
    for( const KIID& identity : aJob->candidateItemIds )
        result.add_candidate_item_ids( identity.AsStdString() );
    if( aJob->testFootprints ) result.mutable_checked_schematic_state()->CopyFrom( aJob->schematicState );
    for( const auto& warning : aJob->inputWarnings ) result.add_input_warnings( warning );
    if( aJob->workerFinished && aJob->status == PDRCJS_COMPLETED )
        result.mutable_findings()->Assign( aJob->findings.begin(), aJob->findings.end() );
    return result;
}

tl::expected<std::optional<PcbDrcJobState>, std::string> PCB_DRC_JOB_MANAGER::ReadOperation(
        const StartPcbDrcJob& request, BOARD& board, const std::string& epoch,
        const SCHEMATIC_OBSERVER& observer, const LIBRARY_OBSERVER& libraries ) const
{
    if( request.process_epoch() != epoch ) return tl::unexpected( "PCB DRC process epoch mismatch" );
    std::shared_ptr<JOB> existing;
    {
        std::lock_guard lock( m_mutex );
        for( const auto& [id, job] : m_jobs )
        {
            if( job->operationId == request.operation_id() ) { existing = job; break; }
        }
    }
    if( !existing ) return std::optional<PcbDrcJobState>();
    if( !google::protobuf::util::MessageDifferencer::Equals( existing->request, request ) )
        return tl::unexpected( "The operation ID is already bound to different DRC arguments" );
    auto result = state( existing, board, epoch, observer, libraries );
    if( !result ) return tl::unexpected( result.error() );
    return std::optional<PcbDrcJobState>( *result );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Start(
        const StartPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch,
        const PCB_DRC_CAPTURE_CONTEXT& aCaptureContext )
{
    if( aRequest.operation_id().empty() || aRequest.operation_id() == niluuid.AsStdString() )
        return tl::unexpected( "A DRC job requires a nonempty operation ID" );
    if( aRequest.process_epoch() != aProcessEpoch ) return tl::unexpected( "PCB DRC process epoch mismatch" );
    if( aRequest.test_footprints()
        && ( !aRequest.has_expected_schematic_state() || !aCaptureContext.schematic ) )
        return tl::unexpected( "Schematic parity requires native capture at the requested state; no job was started" );
    if( !aRequest.test_footprints()
        && ( aRequest.has_expected_schematic_state() || aRequest.allow_duplicate_sheet_names() ) )
        return tl::unexpected( "Schematic parity options require test_footprints" );
    if( aRequest.refill_zones() && !aCaptureContext.routingSettings )
        return tl::unexpected( "Snapshot refill requires native routing settings; no job was started" );
    if( aRequest.document().type() != kiapi::common::types::DOCTYPE_PCB )
        return tl::unexpected( "A DRC job requires a PCB document" );
    std::shared_ptr<JOB> duplicate;
    {
        std::lock_guard lock( m_mutex );
        for( const auto& [id, existing] : m_jobs )
        {
            std::lock_guard existingLock( existing->mutex );
            if( existing->operationId == aRequest.operation_id() )
            {
                if( !google::protobuf::util::MessageDifferencer::Equals( existing->request, aRequest ) )
                    return tl::unexpected( "The operation ID is already bound to different DRC arguments" );
                duplicate = existing;
                break;
            }
        }
    }
    SCHEMATIC_OBSERVER capturedState = [&]( const DocumentSpecifier& document ) -> tl::expected<DocumentLifecycleState, std::string>
    {
        if( !aCaptureContext.schematic || !SameDocument( document, aCaptureContext.schematic->source_state().document() ) )
            return tl::unexpected( "Native schematic source is unavailable" );
        return aCaptureContext.schematic->source_state();
    };
    LIBRARY_OBSERVER observeLibraries = [&]( BOARD& source ) -> tl::expected<std::string, std::string>
    {
        auto captured = DRC_LIBRARY_INPUTS::Capture( source, aCaptureContext.libraries );
        if( !captured ) return tl::unexpected( "Library capture was cancelled" );
        return captured->ContentFingerprint();
    };
    if( duplicate ) return state( duplicate, aBoard, aProcessEpoch, capturedState, observeLibraries );

    const int sequence = aBoard.GetTimeStamp();
    if( !aRequest.has_expected_revision() || sequence < 0
            || aRequest.expected_revision().epoch() != aBoard.m_Uuid.AsStdString()
            || aRequest.expected_revision().sequence() != static_cast<uint64_t>( sequence ) )
        return tl::unexpected( "PCB changed since the requested DRC revision; no job was started" );
    {
        std::lock_guard lock( m_mutex );
        if( m_jobs.size() >= 128 ) return tl::unexpected( "DRC receipt capacity reached; existing receipts preserved" );
        for( const auto& [id, existing] : m_jobs )
        {
            std::lock_guard jobLock( existing->mutex );
            if( !existing->workerFinished ) return tl::unexpected( "A DRC worker is still active for this owner" );
        }
    }

    retireWatches();
    std::unique_ptr<INPUT_WATCHER> watch;
    std::unique_ptr<PCB_DRC_RUN_INPUTS> inputs;
    std::vector<KIID> candidateItemIds;
    try
    {
        // Subscribe before capture so no change falls between the snapshot and
        // its first notification.
        watch = watchInputs( aBoard, aCaptureContext );
        inputs = PCB_DRC_RUN_INPUTS::Capture( aBoard, aCaptureContext );
        if( !inputs ) return tl::unexpected( "Native DRC input capture was cancelled" );
        if( aRequest.candidate_items_size() > 0 )
        {
            auto added = inputs->AddCandidateItems( aRequest.candidate_items() );
            if( !added ) return tl::unexpected( added.error() );
            candidateItemIds = std::move( *added );
        }
        if( aRequest.test_footprints() )
            inputs->SetSchematicInput( PCB_DRC_SCHEMATIC_INPUT::Capture( *aCaptureContext.schematic,
                    aRequest.expected_schematic_state(), aRequest.document(), aProcessEpoch ) );
        if( aBoard.GetTimeStamp() != sequence ) return tl::unexpected( "PCB changed during DRC capture" );
    }
    catch( const std::exception& error )
    {
        return tl::unexpected( std::string( "Could not snapshot PCB for DRC: " ) + error.what() );
    }

    auto job = std::make_shared<JOB>();
    job->request = aRequest;
    job->id = KIID().AsStdString();
    job->operationId = aRequest.operation_id();
    job->document = aRequest.document();
    job->processEpoch = aProcessEpoch;
    job->checkedBoardEpoch = aBoard.m_Uuid.AsStdString();
    job->checkedSequence = aBoard.GetTimeStamp();
    job->refillZones = aRequest.refill_zones();
    job->reportAllTrackErrors = aRequest.report_all_track_errors();
    job->testFootprints = aRequest.test_footprints();
    job->projectBaseline = inputs->ProjectBaseline();
    job->libraryFingerprint = inputs->LibraryFingerprint();
    job->libraryFingerprints = inputs->LibraryFingerprints();
    job->auxiliaryFingerprint = inputs->AuxiliaryBaseline().Fingerprint();
    job->hasLibraryDependencies = inputs->HasLibraryDependencies();
    job->candidateDryRun = !candidateItemIds.empty();
    job->candidateItemIds = std::move( candidateItemIds );
    if( job->testFootprints )
    {
        job->schematicState = aCaptureContext.schematic->source_state();
        job->inputWarnings.assign( aCaptureContext.schematic->warnings().begin(),
                                   aCaptureContext.schematic->warnings().end() );
    }
    job->reporter = std::make_unique<JOB_PROGRESS>();
    if( watch )
    {
        watch->job = job;
        if( !watch->covered )
            job->inputWarnings.emplace_back( "Some native input notifications are unavailable; status reads recheck content" );
        m_watches.emplace( job->id, std::move( watch ) );
    }
    {
        std::lock_guard lock( m_mutex );
        m_jobs.emplace( job->id, job );
    }
    try
    {
    job->worker = std::thread( [job, inputs = std::move( inputs )]() mutable
    {
        PcbDrcJobStatus terminal = PDRCJS_FAILED;
        std::string errorCode, errorMessage;
        std::vector<PcbDrcFinding> findings;
        {
            std::lock_guard lock( job->mutex );
            job->status = PDRCJS_RUNNING;
        }
        try
        {
            BOARD& board = inputs->GetBoard();
            // Board items consult their design settings' engine for cached
            // clearances. A separate unregistered stack engine can miss rules.
            auto& settings = board.GetDesignSettings();
            settings.m_DRCEngine = std::make_shared<DRC_ENGINE>( &board, &settings );
            DRC_ENGINE& engine = *settings.m_DRCEngine;
            inputs->InitializeEngine( engine );
            bool running = false;
            DRC_RUN_SCOPE invocation( engine, running );
            inputs->BindInvocation( engine );
            engine.SetProgressReporter( job->reporter.get() );
            engine.SetViolationHandler( [&findings, &inputs, &settings, &board]( const std::shared_ptr<DRC_ITEM>& item, const VECTOR2I& position,
                                               int layer, const std::function<void( PCB_MARKER* )>& pathGenerator )
            {
                auto ids = item->GetIDs();
                for( auto& id : ids )
                    if( id == inputs->CapturedDrawingIdentity() ) id = inputs->SourceDrawingIdentity();
                item->SetItems( ids );
                auto marker = std::make_unique<PCB_MARKER>( item, position, layer );
                marker->SetParent( &board );
                if( pathGenerator ) pathGenerator( marker.get() );
                google::protobuf::Any encoded;
                marker->Serialize( encoded );
                PcbDrcFinding finding;
                finding.set_native_id( marker->m_Uuid.AsStdString() );
                if( !encoded.UnpackTo( finding.mutable_marker() ) )
                    throw std::runtime_error( "Could not serialize a DRC finding" );
                const auto exclusion = settings.m_DrcExclusions.find( DRC_EXCLUSION::FromMarker( *marker ) );
                if( exclusion != settings.m_DrcExclusions.end() )
                {
                    finding.set_excluded( true );
                    finding.set_comment( exclusion->GetComment().ToStdString( wxConvUTF8 ) );
                }
                findings.push_back( std::move( finding ) );
            } );
            bool copperReady = true;
            if( job->refillZones )
            {
                const auto& preparation = inputs->PrepareCopper( job->reporter.get() );
                using STATUS = PCB_DRC_COPPER_PREPARATION::STATUS;
                copperReady = preparation.status == STATUS::COMPLETED;
                if( preparation.status == STATUS::CANCELLED ) terminal = PDRCJS_CANCELLED;
                else if( !copperReady )
                {
                    terminal = preparation.status == STATUS::NOT_CONVERGED ? PDRCJS_INCOMPLETE : PDRCJS_FAILED;
                    errorCode = preparation.status == STATUS::NOT_CONVERGED
                            ? "refill_not_converged" : "refill_failed";
                    errorMessage = preparation.error.empty() ? "Native copper preparation did not complete"
                                                             : preparation.error;
                }
            }
            if( copperReady )
            {
                const DRC_RUN_RESULT run = engine.RunTests( EDA_UNITS::MM, job->reportAllTrackErrors,
                                                            job->testFootprints );
                if( run == DRC_RUN_RESULT::CANCELLED ) terminal = PDRCJS_CANCELLED;
                else if( run == DRC_RUN_RESULT::COMPLETED ) terminal = PDRCJS_COMPLETED;
                else { terminal = PDRCJS_INCOMPLETE; errorCode = "incomplete"; errorMessage = "DRC did not complete"; }
            }
        }
        catch( const std::exception& error )
        {
            errorCode = "native_exception";
            errorMessage = error.what();
        }
        catch( ... ) { errorCode = "native_exception"; errorMessage = "Unexpected native DRC exception"; }
        // Board/engine/callbacks and all captured input owners are gone before terminal
        // cancellation is acknowledged. No worker publishes partial findings.
        inputs.reset();
        std::lock_guard lock( job->mutex );
        if( job->invalidated )
        {
            terminal = PDRCJS_STALE;
            errorCode = job->errorCode;
            errorMessage = job->errorMessage;
        }
        else if( job->reporter->IsCancelled() ) terminal = PDRCJS_CANCELLED;
        job->workerFinished = true;
        job->status = terminal;
        job->errorCode = errorCode;
        job->errorMessage = errorMessage;
        job->progress = terminal == PDRCJS_COMPLETED ? 1.0 : job->reporter->Progress();
        job->phase = job->reporter->Phase();
        if( terminal == PDRCJS_COMPLETED ) job->findings = std::move( findings );
        job->resultsFresh = false; // Full project/rule snapshot remains unqualified.
    } );
    }
    catch( const std::exception& error )
    {
        std::lock_guard lock( m_mutex );
        m_jobs.erase( job->id );
        m_watches.erase( job->id );
        return tl::unexpected( std::string( "Could not launch native DRC worker: " ) + error.what() );
    }
    return state( job, aBoard, aProcessEpoch, capturedState, observeLibraries );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Read(
        const ReadPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch,
        const SCHEMATIC_OBSERVER& aObserveSchematic, const LIBRARY_OBSERVER& aObserveLibraries )
{
    retireWatches();
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    if( aRequest.process_epoch() != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before reading this DRC job" );
    if( !SameDocument( job->document, aRequest.document() ) ) return tl::unexpected( "PCB DRC job target mismatch" );
    return state( job, aBoard, aProcessEpoch, aObserveSchematic, aObserveLibraries );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Cancel(
        const CancelPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch,
        const SCHEMATIC_OBSERVER& aObserveSchematic, const LIBRARY_OBSERVER& aObserveLibraries )
{
    retireWatches();
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    if( aRequest.process_epoch() != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before cancelling this DRC job" );
    {
        std::lock_guard lock( job->mutex );
        if( !SameDocument( job->document, aRequest.document() ) ) return tl::unexpected( "PCB DRC job target mismatch" );
        if( !job->workerFinished ) job->reporter->Cancel();
    }
    return state( job, aBoard, aProcessEpoch, aObserveSchematic, aObserveLibraries );
}
