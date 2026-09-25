/* Checked lifecycle dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#ifndef KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#define KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#include <api/api_handler.h>
#include <api/common/commands/automation_commands.pb.h>
#include <map>
#include <string>
#include <vector>
#include <wx/string.h>

class PROJECT;

class KICOMMON_API DOCUMENT_LIFECYCLE_CONTROLLER
{
public:
    using DISPATCH = std::function<API_RESULT( ApiRequest& )>;

    /**
     * The fully qualified request message types this controller claims before any handler sees
     * them, in ordinal order.  Handles() and the automation handshake both read this one list, so
     * the handshake advertises exactly what the controller dispatches.
     */
    static const std::vector<std::string>& RequestTypes();
    static bool Handles( const ApiRequest& aRequest );
    API_RESULT Handle( ApiRequest& aRequest, const std::string& aProcessEpoch, const DISPATCH& aDispatch );
    void RememberCleanState( const kiapi::automation::v1::DocumentLifecycleState& aState );
    void AnnotateCleanState( kiapi::automation::v1::DocumentLifecycleState& aState ) const;
    bool IsCleanCloseActive() const { return m_cleanCloseActive; }
    static bool HasUnchangedFileBaselines( const kiapi::automation::v1::DocumentLifecycleState& aState );

    /**
     * Why KiCad cannot replace @a aPath, or an empty string when it can.  Native saves write a
     * sibling temporary file and rename it over the target, so the folder that holds the file
     * (after following a symbolic link, as the writer does) must accept new files as well.
     */
    static wxString WriteBlocker( const wxString& aPath );

    /**
     * Why KiCad holds @a aProject read-only and writes none of its files, or an empty string when
     * it does not.  KiCad decides this when it opens the project (the project file was read-only
     * then, or KiCad could not take the project lock), so the reason says what to change and that
     * the project must then be reopened.  For the lock it looks at the lock file as it is now: it
     * names the holder when another program holds it, the lock file when it is read-only, the
     * other user when the record names one, and says so when nothing holds it.  While KiCad keeps
     * its own lock object for the project, which may hold the lock file itself, it never calls
     * the holder another program and names the recorded owner instead.
     */
    static wxString ReadOnlyProjectReason( const PROJECT& aProject );

    /// Why a native saver did not write a document file during a checked save.
    enum class SAVE_PROBLEM
    {
        /// The file system would not accept the write: a read-only file or folder, a folder that
        /// cannot be created, a full disk or another system error while writing.
        WRITE_BLOCKED,
        /// KiCad refused to save for a reason that making files writable does not fix, for
        /// example conflicting root page numbers or a sheet without a file name.
        SAVE_REFUSED,
        /// Writing the file failed, but neither the writer nor a check of the file and its folder
        /// found why (the project settings writer keeps its system error to itself).  The file is
        /// named with that explanation and is never listed as blocked.
        WRITE_FAILED
    };

    /// Message prefix, followed by ':', of the refusal to read an operation this process has no
    /// receipt for.  Clients match this fixed marker, never the explanation after it.
    static constexpr const char* UNKNOWN_OPERATION_MARKER = "lifecycle_operation_not_started";

    /**
     * Native savers call this during a checked save to say why they did not write a file.  The
     * checked save result names every problem with its reason.  Only WRITE_BLOCKED problems
     * make the result report file_not_writable and list the file among its blocked files;
     * SAVE_REFUSED problems make it native_save_refused, and WRITE_FAILED problems alone
     * native_save_failed.
     * @a aPath may be empty when the problem concerns no single file.  Outside a checked save on
     * this thread it does nothing.
     */
    static void ReportSaveProblem( SAVE_PROBLEM aKind, const wxString& aPath, const wxString& aReason );

private:
    struct RECEIPT
    {
        kiapi::automation::v1::CheckedSaveDocument request;
        kiapi::automation::v1::LifecycleOperationResult result;
        bool close = false;
    };
    // Never evict completed IDs: otherwise an old timeout retry could execute twice.
    std::map<std::string, RECEIPT> m_receipts;
    size_t m_reservedBytes = 0;
    // One native editor container per scope in each project process. Epoch/identity
    // checks prevent a replaced container inheriting its predecessor's checkpoint.
    struct CHECKPOINT { std::string epoch, identity, sha; };
    std::map<int, CHECKPOINT> m_cleanByScope;
    bool m_cleanCloseActive = false;
};
#endif
