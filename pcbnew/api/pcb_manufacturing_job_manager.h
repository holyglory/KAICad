/* Native PCB manufacturing admission and recovery state. GPL-3.0-or-later. */
#ifndef KICAD_PCB_MANUFACTURING_JOB_MANAGER_H
#define KICAD_PCB_MANUFACTURING_JOB_MANAGER_H

#include <api/common/commands/pcb_manufacturing_commands.pb.h>
#include <tl/expected.hpp>
#include <wx/string.h>
#include <mutex>
#include <string>

class PCB_MANUFACTURING_JOB_MANAGER
{
public:
    explicit PCB_MANUFACTURING_JOB_MANAGER( const wxString& aJournalDirectory );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Start(
            const kiapi::automation::v1::RunPcbManufacturingExport&, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceipt, std::string> Cancel(
            const kiapi::automation::v1::CancelPcbManufacturingExport&, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceiptResponse, std::string> Reconcile(
            const kiapi::automation::v1::ReconcilePcbManufacturingExport&, const std::string& aProcessEpoch );
    tl::expected<kiapi::automation::v1::PcbManufacturingExportReceiptResponse, std::string> Read(
            const kiapi::automation::v1::ReadPcbManufacturingExportJob&,
            const std::string& aProcessEpoch );
    kiapi::automation::v1::PcbManufacturingManagerCapabilities Capabilities(
            const kiapi::common::types::DocumentSpecifier&, const std::string& aProcessEpoch ) const;

private:
    using JournalRecord = kiapi::automation::v1::PcbManufacturingJournalRecord;
    using Receipt = kiapi::automation::v1::PcbManufacturingExportReceipt;
    using Request = kiapi::automation::v1::RunPcbManufacturingExport;
    std::string pathFor( const std::string& ) const;
    std::string fingerprint( const Request& ) const;
    std::string sourceDigest( const Request& ) const;
    tl::expected<JournalRecord, std::string> read( const std::string& ) const;
    tl::expected<void, std::string> write( JournalRecord ) const;
    static bool SameDocument( const kiapi::common::types::DocumentSpecifier&, const kiapi::common::types::DocumentSpecifier& );
    static bool IsUuid( const std::string& );
    static bool IsDigest( const std::string& );
    static bool TestAdmissionBarrier();
    static std::string JobKind( const Request& );
    static std::string StagingDirectory( const std::string&, const std::string& );
    static void SetUnsupported( Receipt& );
    const wxString m_journalDirectory;
    mutable std::mutex m_mutex;
};

#endif
