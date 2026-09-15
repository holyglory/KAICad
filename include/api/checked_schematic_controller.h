/* State-bound schematic mutation dispatch. GPL-3.0-or-later. */
#ifndef KICAD_CHECKED_SCHEMATIC_CONTROLLER_H
#define KICAD_CHECKED_SCHEMATIC_CONTROLLER_H

#include <api/api_handler.h>
#include <api/common/commands/automation_commands.pb.h>
#include <map>

class KICOMMON_API CHECKED_SCHEMATIC_CONTROLLER
{
public:
    using DISPATCH = std::function<API_RESULT( ApiRequest& )>;
    static bool Handles( const ApiRequest& aRequest );
    API_RESULT Handle( ApiRequest& aRequest, const std::string& aProcessEpoch,
                       const DISPATCH& aDispatch );

private:
    struct RECEIPT
    {
        kiapi::automation::v1::CheckedSchematicBatch request;
        kiapi::automation::v1::CheckedSchematicBatchReceipt result;
    };
    // Process-owned receipts survive editor reattachment. Never evict an ID:
    // a timed-out caller must not accidentally execute the same operation twice.
    std::map<std::string, RECEIPT> m_receipts;
    size_t m_reservedBytes = 0;
};

#endif
