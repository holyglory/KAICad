/* Native PCB manufacturing admission and journal inspection. GPL-3.0-or-later. */
#ifndef KICAD_PCB_MANUFACTURING_JOB_MANAGER_H
#define KICAD_PCB_MANUFACTURING_JOB_MANAGER_H

#include <api/common/commands/pcb_manufacturing_commands.pb.h>
#include <tl/expected.hpp>
#include <wx/string.h>
#include <functional>
#include <mutex>
#include <string>

/** Request journaling is not an exporter or a complete dependency snapshot. */
class PCB_MANUFACTURING_JOB_MANAGER
{
public:
    struct CAPTURE
    {
        kiapi::automation::v1::DocumentLifecycleState before;
        kiapi::automation::v1::DocumentLifecycleState after;
        std::string sourceBoard;
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
     * The manager validates the returned receipt before replacing the admitted
     * journal record. An adapter failure leaves the admitted record untouched,
     * so a caller cannot mistake a lost handoff for a completed export.
     */
    struct DISPATCH_CONTEXT
    {
        kiapi::automation::v1::RunPcbManufacturingExport request;
        std::string sourceBoard;
        std::string sourceSnapshotSha256;
    };

    using DISPATCH_ADAPTER = std::function<tl::expected<
            kiapi::automation::v1::PcbManufacturingExportReceipt, std::string>(
            const DISPATCH_CONTEXT& )>;

    explicit PCB_MANUFACTURING_JOB_MANAGER( const wxString& aJournalDirectory );
    static wxString JournalDirectory( const wxString& aSettingsDirectory );

    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Start(
            const kiapi::automation::v1::RunPcbManufacturingExport&,
            const std::string& aProcessEpoch, const CAPTURE_SOURCE& aCapture,
            bool aLeaveAdmitted = false );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Dispatch(
            const kiapi::automation::v1::RunPcbManufacturingExport&,
            const std::string& aProcessEpoch, const DISPATCH_ADAPTER& aAdapter );
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
    static bool TestAdmissionBarrier();
    static std::string JobKind( const Request& );
    static void SetUnsupported( Receipt& );
    const wxString m_journalDirectory;
    mutable std::mutex m_mutex;
};

#endif
