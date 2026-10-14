/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2023 Jon Evans <jon@craftyjon.com>
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 *
 * This program is free software: you can redistribute it and/or modify it
 * under the terms of the GNU General Public License as published by the
 * Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef KICAD_API_HANDLER_PCB_H
#define KICAD_API_HANDLER_PCB_H

#include <map>
#include <memory>
#include <mutex>
#include <optional>

#include <api/api_handler_board.h>
#include <api/pcb_context.h>
#include <api/board/board_jobs.pb.h>
#include <api/common/commands/cross_probe_commands.pb.h>
#include <api/common/commands/project_commands.pb.h>
#include <api/pcb_drc_job_manager.h>
#include <api/pcb_manufacturing_job_manager.h>
#include <api/common/commands/automation_commands.pb.h>
#include <api/common/commands/pcb_render_inspection_commands.pb.h>
#include <api/common/commands/pcb_manufacturing_commands.pb.h>
#include <properties/property_mgr.h>
#include <file_content_baseline.h>

using namespace kiapi::board::jobs;


class PCB_EDIT_FRAME;
class PCB_TRACK;
class PROPERTY_BASE;
class GENERATOR_TOOL;
class TOOL_MANAGER;


class API_HANDLER_PCB : public API_HANDLER_BOARD
{
public:
    API_HANDLER_PCB( PCB_EDIT_FRAME* aFrame );
    API_HANDLER_PCB( std::shared_ptr<PCB_CONTEXT> aContext, PCB_EDIT_FRAME* aFrame = nullptr );
    ~API_HANDLER_PCB() override;

    // Native editor boundaries for PCB DRC jobs; the owning frame calls these on
    // its UI thread. A handler without a frame never subscribes to board events.
    void DetachDrcBoard( const BOARD* aBoard )
    {
        clearRoutePreviewSessions();
        m_drcJobs.DetachBoard( aBoard );
    }
    void DrcBoardChanged( const BOARD* aBoard )
    {
        // A GUI edit invalidates every detached router branch before DRC
        // observes the new board revision. The branch is preview-only, so
        // invalidation never creates an undo item or board mutation.
        clearRoutePreviewSessions();
        m_drcJobs.BoardChanged( aBoard );
    }
    // Recovery checkpoint after activation or a settings notification. A settings
    // notification passes true: it may have changed library configuration in memory
    // (path variables, library rows) without a file notification, so library content
    // is compared for every check. Activation passes false and compares each check's
    // library configuration first (PCB_DRC_JOB_MANAGER::ObserveInputs).
    void ObserveNativeDrcInputs( bool aLibraryConfigurationMayHaveChanged );

private:
    // Direct unit fixtures may invoke an unregistered handler. Production
    // handlers use the server that registered this handler; the fallback keeps
    // those fixture calls bound to the established global server.
    KICAD_API_SERVER& drcApiServer() const;

    typedef std::map<std::string, PROPERTY_BASE*> PROTO_PROPERTY_MAP;

    HANDLER_RESULT<commands::GetOpenDocumentsResponse> handleGetOpenDocuments(
            const HANDLER_CONTEXT<commands::GetOpenDocuments>& aCtx );

    HANDLER_RESULT<Empty> handleSaveDocument( const HANDLER_CONTEXT<commands::SaveDocument>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::DocumentLifecycleState> handleReadLifecycleState(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadDocumentLifecycleState>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbDrcState> handleReadDrcState(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbDrcState>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> handleStartDrcJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::StartPcbDrcJob>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> handleReadDrcJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbDrcJob>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityPcbDrcReadOperationResponse>
    handleReadDrcOperation(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityPcbDrcReadOperation>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> handleReadDrcReplacementJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityPcbDrcReplacementJob>& aCtx );
    tl::expected<kiapi::automation::v1::DocumentLifecycleState, std::string> observeDrcSchematic(
            const kiapi::common::types::DocumentSpecifier& aDocument );
    tl::expected<std::string, std::string> observeDrcLibraries( BOARD& aBoard );
    tl::expected<std::string, std::string> observeDrcAuxiliary( BOARD& aBoard );
    HANDLER_RESULT<kiapi::automation::v1::PcbDrcJobState> handleCancelDrcJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::CancelPcbDrcJob>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> handleRoutePreview(
            const HANDLER_CONTEXT<kiapi::automation::v1::StartPcbRoutePreview>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> handleContinueRoutePreview(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewContinue>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> handleCancelRoutePreview(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewCancel>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> handleReadRoutePreview(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityRoutePreviewRead>& aCtx );

    HANDLER_RESULT<Empty> handleSaveCopyOfDocument(
            const HANDLER_CONTEXT<commands::SaveCopyOfDocument>& aCtx );

    HANDLER_RESULT<Empty> handleRevertDocument(
            const HANDLER_CONTEXT<commands::RevertDocument>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetItems(
            const HANDLER_CONTEXT<commands::GetItems>& aCtx );

    HANDLER_RESULT<BoardEnabledLayersResponse> handleSetBoardEnabledLayers(
            const HANDLER_CONTEXT<SetBoardEnabledLayers>& aCtx );

    HANDLER_RESULT<BoardDesignRulesResponse> handleGetBoardDesignRules(
            const HANDLER_CONTEXT<GetBoardDesignRules>& aCtx );

    HANDLER_RESULT<BoardDesignRulesResponse> handleSetBoardDesignRules(
            const HANDLER_CONTEXT<SetBoardDesignRules>& aCtx );

    HANDLER_RESULT<CustomRulesResponse> handleGetCustomDesignRules(
            const HANDLER_CONTEXT<GetCustomDesignRules>& aCtx );

    HANDLER_RESULT<CustomRulesResponse> handleSetCustomDesignRules(
            const HANDLER_CONTEXT<SetCustomDesignRules>& aCtx );

    HANDLER_RESULT<common::types::EmbeddedFiles> handleGetEmbeddedFiles(
            const HANDLER_CONTEXT<GetEmbeddedFiles>& aCtx );

    HANDLER_RESULT<google::protobuf::Empty> handleAddEmbeddedFiles(
            const HANDLER_CONTEXT<AddEmbeddedFiles>& aCtx );

    HANDLER_RESULT<google::protobuf::Empty> handleSetEmbeddedFiles(
            const HANDLER_CONTEXT<SetEmbeddedFiles>& aCtx );

    HANDLER_RESULT<types::Vector2> handleGetBoardOrigin(
            const HANDLER_CONTEXT<GetBoardOrigin>& aCtx );

    HANDLER_RESULT<Empty> handleSetBoardOrigin( const HANDLER_CONTEXT<SetBoardOrigin>& aCtx );

    HANDLER_RESULT<BoardLayerNameResponse> handleGetBoardLayerName(
            const HANDLER_CONTEXT<GetBoardLayerName>& aCtx );

    HANDLER_RESULT<BoardLayerResponse> handleGetBoardLayerByName(
            const HANDLER_CONTEXT<GetBoardLayerByName>& aCtx );

    HANDLER_RESULT<NetsResponse> handleGetNets( const HANDLER_CONTEXT<GetNets>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetConnectedItems(
            const HANDLER_CONTEXT<GetConnectedItems>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetItemsByNet(
            const HANDLER_CONTEXT<GetItemsByNet>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetItemsByNetClass(
            const HANDLER_CONTEXT<GetItemsByNetClass>& aCtx );

    HANDLER_RESULT<NetClassForNetsResponse> handleGetNetClassForNets(
            const HANDLER_CONTEXT<GetNetClassForNets>& aCtx );

    HANDLER_RESULT<Empty> handleRefillZones( const HANDLER_CONTEXT<RefillZones>& aCtx );

    HANDLER_RESULT<ImportNetlistResponse> handleImportNetlist(
            const HANDLER_CONTEXT<ImportNetlist>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::NativeCapabilityPcbReadOperationResponse>
    handleReadPcbOperation(
            const HANDLER_CONTEXT<kiapi::automation::v1::NativeCapabilityPcbReadOperation>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceipt>
    handleRunPcbManufacturingExport(
            const HANDLER_CONTEXT<kiapi::automation::v1::RunPcbManufacturingExport>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceiptResponse>
    handleReadPcbManufacturingExport(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbManufacturingExport>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceipt>
    handleStartPcbManufacturingExportJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::StartPcbManufacturingExportJob>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceiptResponse>
    handleReadPcbManufacturingExportJob(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadPcbManufacturingExportJob>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingManagerCapabilities>
    handleGetPcbManufacturingManagerCapabilities(
            const HANDLER_CONTEXT<kiapi::automation::v1::GetPcbManufacturingManagerCapabilities>& aCtx );

    // Native manager recovery seam. SVG, Gerber, ODB++ and IPC-2581 requests may use the
    // KiCad-owned synchronous plot adapters on this handler's owner thread.
    // Detached workers and restart publication remain separate unsupported
    // features; the legacy synchronous exporter remains backward compatible.
    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceipt>
    handleCancelPcbManufacturingExport(
            const HANDLER_CONTEXT<kiapi::automation::v1::CancelPcbManufacturingExport>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::PcbManufacturingExportReceiptResponse>
    handleReconcilePcbManufacturingExport(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReconcilePcbManufacturingExport>& aCtx );

    HANDLER_RESULT<BoardEditorAppearanceSettings> handleGetBoardEditorAppearanceSettings(
            const HANDLER_CONTEXT<GetBoardEditorAppearanceSettings>& aCtx );

    HANDLER_RESULT<Empty> handleSetBoardEditorAppearanceSettings(
            const HANDLER_CONTEXT<SetBoardEditorAppearanceSettings>& aCtx );

    HANDLER_RESULT<BoardPlotSettingsResponse>
    handleGetBoardPlotSettings( const HANDLER_CONTEXT<GetBoardPlotSettings>& aCtx );

    HANDLER_RESULT<Empty> handleSetBoardPlotSettings( const HANDLER_CONTEXT<SetBoardPlotSettings>& aCtx );

    HANDLER_RESULT<InjectDrcErrorResponse> handleInjectDrcError(
            const HANDLER_CONTEXT<InjectDrcError>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExport3D(
            const HANDLER_CONTEXT<RunBoardJobExport3D>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportRender(
            const HANDLER_CONTEXT<RunBoardJobExportRender>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::PcbRenderInspectionResponse> handlePcbRenderInspection(
            const HANDLER_CONTEXT<kiapi::automation::v1::PcbRenderInspectionRequest>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportSvg(
            const HANDLER_CONTEXT<RunBoardJobExportSvg>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportDxf(
            const HANDLER_CONTEXT<RunBoardJobExportDxf>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportPdf(
            const HANDLER_CONTEXT<RunBoardJobExportPdf>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportPs(
            const HANDLER_CONTEXT<RunBoardJobExportPs>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportGerbers(
            const HANDLER_CONTEXT<RunBoardJobExportGerbers>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportDrill(
            const HANDLER_CONTEXT<RunBoardJobExportDrill>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportPosition(
            const HANDLER_CONTEXT<RunBoardJobExportPosition>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportGencad(
            const HANDLER_CONTEXT<RunBoardJobExportGencad>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportIpc2581(
            const HANDLER_CONTEXT<RunBoardJobExportIpc2581>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportIpcD356(
            const HANDLER_CONTEXT<RunBoardJobExportIpcD356>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportODB(
            const HANDLER_CONTEXT<RunBoardJobExportODB>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunBoardJobExportStats(
            const HANDLER_CONTEXT<RunBoardJobExportStats>& aCtx );

    HANDLER_RESULT<commands::CrossProbeAnnounceResponse> handleCrossProbeAnnounce(
            const HANDLER_CONTEXT<commands::CrossProbeAnnounce>& aCtx );

    HANDLER_RESULT<commands::SyncSelectionResponse> handleSyncSelection(
            const HANDLER_CONTEXT<commands::SyncSelection>& aCtx );

    HANDLER_RESULT<commands::HighlightNetsResponse> handleHighlightNets(
            const HANDLER_CONTEXT<commands::HighlightNets>& aCtx );

protected:
    kiapi::common::types::DocumentType thisDocumentType() const override
    {
        return kiapi::common::types::DOCTYPE_PCB;
    }

    tl::expected<bool, ApiResponseStatus> validateDocumentInternal( const DocumentSpecifier& aDocument ) const override;

    std::optional<TITLE_BLOCK*> getTitleBlock() override;

    std::optional<PAGE_INFO> getPageSettings() override;

    bool setPageSettings( const PAGE_INFO& aPageInfo ) override;

    wxString getDrawingSheetFileName() override;

    void setDrawingSheetFileName( const wxString& aFileName ) override;

    void onModified() override;

private:
    struct ROUTE_PREVIEW_SESSION;

    void clearRoutePreviewSessions();
    HANDLER_RESULT<kiapi::automation::v1::PcbRoutePreviewState> advanceRoutePreviewSession(
            ROUTE_PREVIEW_SESSION& aSession,
            const google::protobuf::RepeatedPtrField<kiapi::common::types::Vector2>& aWaypoints,
            bool aForceFinish );

    PCB_CONTEXT* pcbContext() const { return static_cast<PCB_CONTEXT*>( context() ); }

    // The custom-rules sidecar is not owned by BOARD::FileBaseline(). Keep one
    // baseline for the open board so an external edit after a lifecycle read is
    // rejected instead of being adopted as a new admission baseline.
    void refreshCustomRulesBaseline();

    PCB_EDIT_FRAME* frame() const;

    PCB_DRC_JOB_MANAGER m_drcJobs;
    PCB_MANUFACTURING_JOB_MANAGER m_manufacturingJobs;
    std::map<std::string, std::unique_ptr<ROUTE_PREVIEW_SESSION>> m_routePreviewSessions;

    struct PCB_OPERATION_RECEIPT
    {
        mutable std::mutex mutex;
        std::string operation;
        std::string fingerprint;
        DocumentSpecifier document;
        std::string request;
        bool completed = false;
        bool terminal = false;
        kiapi::automation::v1::NativeZoneFillOutcome zoneFillOutcome = kiapi::automation::v1::NZFO_UNKNOWN;
        kiapi::automation::v1::NativeZoneFillOutcome primaryFillOutcome = kiapi::automation::v1::NZFO_UNKNOWN;
        kiapi::automation::v1::NativeZoneFillOutcome generatorRefillOutcome = kiapi::automation::v1::NZFO_UNKNOWN;
        bool generatorRefillRequired = false;
        bool primaryCommitApplied = false;
        bool generatorRefillCommitApplied = false;
        bool rollbackCompleted = false;
        std::string failureStage;
        std::string errorCode;
        std::string errorMessage;
        std::string processEpoch;
        bool commitStateKnown = false;
        std::optional<ImportNetlistResponse> importResponse;
        std::optional<kiapi::automation::v1::PcbManufacturingExportReceipt> manufacturingExport;
    };

    using PCB_OPERATION_RECEIPTS = std::map<std::string, std::shared_ptr<PCB_OPERATION_RECEIPT>>;

    // A receipt belongs to the native process epoch, not to an editor handler.
    // All handlers registered with the same server must share its lookup, including
    // after an editor closes and another handler opens the document again.
    PCB_OPERATION_RECEIPTS& operationReceipts() const;
};

#endif //KICAD_API_HANDLER_PCB_H
