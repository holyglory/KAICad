/* Native asynchronous PCB DRC job ownership. GPL-3.0-or-later. */
#include "pcb_drc_job_manager.h"
#include "pcb_drc_run_inputs.h"

#include <board.h>
#include <board_design_settings.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <drc/drc_run_scope.h>
#include <pcb_marker.h>
#include <progress_reporter.h>
#include <google/protobuf/util/message_differencer.h>

#include <atomic>
#include <algorithm>
#include <cmath>
#include <functional>
#include <map>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <vector>

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

PCB_DRC_JOB_MANAGER::PCB_DRC_JOB_MANAGER() = default;

PCB_DRC_JOB_MANAGER::~PCB_DRC_JOB_MANAGER()
{
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
        const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch ) const
{
    if( !aJob ) return tl::unexpected( "Unknown PCB DRC job" );
    std::lock_guard lock( aJob->mutex );
    if( aJob->processEpoch != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before reading this DRC job" );
    if( aJob->reporter && !aJob->workerFinished )
    {
        aJob->progress = std::max( aJob->progress, aJob->reporter->Progress() );
        const std::string phase = aJob->reporter->Phase();
        if( !phase.empty() ) aJob->phase = phase;
    }
    const bool liveChanged = aBoard.m_Uuid.AsStdString() != aJob->checkedBoardEpoch
                             || aBoard.GetTimeStamp() != aJob->checkedSequence;
    if( ( aJob->status == PDRCJS_RUNNING || aJob->status == PDRCJS_QUEUED
          || aJob->status == PDRCJS_COMPLETED ) && liveChanged )
    {
        aJob->invalidated = true;
        if( aJob->workerFinished ) aJob->status = PDRCJS_STALE;
        aJob->resultsFresh = false;
        aJob->findings.clear();
        aJob->errorCode = "document_changed";
        aJob->errorMessage = "The live PCB changed while DRC was running";
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
    if( aJob->workerFinished && aJob->status == PDRCJS_COMPLETED )
        result.mutable_findings()->Assign( aJob->findings.begin(), aJob->findings.end() );
    return result;
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Start(
        const StartPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch,
        const PCB_DRC_CAPTURE_CONTEXT& aCaptureContext )
{
    if( aRequest.operation_id().empty() || aRequest.operation_id() == niluuid.AsStdString() )
        return tl::unexpected( "A DRC job requires a nonempty operation ID" );
    if( aRequest.process_epoch() != aProcessEpoch ) return tl::unexpected( "PCB DRC process epoch mismatch" );
    if( aRequest.refill_zones() || aRequest.test_footprints() )
        return tl::unexpected( "Snapshot refill and schematic parity are not implemented; no job was started" );
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
    if( duplicate ) return state( duplicate, aBoard, aProcessEpoch );

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

    std::unique_ptr<PCB_DRC_RUN_INPUTS> inputs;
    try
    {
        inputs = PCB_DRC_RUN_INPUTS::Capture( aBoard, aCaptureContext );
        if( !inputs ) return tl::unexpected( "Native DRC input capture was cancelled" );
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
    job->reporter = std::make_unique<JOB_PROGRESS>();
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
            const DRC_RUN_RESULT run = engine.RunTests( EDA_UNITS::MM, job->reportAllTrackErrors,
                                                        job->testFootprints );
            if( run == DRC_RUN_RESULT::CANCELLED ) terminal = PDRCJS_CANCELLED;
            else if( run == DRC_RUN_RESULT::COMPLETED ) terminal = PDRCJS_COMPLETED;
            else { terminal = PDRCJS_INCOMPLETE; errorCode = "incomplete"; errorMessage = "DRC did not complete"; }
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
        if( job->invalidated ) { terminal = PDRCJS_STALE; errorCode = "document_changed"; }
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
        return tl::unexpected( std::string( "Could not launch native DRC worker: " ) + error.what() );
    }
    return state( job, aBoard, aProcessEpoch );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Read(
        const ReadPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch )
{
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    if( aRequest.process_epoch() != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before reading this DRC job" );
    if( !SameDocument( job->document, aRequest.document() ) ) return tl::unexpected( "PCB DRC job target mismatch" );
    return state( job, aBoard, aProcessEpoch );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Cancel(
        const CancelPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch )
{
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    if( aRequest.process_epoch() != aProcessEpoch )
        return tl::unexpected( "The native process epoch changed; reattach before cancelling this DRC job" );
    {
        std::lock_guard lock( job->mutex );
        if( !SameDocument( job->document, aRequest.document() ) ) return tl::unexpected( "PCB DRC job target mismatch" );
        if( !job->workerFinished ) job->reporter->Cancel();
    }
    return state( job, aBoard, aProcessEpoch );
}
