/* Owned native DRC input bundle. GPL-3.0-or-later. */
#ifndef KICAD_PCB_DRC_RUN_INPUTS_H
#define KICAD_PCB_DRC_RUN_INPUTS_H

#include <file_content_baseline.h>
#include <google/protobuf/any.pb.h>
#include <google/protobuf/repeated_field.h>
#include <kiid.h>
#include <json_common.h>
#include <map>
#include <memory>
#include <string>
#include <tl/expected.hpp>
#include <vector>
namespace PNS { class ROUTING_SETTINGS; }
namespace kiapi::automation::v1 { class SchematicParityNetlistSnapshot; }
class BOARD;
class DRC_ENGINE;
class DRC_LIBRARY_INPUTS;
class DS_DATA_MODEL;
class DS_PROXY_VIEW_ITEM;
class FOOTPRINT_LIBRARY_ADAPTER;
class PCB_DRC_DOCUMENT_SNAPSHOT;
class PCB_DRC_SCHEMATIC_INPUT;
class PROGRESS_REPORTER;

struct PCB_DRC_CAPTURE_CONTEXT
{
    FOOTPRINT_LIBRARY_ADAPTER& libraries;
    DS_DATA_MODEL& drawing;
    KIID drawingIdentity;
    const PNS::ROUTING_SETTINGS* routingSettings = nullptr;
    // Native capture result, borrowed only for this uninterrupted checkpoint.
    const kiapi::automation::v1::SchematicParityNetlistSnapshot* schematic = nullptr;
};

struct PCB_DRC_COPPER_PREPARATION
{
    enum class STATUS { NOT_RUN, COMPLETED, CANCELLED, NOT_CONVERGED, FAILED };
    STATUS status = STATUS::NOT_RUN;
    std::string error;
    std::vector<KIID> regenerated;
};

// Lightweight, immutable receipt data; never borrows the live project or the
// worker's private board. This covers project/settings/rules, not every input.
class PCB_DRC_PROJECT_BASELINE
{
public:
    bool Unchanged( const BOARD& aBoard ) const;
    // The live project settings Unchanged() compares, observed once so that one
    // checkpoint can compare many baselines of the same board. Throws when the
    // settings cannot be represented; that is never evidence of freshness.
    static nlohmann::json ObserveSettings( const BOARD& aBoard );
    bool Unchanged( const BOARD& aBoard, const nlohmann::json& aObservedSettings ) const;

private:
    friend class PCB_DRC_RUN_INPUTS;
    FILE_CONTENT_BASELINE m_rules;
    nlohmann::json m_settings;
};

// Retained native presentation/router state, without any live editor pointers.
class PCB_DRC_AUXILIARY_BASELINE
{
public:
    static PCB_DRC_AUXILIARY_BASELINE Capture( const PCB_DRC_CAPTURE_CONTEXT& aContext );
    bool Unchanged( const PCB_DRC_CAPTURE_CONTEXT& aContext ) const;
    std::string Fingerprint() const;

private:
    nlohmann::json m_state;
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
    void SetSchematicInput( std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> aInput );
    const KIID& CapturedDrawingIdentity() const;
    const KIID& SourceDrawingIdentity() const { return m_sourceDrawingIdentity; }
    bool RulesUnchanged() const;
    const PCB_DRC_PROJECT_BASELINE& ProjectBaseline() const { return m_projectBaseline; }
    const PCB_DRC_AUXILIARY_BASELINE& AuxiliaryBaseline() const { return m_auxiliaryBaseline; }
    std::string LibraryFingerprint() const;
    // The captured content digest of each footprint library the board uses.
    std::map<wxString, std::string> LibraryFingerprints() const;
    bool HasLibraryDependencies() const;

    // Add explicitly net-bound Track/Arc/Via candidates only to this detached
    // DRC snapshot. The live editor board is never changed.
    tl::expected<std::vector<KIID>, std::string> AddCandidateItems(
            const google::protobuf::RepeatedPtrField<google::protobuf::Any>& aItems );

    // Mutates only this bundle's detached board. Failure makes the preparation
    // unusable: capture a fresh bundle instead of retrying partially prepared data.
    // Call after installing and initializing the board's owned DRC engine.
    const PCB_DRC_COPPER_PREPARATION& PrepareCopper( PROGRESS_REPORTER* aReporter = nullptr );

private:
    PCB_DRC_RUN_INPUTS() = default;
    std::unique_ptr<PCB_DRC_DOCUMENT_SNAPSHOT> m_document;
    std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> m_schematic;
    std::shared_ptr<const DRC_LIBRARY_INPUTS> m_libraries;
    std::unique_ptr<DS_DATA_MODEL> m_drawing;
    std::unique_ptr<DS_PROXY_VIEW_ITEM> m_proxy;
    FILE_CONTENT_BASELINE m_rulesBaseline;
    PCB_DRC_PROJECT_BASELINE m_projectBaseline;
    PCB_DRC_AUXILIARY_BASELINE m_auxiliaryBaseline;
    std::string m_rulesText;
    KIID m_sourceDrawingIdentity;
    std::unique_ptr<PNS::ROUTING_SETTINGS> m_routingSettings;
    PCB_DRC_COPPER_PREPARATION m_copperPreparation;
};
#endif
