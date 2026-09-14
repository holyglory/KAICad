/* Native asynchronous PCB DRC job ownership. GPL-3.0-or-later. */
#include "pcb_drc_job_manager.h"

#include <board.h>
#include <drc/drc_engine.h>
#include <drc/drc_item.h>
#include <pcb_io/kicad_sexpr/pcb_io_kicad_sexpr.h>
#include <pcb_io/pcb_io_mgr.h>
#include <pcb_marker.h>
#include <progress_reporter.h>
#include <wx/filename.h>
#include <wx/filefn.h>

#include <atomic>
#include <functional>
#include <map>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <vector>

using namespace kiapi::automation::v1;

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
    void SetCurrentProgress( double aProgress ) override { m_progress.store( aProgress ); }
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
    if( aJob->reporter )
    {
        aJob->progress = aJob->reporter->Progress();
        aJob->phase = aJob->reporter->Phase();
    }
    const bool liveChanged = aBoard.m_Uuid.AsStdString() != aJob->checkedBoardEpoch
                             || aBoard.GetTimeStamp() != aJob->checkedSequence;
    if( ( aJob->status == PDRCJS_RUNNING || aJob->status == PDRCJS_QUEUED
          || aJob->status == PDRCJS_COMPLETED ) && liveChanged )
    {
        aJob->status = PDRCJS_STALE;
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
    result.mutable_findings()->Assign( aJob->findings.begin(), aJob->findings.end() );
    return result;
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Start(
        const StartPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch )
{
    if( aRequest.operation_id().empty() ) return tl::unexpected( "A DRC job requires an operation ID" );
    if( aRequest.document().type() != kiapi::common::types::DOCTYPE_PCB )
        return tl::unexpected( "A DRC job requires a PCB document" );
    {
        std::lock_guard lock( m_mutex );
        for( const auto& [id, existing] : m_jobs )
        {
            std::lock_guard existingLock( existing->mutex );
            if( existing->operationId == aRequest.operation_id() )
            {
                if( existing->document != aRequest.document() )
                    return tl::unexpected( "The operation ID is already bound to another PCB target" );
                return state( existing, aBoard, aProcessEpoch );
            }
        }
    }

    wxString path = wxFileName::CreateTempFileName( "kicad-drc-job-" );
    try
    {
        PCB_IO_KICAD_SEXPR writer;
        writer.SaveBoard( path, &aBoard, nullptr );
    }
    catch( const std::exception& error )
    {
        wxRemoveFile( path );
        return tl::unexpected( std::string( "Could not snapshot PCB for DRC: " ) + error.what() );
    }

    auto job = std::make_shared<JOB>();
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
    job->worker = std::thread( [job, path, this]
    {
        std::unique_ptr<BOARD> board;
        try
        {
            board.reset( PCB_IO_MGR::Load( PCB_IO_MGR::KICAD_SEXPR, path, nullptr, nullptr, nullptr ) );
            if( !board ) throw std::runtime_error( "Native PCB snapshot could not be loaded" );
            DRC_ENGINE engine( board.get(), &board->GetDesignSettings() );
            engine.InitEngine( board->GetDesignRulesPath() );
            engine.SetProgressReporter( job->reporter.get() );
            engine.SetViolationHandler( [job]( const std::shared_ptr<DRC_ITEM>& item, const VECTOR2I& position,
                                               int layer, const std::function<void( PCB_MARKER* )>& pathGenerator )
            {
                auto marker = std::make_unique<PCB_MARKER>( item, position, layer );
                pathGenerator( marker.get() );
                google::protobuf::Any encoded;
                marker->Serialize( encoded );
                PcbDrcFinding finding;
                finding.set_native_id( marker->m_Uuid.AsStdString() );
                if( !encoded.UnpackTo( finding.mutable_marker() ) ) return;
                std::lock_guard lock( job->mutex );
                job->findings.push_back( std::move( finding ) );
            } );
            const DRC_RUN_RESULT run = engine.RunTests( EDA_UNITS::MM, job->reportAllTrackErrors,
                                                        job->testFootprints );
            std::lock_guard lock( job->mutex );
            if( job->status == PDRCJS_STALE ) return;
            job->progress = 1.0;
            job->phase = "complete";
            if( run == DRC_RUN_RESULT::CANCELLED ) job->status = PDRCJS_CANCELLED;
            else if( run == DRC_RUN_RESULT::COMPLETED ) { job->status = PDRCJS_COMPLETED; job->resultsFresh = true; }
            else { job->status = PDRCJS_INCOMPLETE; job->errorCode = "incomplete"; job->errorMessage = "DRC did not complete"; }
        }
        catch( const std::exception& error )
        {
            std::lock_guard lock( job->mutex );
            job->status = PDRCJS_FAILED;
            job->errorCode = "native_exception";
            job->errorMessage = error.what();
        }
        wxRemoveFile( path );
    } );
    return state( job, aBoard, aProcessEpoch );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Read(
        const ReadPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch )
{
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    if( job->document != aRequest.document() ) return tl::unexpected( "PCB DRC job target mismatch" );
    return state( job, aBoard, aProcessEpoch );
}

tl::expected<PcbDrcJobState, std::string> PCB_DRC_JOB_MANAGER::Cancel(
        const CancelPcbDrcJob& aRequest, BOARD& aBoard, const std::string& aProcessEpoch )
{
    auto job = find( aRequest.job_id() );
    if( !job ) return tl::unexpected( "Unknown PCB DRC job" );
    {
        std::lock_guard lock( job->mutex );
        if( job->document != aRequest.document() ) return tl::unexpected( "PCB DRC job target mismatch" );
        if( job->reporter ) job->reporter->Cancel();
        if( job->status == PDRCJS_QUEUED || job->status == PDRCJS_RUNNING ) job->status = PDRCJS_CANCELLED;
    }
    return state( job, aBoard, aProcessEpoch );
}
