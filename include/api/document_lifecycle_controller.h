/* Checked lifecycle dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#ifndef KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#define KICAD_DOCUMENT_LIFECYCLE_CONTROLLER_H
#include <api/api_handler.h>
#include <api/common/commands/automation_commands.pb.h>
#include <map>

class KICOMMON_API DOCUMENT_LIFECYCLE_CONTROLLER
{
public:
    using DISPATCH = std::function<API_RESULT( ApiRequest& )>;
    static bool Handles( const ApiRequest& aRequest );
    API_RESULT Handle( ApiRequest& aRequest, const std::string& aProcessEpoch, const DISPATCH& aDispatch );

private:
    struct RECEIPT
    {
        kiapi::automation::v1::CheckedSaveDocument request;
        kiapi::automation::v1::LifecycleOperationResult result;
    };
    // Never evict completed IDs: otherwise an old timeout retry could execute twice.
    std::map<std::string, RECEIPT> m_receipts;
    size_t m_reservedBytes = 0;
};
#endif
