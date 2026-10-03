/* Checked PCB route dispatch and process-owned retry receipts. GPL-3.0-or-later. */
#ifndef KICAD_CHECKED_PCB_ROUTE_CONTROLLER_H
#define KICAD_CHECKED_PCB_ROUTE_CONTROLLER_H

#include <api/api_handler.h>
#include <api/common/commands/checked_pcb_route_commands.pb.h>
#include <map>
#include <string>
#include <vector>

class CHECKED_PCB_ROUTE_CONTROLLER
{
public:
    using DISPATCH = std::function<API_RESULT( ApiRequest& )>;
    static const std::vector<std::string>& RequestTypes();
    static bool Handles( const ApiRequest& aRequest );
    API_RESULT Handle( ApiRequest& aRequest, const std::string& aProcessEpoch,
                       const DISPATCH& aDispatch );

private:
    struct RECEIPT
    {
        kiapi::automation::v1::CheckedPcbRouteCommit request;
        kiapi::automation::v1::CheckedPcbRouteReceipt result;
    };
    std::map<std::string, RECEIPT> m_receipts;
};

#endif
