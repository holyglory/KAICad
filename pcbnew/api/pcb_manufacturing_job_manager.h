/* Native PCB manufacturing admission and journal inspection. GPL-3.0-or-later. */
#ifndef KICAD_PCB_MANUFACTURING_JOB_MANAGER_H
#define KICAD_PCB_MANUFACTURING_JOB_MANAGER_H

#include <api/common/commands/pcb_manufacturing_commands.pb.h>
#include <tl/expected.hpp>
#include <wx/string.h>
#include <functional>
#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

/** Request journaling is not an exporter or a complete dependency snapshot. */
class PCB_MANUFACTURING_JOB_MANAGER
{
public:
    /**
     * Cooperative cancellation shared by the manager and one worker.
     *
     * The token never owns native editor objects.  Adapters may poll it while
     * producing output and must return without touching the live board once it
     * is set.  The manager still journals dispatch intent before invoking an
     * adapter and therefore fails closed if cancellation races native code.
     */
    struct CANCELLATION_TOKEN
    {
        void Request() { requested.store( true, std::memory_order_release ); }
        bool IsRequested() const { return requested.load( std::memory_order_acquire ); }

    private:
        std::atomic<bool> requested{ false };
    };

    using CANCELLATION = std::shared_ptr<CANCELLATION_TOKEN>;

    struct CAPTURE
    {
        kiapi::automation::v1::DocumentLifecycleState before;
        kiapi::automation::v1::DocumentLifecycleState after;
        std::string sourceBoard;
        kiapi::automation::v1::PcbManufacturingDetachedSnapshot sourceSnapshot;
    };

    // Invoked only on first admission, on the registering editor thread. The
    // owner obtains both observations through the public API server and copies
    // the board between them without yielding to editor input.
    using CAPTURE_SOURCE = std::function<tl::expected<CAPTURE, std::string>(
            const kiapi::common::types::DocumentSpecifier& )>;

    /**
     * Immutable handoff to a KiCad-owned exporter adapter.
     *
     * The manager owns admission, operation identity and the durable journal.
     * An adapter may dispatch the request to native export code and return a
     * native receipt, but it cannot change the request or lifecycle baseline.
     * The manager durably records dispatch intent before invoking the adapter,
     * then validates the returned receipt before replacing that submitted
     * journal record. An adapter failure closes as indeterminate so a caller
     * cannot retry after a handoff that may already have reached native code.
     */
    struct DISPATCH_CONTEXT
    {
        kiapi::automation::v1::RunPcbManufacturingExport request;
        std::string sourceBoard;
        std::string sourceSnapshotSha256;
        kiapi::automation::v1::PcbManufacturingDetachedSnapshot sourceSnapshot;
        // Detached adapters may write only below this operation-owned path.
        // The manager creates it before dispatch intent is published; the
        // owner thread remains responsible for any later publication. An
        // uncertain detached result may retain this exact path for explicit
        // recovery, but that retention never claims publication.
        std::string stagingDirectory;
        // Copied before worker creation.  This is the immutable lifecycle and
        // dependency baseline associated with sourceBoard; a worker never
        // re-reads the live editor to fill in missing inputs.
        kiapi::automation::v1::DocumentLifecycleState sourceState;
        CANCELLATION cancellation;
        bool detachedWorker = false;
    };

    using DISPATCH_ADAPTER = std::function<tl::expected<
            kiapi::automation::v1::PcbManufacturingExportReceipt, std::string>(
            const DISPATCH_CONTEXT& )>;

    explicit PCB_MANUFACTURING_JOB_MANAGER( const wxString& aJournalDirectory );
    ~PCB_MANUFACTURING_JOB_MANAGER();
    static wxString JournalDirectory( const wxString& aSettingsDirectory );

    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Start(
            const kiapi::automation::v1::RunPcbManufacturingExport&,
            const std::string& aProcessEpoch, const CAPTURE_SOURCE& aCapture,
            bool aLeaveAdmitted = false );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Dispatch(
            const kiapi::automation::v1::RunPcbManufacturingExport&,
            const std::string& aProcessEpoch, const DISPATCH_ADAPTER& aAdapter,
            const CANCELLATION& aCancellation = {}, bool aDetachedWorker = false );
    /**
     * Dispatch through a manager-owned joinable worker.  The adapter receives
     * only the copied request/source/dependency snapshot and cancellation
     * token.  The returned receipt is the admission/dispatch observation;
     * callers use Join or Read to observe terminal worker output.
     */
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> DispatchAsync(
            const kiapi::automation::v1::RunPcbManufacturingExport&,
            const std::string& aProcessEpoch, const DISPATCH_ADAPTER& aAdapter );
    /** Join one worker and durably record that the worker has been joined. */
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Join(
            const std::string& aOperationId, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Cancel(
            const kiapi::automation::v1::CancelPcbManufacturingExport&, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceiptResponse, std::string> Reconcile(
            const kiapi::automation::v1::ReconcilePcbManufacturingExport&, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceiptResponse, std::string> Read(
            const kiapi::automation::v1::ReadPcbManufacturingExportJob&, const std::string& aProcessEpoch );
    kiapi::automation::v1::PcbManufacturingManagerCapabilities Capabilities(
            const kiapi::common::types::DocumentSpecifier&, const std::string& aProcessEpoch ) const;

private:
    using JournalRecord = kiapi::automation::v1::PcbManufacturingJournalRecord;
    using Receipt = kiapi::automation::v1::PcbManufacturingExportReceipt;
    using Request = kiapi::automation::v1::RunPcbManufacturingExport;

    std::string pathFor( const std::string& aOperationId ) const;
    tl::expected<bool, std::string> requireDirectory( bool aCreate ) const;
    tl::expected<JournalRecord, std::string> read( const std::string& aOperationId ) const;
    tl::expected<void, std::string> write( JournalRecord& aRecord ) const;
    static tl::expected<void, std::string> ValidateRequest( const Request& );
    static tl::expected<void, std::string> ValidateRecord( const JournalRecord& );
    static tl::expected<void, std::string> ValidateDispatchedReceipt(
            const JournalRecord&, const Receipt& );
    static bool SameDocument( const kiapi::common::types::DocumentSpecifier&,
                              const kiapi::common::types::DocumentSpecifier& );
    static bool ValidDocument( const kiapi::common::types::DocumentSpecifier& );
    static bool ValidState( const kiapi::automation::v1::DocumentLifecycleState& );
    static bool IsUuid( const std::string& );
    static bool IsDigest( const std::string& );
    static bool HasCompleteSourceSnapshot(
            const kiapi::automation::v1::DocumentLifecycleState& );
    static bool HasCompleteDetachedSnapshot(
            const kiapi::automation::v1::PcbManufacturingDetachedSnapshot&,
            const kiapi::common::types::DocumentSpecifier& );
    static bool SnapshotMatchesState(
            const kiapi::automation::v1::PcbManufacturingDetachedSnapshot&,
            const kiapi::automation::v1::DocumentLifecycleState& );
    static tl::expected<void, std::string> VerifyPublishedOutputs(
            const kiapi::automation::v1::PcbManufacturingExportReceipt& );
    static bool TestAdmissionBarrier();
    static std::string JobKind( const Request& );
    static void SetUnsupported( Receipt& );
    static void SetDispatchSubmitted( Receipt& );
    static void SetDispatchIndeterminate( Receipt&, const std::string&, const std::string& );
    struct WORKER
    {
        CANCELLATION cancellation;
        std::mutex joinMutex;
        std::thread thread;
    };
    tl::expected<Receipt, std::string> MarkWorkerJoined( const std::string& aOperationId,
                                                         const std::string& aProcessEpoch );
    tl::expected<Receipt, std::string> FinalizeWorkerIndeterminate(
            const std::string& aOperationId, const std::string& aProcessEpoch,
            const std::string& aErrorCode, const std::string& aErrorMessage );
    const wxString m_journalDirectory;
    mutable std::mutex m_mutex;
    std::map<std::string, std::shared_ptr<WORKER>> m_workers;
};

#endif
