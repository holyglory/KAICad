/* Checked lifecycle dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#ifndef KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#define KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#include <api/api_handler.h>
#include <api/common/commands/automation_commands.pb.h>
#include <map>
#include <string>
#include <vector>
#include <wx/string.h>

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
     * Native writers call this during a checked save to say why a file was not written, for
     * example because it is read-only or the disk is full.  The checked save result names the
     * file and the reason.  Outside a checked save on this thread it does nothing.
     */
    static void ReportWriteFailure( const wxString& aPath, const wxString& aReason );

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
