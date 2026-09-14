/* Native asynchronous PCB DRC job ownership. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_JOB_MANAGER_H
#define KICAD_PCB_DRC_JOB_MANAGER_H

#include <api/common/commands/automation_commands.pb.h>
#include <tl/expected.hpp>

#include <map>
#include <memory>
#include <mutex>
#include <string>

class BOARD;
struct PCB_DRC_CAPTURE_CONTEXT;

/**
 * Owns DRC jobs for one native process/document owner. Jobs run against a
 * serialized snapshot, so the IPC handler remains responsive and live edits
 * can invalidate the result instead of racing the checker.
 */
class PCB_DRC_JOB_MANAGER
{
public:
    PCB_DRC_JOB_MANAGER();
    ~PCB_DRC_JOB_MANAGER();

    PCB_DRC_JOB_MANAGER( const PCB_DRC_JOB_MANAGER& ) = delete;
    PCB_DRC_JOB_MANAGER& operator=( const PCB_DRC_JOB_MANAGER& ) = delete;

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Start(
            const kiapi::automation::v1::StartPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch, const PCB_DRC_CAPTURE_CONTEXT& aCaptureContext );

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Read(
            const kiapi::automation::v1::ReadPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch );

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Cancel(
            const kiapi::automation::v1::CancelPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch );

private:
    struct JOB;
    std::shared_ptr<JOB> find( const std::string& aJobId ) const;
    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> state(
            const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch ) const;

    mutable std::mutex m_mutex;
    std::map<std::string, std::shared_ptr<JOB>> m_jobs;
};

#endif
