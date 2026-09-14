/* Native asynchronous PCB DRC job ownership. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_JOB_MANAGER_H
#define KICAD_PCB_DRC_JOB_MANAGER_H

#include <api/common/commands/automation_commands.pb.h>
#include <tl/expected.hpp>

#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <functional>
#include <optional>

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
    using SCHEMATIC_OBSERVER = std::function<
            tl::expected<kiapi::automation::v1::DocumentLifecycleState, std::string>(
                    const kiapi::common::types::DocumentSpecifier& )>;
    using LIBRARY_OBSERVER = std::function<tl::expected<std::string, std::string>( BOARD& )>;
    using AUXILIARY_OBSERVER = std::function<tl::expected<std::string, std::string>( BOARD& )>;
    // Owner-thread callback resolves current editor settings on each read. It is
    // never passed to the worker or invoked during destruction.
    explicit PCB_DRC_JOB_MANAGER( AUXILIARY_OBSERVER aObserveAuxiliary );
    ~PCB_DRC_JOB_MANAGER();

    PCB_DRC_JOB_MANAGER( const PCB_DRC_JOB_MANAGER& ) = delete;
    PCB_DRC_JOB_MANAGER& operator=( const PCB_DRC_JOB_MANAGER& ) = delete;

    // Lookup a retry before native capture; it must not need an obsolete source
    // state to be recaptured and must never launch a second worker.
    tl::expected<std::optional<kiapi::automation::v1::PcbDrcJobState>, std::string> ReadOperation(
            const kiapi::automation::v1::StartPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch, const SCHEMATIC_OBSERVER& aObserveSchematic = {},
            const LIBRARY_OBSERVER& aObserveLibraries = {} ) const;

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Start(
            const kiapi::automation::v1::StartPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch, const PCB_DRC_CAPTURE_CONTEXT& aCaptureContext );

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Read(
            const kiapi::automation::v1::ReadPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch, const SCHEMATIC_OBSERVER& aObserveSchematic = {},
            const LIBRARY_OBSERVER& aObserveLibraries = {} );

    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> Cancel(
            const kiapi::automation::v1::CancelPcbDrcJob& aRequest, BOARD& aBoard,
            const std::string& aProcessEpoch, const SCHEMATIC_OBSERVER& aObserveSchematic = {},
            const LIBRARY_OBSERVER& aObserveLibraries = {} );

private:
    struct JOB;
    std::shared_ptr<JOB> find( const std::string& aJobId ) const;
    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> state(
            const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch,
            const SCHEMATIC_OBSERVER& aObserveSchematic = {},
            const LIBRARY_OBSERVER& aObserveLibraries = {} ) const;

    mutable std::mutex m_mutex;
    const AUXILIARY_OBSERVER m_observeAuxiliary;
    std::map<std::string, std::shared_ptr<JOB>> m_jobs;
};

#endif
