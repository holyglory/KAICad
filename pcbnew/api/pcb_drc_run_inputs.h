/* Owned native DRC input bundle. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_RUN_INPUTS_H
#define KICAD_PCB_DRC_RUN_INPUTS_H

#include <file_content_baseline.h>
#include <kiid.h>
#include <memory>
#include <string>
class BOARD;
class DRC_ENGINE;
class DRC_LIBRARY_INPUTS;
class DS_DATA_MODEL;
class DS_PROXY_VIEW_ITEM;
class FOOTPRINT_LIBRARY_ADAPTER;
class PCB_DRC_DOCUMENT_SNAPSHOT;
class PROGRESS_REPORTER;

struct PCB_DRC_CAPTURE_CONTEXT
{
    FOOTPRINT_LIBRARY_ADAPTER& libraries;
    DS_DATA_MODEL& drawing;
    KIID drawingIdentity;
};

class PCB_DRC_RUN_INPUTS
{
public:
    ~PCB_DRC_RUN_INPUTS();
    static std::unique_ptr<PCB_DRC_RUN_INPUTS> Capture(
            BOARD& aBoard, const PCB_DRC_CAPTURE_CONTEXT& aContext,
            PROGRESS_REPORTER* aReporter = nullptr );
    BOARD& GetBoard() const;
    void InitializeEngine( DRC_ENGINE& aEngine ) const;
    // Call inside DRC_RUN_SCOPE after its admission/cleanup guard is created.
    void BindInvocation( DRC_ENGINE& aEngine );
    const KIID& CapturedDrawingIdentity() const;
    const KIID& SourceDrawingIdentity() const { return m_sourceDrawingIdentity; }
    bool RulesUnchanged() const;

private:
    PCB_DRC_RUN_INPUTS() = default;
    std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> m_document;
    std::shared_ptr<const DRC_LIBRARY_INPUTS> m_libraries;
    std::unique_ptr<DS_DATA_MODEL> m_drawing;
    std::unique_ptr<DS_PROXY_VIEW_ITEM> m_proxy;
    FILE_CONTENT_BASELINE m_rulesBaseline;
    std::string m_rulesText;
    KIID m_sourceDrawingIdentity;
};
#endif
