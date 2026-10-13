/* Native PCB manufacturing admission and recovery state. GPL-3.0-or-later. */
#include "pcb_manufacturing_job_manager.h"
#include <api/native_state_digest.h>
#include <google/protobuf/util/message_differencer.h>
#include <kiplatform/io.h>
#include <wx/filename.h>
#include <wx/string.h>
#include <cstdlib>
#include <cctype>
#include <algorithm>
#include <filesystem>
#include <fstream>

using namespace kiapi::automation::v1;
using kiapi::common::types::DocumentType;
namespace { std::string Hash( std::string_view s ) { NATIVE_STATE_DIGEST d; d.Append(s); return d.Hex(); } }

PCB_MANUFACTURING_JOB_MANAGER::PCB_MANUFACTURING_JOB_MANAGER( const wxString& dir ) : m_journalDirectory(dir)
{ wxFileName::Mkdir( m_journalDirectory, wxS_DIR_DEFAULT, wxPATH_MKDIR_FULL ); }

std::string PCB_MANUFACTURING_JOB_MANAGER::pathFor( const std::string& id ) const
{ std::string name = id + ".mfg-journal"; return wxFileName( m_journalDirectory, wxString::FromUTF8(name.c_str()) ).GetFullPath().ToStdString(); }
std::string PCB_MANUFACTURING_JOB_MANAGER::fingerprint( const Request& r ) const { return Hash(r.SerializeAsString()); }
std::string PCB_MANUFACTURING_JOB_MANAGER::sourceDigest( const Request& r ) const { return Hash(r.expected_state().SerializeAsString()); }
bool PCB_MANUFACTURING_JOB_MANAGER::SameDocument( const kiapi::common::types::DocumentSpecifier& a, const kiapi::common::types::DocumentSpecifier& b ) { return google::protobuf::util::MessageDifferencer::Equals(a,b); }
bool PCB_MANUFACTURING_JOB_MANAGER::IsUuid( const std::string& s )
{
    if(s.size()!=36) return false;
    for(size_t i=0;i<s.size();++i) { if(i==8||i==13||i==18||i==23) { if(s[i]!='-') return false; } else if(!std::isxdigit((unsigned char)s[i])||std::isupper((unsigned char)s[i])) return false; }
    return s != "00000000-0000-0000-0000-000000000000";
}
bool PCB_MANUFACTURING_JOB_MANAGER::IsDigest( const std::string& s ) { return s.size()==64 && std::all_of(s.begin(),s.end(),[](char c){return std::isxdigit((unsigned char)c)&&!std::isupper((unsigned char)c);}); }
bool PCB_MANUFACTURING_JOB_MANAGER::TestAdmissionBarrier() { const char* v=std::getenv("KICAD_MANUFACTURING_TEST_BARRIER"); return v&&std::string(v)=="after-admission"; }
std::string PCB_MANUFACTURING_JOB_MANAGER::JobKind( const Request& r )
{
    switch(r.job_case()) { case Request::kExport3D:return "3d"; case Request::kExportRender:return "render"; case Request::kExportSvg:return "svg"; case Request::kExportDxf:return "dxf"; case Request::kExportPdf:return "pdf"; case Request::kExportPs:return "ps"; case Request::kExportGerbers:return "gerbers"; case Request::kExportDrill:return "drill"; case Request::kExportPosition:return "position"; case Request::kExportGencad:return "gencad"; case Request::kExportIpc2581:return "ipc2581"; case Request::kExportIpcD356:return "ipc-d356"; case Request::kExportOdb:return "odb"; case Request::kExportStats:return "stats"; default:return {}; }
}
std::string PCB_MANUFACTURING_JOB_MANAGER::StagingDirectory(const std::string& out,const std::string& id)
{ auto p=std::filesystem::u8path(out).lexically_normal(); return (p.parent_path()/(p.filename().string()+".kicad-staging-"+id)).generic_string(); }
void PCB_MANUFACTURING_JOB_MANAGER::SetUnsupported( Receipt& r )
{
    r.set_status(PCB_MFG_EXPORT_UNSUBMITTED); r.set_dispatch_stage(PCB_MFG_DISPATCH_ADMITTED); r.set_terminal(true); r.set_native_dispatch(false);
    r.set_error_code("export_adapter_unavailable"); r.set_error_message("The detached native manufacturing exporter is not enabled; no output was generated.");
    r.set_recovery_state(PCB_MFG_RECOVERY_UNSUBMITTED); r.set_worker_finished(true); r.set_export_adapter_available(false);
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::JournalRecord,std::string> PCB_MANUFACTURING_JOB_MANAGER::read(const std::string& id) const
{
    std::ifstream f(pathFor(id),std::ios::binary); if(!f) return tl::unexpected("journal_not_found");
    std::string b((std::istreambuf_iterator<char>(f)),{}); JournalRecord r; if(!r.ParseFromString(b)||r.format_version()!=1||r.request().operation_id()!=id||!IsDigest(r.receipt().durable_journal_sha256())) return tl::unexpected("invalid_manufacturing_journal"); return r;
}

tl::expected<void,std::string> PCB_MANUFACTURING_JOB_MANAGER::write(JournalRecord r) const
{
    std::filesystem::create_directories(m_journalDirectory.ToStdString()); Receipt* p=r.mutable_receipt(); p->clear_durable_journal_sha256(); std::string u=r.SerializeAsString(); p->set_durable_journal_sha256(Hash(u)); std::string b=r.SerializeAsString(); wxString e;
    std::string path = pathFor(r.request().operation_id());
    if(!KIPLATFORM::IO::AtomicWriteFile(wxString::FromUTF8(path.c_str()),b.data(),b.size(),&e)) return tl::unexpected(e.ToStdString()); return {};
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt,std::string> PCB_MANUFACTURING_JOB_MANAGER::Start(const Request& r,const std::string& epoch)
{
    std::lock_guard lock(m_mutex);
    if(!IsUuid(r.operation_id())||!IsUuid(epoch)||r.process_epoch()!=epoch||r.document().type()!=DocumentType::DOCTYPE_PCB||r.job_case()==Request::JOB_NOT_SET||r.output_directory().empty()||r.expected_state().state_sha256().empty()) return tl::unexpected("invalid_manufacturing_manager_request");
    if(auto old=read(r.operation_id());old){if(old->request().SerializeAsString()!=r.SerializeAsString()) return tl::unexpected("manufacturing_operation_reused"); return old->receipt();} else if(old.error()!="journal_not_found") return tl::unexpected(old.error());
    JournalRecord j; j.set_format_version(1); j.mutable_request()->CopyFrom(r); j.set_source_board(r.SerializeAsString()); j.set_journal_revision(1); Receipt* q=j.mutable_receipt();
    q->set_operation_id(r.operation_id()); q->set_process_epoch(epoch); q->mutable_document()->CopyFrom(r.document()); q->set_request_fingerprint_sha256(fingerprint(r)); q->set_source_snapshot_sha256(sourceDigest(r)); q->set_job_kind(JobKind(r)); q->set_status(PCB_MFG_EXPORT_ADMITTED); q->set_dispatch_stage(PCB_MFG_DISPATCH_ADMITTED); q->mutable_expected_state()->CopyFrom(r.expected_state()); q->mutable_observed_before()->CopyFrom(r.expected_state()); q->set_output_directory(r.output_directory()); q->set_staging_directory(StagingDirectory(r.output_directory(),r.operation_id())); q->set_publication_state(PCB_MFG_PUBLICATION_STAGED); q->set_recovery_state(PCB_MFG_RECOVERY_ACTIVE); q->set_snapshot_complete(false); q->set_export_adapter_available(false);
    if(auto ok=write(j);!ok) return tl::unexpected(ok.error());
    if(!TestAdmissionBarrier()){SetUnsupported(*q);++*j.mutable_journal_revision();if(auto ok=write(j);!ok)return tl::unexpected(ok.error());}
    return *q;
}

tl::expected<PCB_MANUFACTURING_JOB_MANAGER::Receipt,std::string> PCB_MANUFACTURING_JOB_MANAGER::Cancel(const CancelPcbManufacturingExport& r,const std::string& epoch)
{
    std::lock_guard lock(m_mutex); auto j=read(r.operation_id()); if(!j)return tl::unexpected(j.error()); if(!IsUuid(epoch)||j->receipt().process_epoch()!=epoch||!SameDocument(j->request().document(),r.document()))return tl::unexpected("manufacturing_cancel_identity_mismatch"); Receipt* q=j->mutable_receipt(); if(q->terminal())return *q; if(q->native_dispatch())return tl::unexpected("manufacturing_cancel_requires_worker"); q->set_cancellation_requested(true);q->set_recovery_state(PCB_MFG_RECOVERY_CANCELLED);q->set_status(PCB_MFG_EXPORT_UNSUBMITTED);q->set_terminal(true);q->set_worker_finished(true);q->set_error_code("export_cancelled_before_dispatch");q->set_error_message("The manager cancelled the admitted export before native dispatch.");++*j->mutable_journal_revision();if(auto ok=write(*j);!ok)return tl::unexpected(ok.error());return *q;
}

tl::expected<PcbManufacturingExportReceiptResponse,std::string> PCB_MANUFACTURING_JOB_MANAGER::Reconcile(const ReconcilePcbManufacturingExport& r,const std::string& epoch)
{
    std::lock_guard lock(m_mutex); PcbManufacturingExportReceiptResponse out; auto j=read(r.operation_id()); if(!j){if(j.error()=="journal_not_found"){out.set_found(false);out.set_reconciliation_supported(true);out.set_observing_process_epoch(epoch);return out;}return tl::unexpected(j.error());} const Receipt& old=j->receipt(); if(!IsUuid(epoch)||!IsDigest(r.request_fingerprint_sha256())||old.request_fingerprint_sha256()!=r.request_fingerprint_sha256()||!SameDocument(old.document(),r.document())||!r.allow_prior_process_epoch()||r.prior_process_epoch()!=old.process_epoch()) return tl::unexpected("manufacturing_reconcile_identity_mismatch"); Receipt* q=j->mutable_receipt();q->set_recovery_state(PCB_MFG_RECOVERY_RECONCILED);++*j->mutable_journal_revision();if(auto ok=write(*j);!ok)return tl::unexpected(ok.error());out.set_found(true);out.set_reconciliation_supported(true);out.set_observing_process_epoch(epoch);out.set_request_fingerprint_verified(true);out.set_journal_verified(true);out.set_prior_process_epoch_verified(true);out.mutable_receipt()->CopyFrom(*q);return out;
}
