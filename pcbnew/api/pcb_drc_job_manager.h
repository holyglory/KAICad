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
class FOOTPRINT_LIBRARY_ADAPTER;
class wxFileSystemWatcherEvent;
class wxString;
struct PCB_DRC_CAPTURE_CONTEXT;
struct PCB_DRC_PROJECT_OBSERVATION;

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
    // Resolves the owner's current footprint library adapter for a board, or
    // null when that board is not (or no longer) the owner's live board.
    using LIBRARY_RESOLVER = std::function<FOOTPRINT_LIBRARY_ADAPTER*( BOARD& )>;
    // Owner-thread callback resolves current editor settings at observations. It is
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

    /*
     * Native event ownership. Only an editor owner that runs on the native UI
     * thread and calls DetachBoard() before it replaces or destroys a board may
     * enable events; later jobs then subscribe to board commits and to native
     * notifications for their external rule and library files. A headless owner
     * never enables them: its jobs hold no reference to the source board and keep
     * the complete read-time comparisons, so the board may be replaced or
     * destroyed at any time. Live reads keep those comparisons in both modes
     * until a notification completeness barrier exists (n456d6b796cd7a9a3).
     */
    void EnableNativeEvents( LIBRARY_RESOLVER aResolveLibraries );
    // The board is about to be replaced or destroyed: its receipts become stale
    // (input_events_lost) and every subscription to it is released.
    void DetachBoard( const BOARD* aBoard );
    // A native committed change, undo/redo or settings edit reached the owner.
    void BoardChanged( const BOARD* aBoard );
    /*
     * A recovery checkpoint: observe every live receipt of this board again.
     * Already stale receipts never revive. The parity schematic, the drawing sheet
     * and router settings, the project settings with the custom rules file, and the
     * library content are each observed at most once per checkpoint, whatever the
     * number of receipts.
     *
     * Library content changes on disk reach receipts whose native file
     * notifications cover their libraries as notifications. Library configuration
     * changes in memory do not: path variables (Configure Paths), or a library row
     * disabled, repointed, reloaded or given another type or options. A settings
     * notification passes aLibraryConfigurationMayHaveChanged and compares library
     * content for every receipt. Window activation passes false: a covered receipt
     * then skips the library content only while each of its libraries still has
     * the row, resolved URI, type, options, disabled flag and loaded state recorded
     * when it started, compared without loading any footprint. Every read still
     * compares all inputs before exposing results (n456d6b796cd7a9a3).
     */
    void ObserveInputs( BOARD& aBoard, const std::string& aProcessEpoch,
                        const SCHEMATIC_OBSERVER& aObserveSchematic,
                        const LIBRARY_OBSERVER& aObserveLibraries,
                        bool aLibraryConfigurationMayHaveChanged );

private:
    friend struct DRC_CAPTURE_FIXTURE;
    struct INPUT_WATCHER;
    struct FILE_EVENTS;
    struct JOB;
    struct LIVE_INPUTS;
    static void invalidate( const std::shared_ptr<JOB>& aJob, const std::string& aCode,
                            const std::string& aMessage );
    std::unique_ptr<INPUT_WATCHER> watchInputs( BOARD& aBoard, const PCB_DRC_CAPTURE_CONTEXT& aContext );
    // Native notifications for this board may have been missed. Production loses
    // notifications only through fileEvent(); the lifecycle fixture calls this directly.
    void InputEventsLost( const BOARD* aBoard );
    void fileEvent( wxFileSystemWatcherEvent& aEvent );
    void retireWatches();
    // Lifecycle fixture introspection of the single shared native watcher: the
    // receipts sharing one directory subscription, and its native watched paths.
    int fileSubscribers( const wxString& aDirectory ) const;
    int nativeWatches() const;
    // Lifecycle fixture introspection without an observation: the stale reason of a
    // receipt a notification or checkpoint invalidated, or empty while it is live.
    std::string invalidation( const std::string& aJobId ) const;
    std::shared_ptr<JOB> find( const std::string& aJobId ) const;
    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> state(
            const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch,
            const SCHEMATIC_OBSERVER& aObserveSchematic = {},
            const LIBRARY_OBSERVER& aObserveLibraries = {} ) const;
    tl::expected<kiapi::automation::v1::PcbDrcJobState, std::string> state(
            const std::shared_ptr<JOB>& aJob, BOARD& aBoard, const std::string& aProcessEpoch,
            const LIVE_INPUTS& aInputs ) const;

    mutable std::mutex m_mutex;
    const AUXILIARY_OBSERVER m_observeAuxiliary;
    // Observes the live project inputs (PCB_DRC_PROJECT_BASELINE::Observe); the
    // lifecycle fixture counts observations through it.
    std::function<PCB_DRC_PROJECT_OBSERVATION( const BOARD& )> m_observeProject;
    std::map<std::string, std::shared_ptr<JOB>> m_jobs;
    // Owner-thread native subscriptions. Workers never see these objects.
    LIBRARY_RESOLVER m_resolveLibraries;
    bool m_eventsEnabled = false;
    std::unique_ptr<FILE_EVENTS> m_files;
    std::map<std::string, std::unique_ptr<INPUT_WATCHER>> m_watches;
};

#endif
