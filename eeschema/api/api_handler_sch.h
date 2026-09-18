/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 *
 * Copyright (C) 2024 Jon Evans <jon@craftyjon.com>
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

#ifndef KICAD_API_HANDLER_SCH_H
#define KICAD_API_HANDLER_SCH_H

#include <api/api_handler_editor.h>
#include <api/common/commands/automation_commands.pb.h>
#include <api/sch_context.h>
#include <api/common/commands/cross_probe_commands.pb.h>
#include <api/common/commands/editor_commands.pb.h>
#include <api/common/commands/project_commands.pb.h>
#include <google/protobuf/empty.pb.h>
#include <api/schematic/schematic_commands.pb.h>
#include <api/schematic/schematic_jobs.pb.h>
#include <kiid.h>

using namespace kiapi;
using namespace kiapi::common;

using google::protobuf::Empty;

class SCH_EDIT_FRAME;
class SCH_ITEM;
class SCH_SHEET;


class API_HANDLER_SCH : public API_HANDLER_EDITOR
{
public:
    API_HANDLER_SCH( SCH_EDIT_FRAME* aFrame );
    API_HANDLER_SCH( std::shared_ptr<SCH_CONTEXT> aContext, SCH_EDIT_FRAME* aFrame = nullptr );

protected:
    std::optional<ApiResponseStatus> checkForHeadless( const std::string& aCommandName ) const;

    std::unique_ptr<COMMIT> createCommit() override;

    kiapi::common::types::DocumentType thisDocumentType() const override
    {
        return kiapi::common::types::DOCTYPE_SCHEMATIC;
    }

    const EDA_IU_SCALE& getIuScale() const override { return schIUScale; }

    tl::expected<bool, ApiResponseStatus> validateDocumentInternal( const DocumentSpecifier& aDocument ) const override;

    std::optional<SCH_ITEM*> getItemById( const KIID& aId, SCH_SHEET_PATH* aPathOut = nullptr ) const;

    HANDLER_RESULT<std::unique_ptr<EDA_ITEM>> createItemForType( KICAD_T aType,
                                                                 EDA_ITEM* aContainer );

    HANDLER_RESULT<types::ItemRequestStatus> handleCreateUpdateItemsInternal( bool aCreate,
            const std::string& aClientName,
            const types::ItemHeader &aHeader,
            const google::protobuf::RepeatedPtrField<google::protobuf::Any>& aItems,
            std::function<void(commands::ItemStatus, google::protobuf::Any)> aItemHandler )
            override;

    void deleteItemsInternal( std::map<KIID, ItemDeletionStatus>& aItemsToDelete,
                              const std::string& aClientName ) override;

    std::optional<EDA_ITEM*> getItemFromDocument( const DocumentSpecifier& aDocument,
                                                  const KIID& aId ) override;

    std::optional<TITLE_BLOCK*> getTitleBlock() override;

    std::optional<PAGE_INFO> getPageSettings() override;

    bool setPageSettings( const PAGE_INFO& aPageInfo ) override;

    wxString getDrawingSheetFileName() override;

    void setDrawingSheetFileName( const wxString& aFileName ) override;

    void onModified() override;

    SCH_CONTEXT* context() const { return m_context.get(); }

    TOOL_MANAGER* toolManager() const { return context()->GetToolManager(); }

    PROJECT& project() const { return context()->Prj(); }

    HANDLER_RESULT<kiapi::automation::v1::SchematicMetadataSnapshot> handleReadMetadata(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicMetadata>& aCtx );

private:
    static std::optional<kiapi::common::ApiResponseStatus> validateSnapshotSchema( uint32_t aVersion );
    static void projectSnapshotSchema( kiapi::schematic::types::SchematicMetadata& aMetadata,
                                       uint32_t aVersion );
    HANDLER_RESULT<kiapi::automation::v1::SchematicSaveState> handleReadSaveState(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicSaveState>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::DocumentLifecycleState> handleReadLifecycleState(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadDocumentLifecycleState>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicParityNetlistSnapshot> handleReadParityNetlist(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicParityNetlist>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicHierarchyDataSnapshot> handleReadHierarchyData(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicHierarchyData>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicElectricalState> handleReadElectricalState(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicElectricalState>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicMetadataSnapshot> readMetadataForPath(
            const SCH_SHEET_PATH& aPath, const kiapi::common::types::DocumentSpecifier& aDocument );
    HANDLER_RESULT<kiapi::schematic::types::SchematicScreenData> readScreenDataForPath(
            const SCH_SHEET_PATH& aPath, const kiapi::common::types::DocumentSpecifier& aDocument );
    HANDLER_RESULT<kiapi::automation::v1::SchematicObservation> handleCaptureObservation(
            const HANDLER_CONTEXT<kiapi::automation::v1::CaptureSchematicObservation>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::SchematicScreenDataSnapshot> handleReadScreenData(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicScreenData>& aCtx );

    HANDLER_RESULT<kiapi::automation::v1::SchematicOperationReceipt> handleInspectOperation(
            const HANDLER_CONTEXT<kiapi::automation::v1::InspectSchematicOperation>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicPresentationFacts> handleReadPresentationFacts(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicPresentationFacts>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicPlacementGeometry> handleMeasurePlacement(
            const HANDLER_CONTEXT<kiapi::automation::v1::MeasureSchematicPlacement>& aCtx );
    HANDLER_RESULT<commands::GetBoundingBoxResponse> handleGetBoundingBox(
            const HANDLER_CONTEXT<commands::GetBoundingBox>& aCtx );
    bool m_atomicBatchActive = false;
    // Non-owning pointer to the current request's staged-object ownership.
    // These objects are not in the screen until the native commit is pushed.
    using STAGED_ITEMS = std::map<std::pair<SCH_SCREEN*, KIID>, std::unique_ptr<SCH_ITEM>>;
    STAGED_ITEMS* m_atomicCreatedItems = nullptr;
    const SCH_SHEET_PATH* m_atomicTargetPath = nullptr;
    std::optional<SCH_SHEET_PATH> resolveBatchSheet( const KIID_PATH& aPath ) const;
    HANDLER_RESULT<kiapi::automation::v1::SchematicItemBatchResult> handleApplyItemBatch(
            const HANDLER_CONTEXT<kiapi::automation::v1::ApplySchematicItemBatch>& aCtx );
    HANDLER_RESULT<types::PageSettings> handleGetPageSettings(
            const HANDLER_CONTEXT<commands::GetPageSettings>& aCtx );
    HANDLER_RESULT<types::PageSettings> handleSetPageSettings(
            const HANDLER_CONTEXT<commands::SetPageSettings>& aCtx );
    HANDLER_RESULT<bool> validateDisplayedSheet( const DocumentSpecifier& aDocument );
    HANDLER_RESULT<types::TitleBlockInfo> handleGetTitleBlockInfo(
            const HANDLER_CONTEXT<commands::GetTitleBlockInfo>& aCtx ) override;
    HANDLER_RESULT<google::protobuf::Empty> handleSetTitleBlockInfo(
            const HANDLER_CONTEXT<commands::SetTitleBlockInfo>& aCtx ) override;
    std::optional<ApiResponseStatus> checkForStableObservation();
    HANDLER_RESULT<kiapi::automation::v1::SchematicChangeJournal> handleReadChangeJournal(
            const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicChangeJournal>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicPreview> handleCapturePreview(
            const HANDLER_CONTEXT<kiapi::automation::v1::CaptureSchematicPreview>& aCtx );
    HANDLER_RESULT<kiapi::automation::v1::SchematicViewSet> handleRenderViews(
            const HANDLER_CONTEXT<kiapi::automation::v1::RenderSchematicViews>& aCtx );
    HANDLER_RESULT<types::DocumentSpecifier> handleActivateSheet(
            const HANDLER_CONTEXT<kiapi::automation::v1::ActivateSchematicSheet>& aCtx );

    HANDLER_RESULT<google::protobuf::Empty> handleSaveDocument(
            const HANDLER_CONTEXT<commands::SaveDocument>& aCtx );

    HANDLER_RESULT<google::protobuf::Empty> handleSaveCopyOfDocument(
            const HANDLER_CONTEXT<commands::SaveCopyOfDocument>& aCtx );

    HANDLER_RESULT<google::protobuf::Empty>
    handleRevertDocument( const HANDLER_CONTEXT<commands::RevertDocument>& aCtx );

    HANDLER_RESULT<commands::GetOpenDocumentsResponse>
    handleGetOpenDocuments( const HANDLER_CONTEXT<commands::GetOpenDocuments>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse> handleGetItems( const HANDLER_CONTEXT<commands::GetItems>& aCtx );

    HANDLER_RESULT<commands::GetItemsResponse>
    handleGetItemsById( const HANDLER_CONTEXT<commands::GetItemsById>& aCtx );

    HANDLER_RESULT<commands::SelectionResponse>
    handleGetSelection( const HANDLER_CONTEXT<commands::GetSelection>& aCtx );

    HANDLER_RESULT<Empty> handleClearSelection( const HANDLER_CONTEXT<commands::ClearSelection>& aCtx );

    HANDLER_RESULT<commands::SelectionResponse>
    handleAddToSelection( const HANDLER_CONTEXT<commands::AddToSelection>& aCtx );

    HANDLER_RESULT<commands::SelectionResponse>
    handleRemoveFromSelection( const HANDLER_CONTEXT<commands::RemoveFromSelection>& aCtx );

    HANDLER_RESULT<types::RunJobResponse>
    handleRunSchematicJobExportSvg( const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportSvg>& aCtx );

    HANDLER_RESULT<types::RunJobResponse>
    handleRunSchematicJobExportDxf( const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportDxf>& aCtx );

    HANDLER_RESULT<types::RunJobResponse>
    handleRunSchematicJobExportPdf( const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportPdf>& aCtx );

    HANDLER_RESULT<types::RunJobResponse>
    handleRunSchematicJobExportPs( const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportPs>& aCtx );

    HANDLER_RESULT<types::RunJobResponse> handleRunSchematicJobExportNetlist(
            const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportNetlist>& aCtx );

    HANDLER_RESULT<types::RunJobResponse>
    handleRunSchematicJobExportBOM( const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportBOM>& aCtx );

    HANDLER_RESULT<kiapi::schematic::commands::SchematicHierarchyResponse>
    handleGetSchematicHierarchy( const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicHierarchy>& aCtx );

    void packSheetInstance( kiapi::schematic::types::SheetInstance* aInstance, SCH_SHEET_PATH& aPath,
                            SCH_SHEET* aSheet );

    /// Serializes a schematic item into @p aOut, using path-aware packing for symbols and sheets.
    /// Returns false if the item could not be packed (e.g. a symbol/sheet missing instance data).
    bool packSchItem( google::protobuf::Any& aOut, SCH_ITEM* aItem, const SCH_SHEET_PATH& aPath );

    HANDLER_RESULT<kiapi::schematic::commands::SchematicNetlistResponse>
    handleGetSchematicNetlist( const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicNetlist>& aCtx );

    HANDLER_RESULT<commands::CrossProbeAnnounceResponse>
    handleCrossProbeAnnounce( const HANDLER_CONTEXT<commands::CrossProbeAnnounce>& aCtx );

    HANDLER_RESULT<commands::SyncSelectionResponse>
    handleSyncSelection( const HANDLER_CONTEXT<commands::SyncSelection>& aCtx );

    HANDLER_RESULT<commands::HighlightNetsResponse> handleHighlightNets(
            const HANDLER_CONTEXT<commands::HighlightNets>& aCtx );

    HANDLER_RESULT<kiapi::schematic::commands::SchematicVariantsResponse>
    handleGetSchematicVariants( const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicVariants>& aCtx );

    HANDLER_RESULT<commands::ExpandTextVariablesResponse>
    handleExpandTextVariables( const HANDLER_CONTEXT<commands::ExpandTextVariables>& aCtx );

    SCHEMATIC* schematic() const;

    void filterValidSchTypes( std::set<KICAD_T>& aTypeList );

    SCH_EDIT_FRAME*              m_frame;
    std::shared_ptr<SCH_CONTEXT> m_context;
    static std::set<KICAD_T>     s_allowedTypes;

    struct BATCH_RECEIPT
    {
        std::string request;
        kiapi::automation::v1::SchematicItemBatchResult result;
        bool completed = false;
        std::optional<ApiResponseStatus> failure;
    };
    std::string m_batchReceiptEpoch;
    std::map<std::string, BATCH_RECEIPT> m_batchReceipts;
    size_t m_batchReceiptBytes = 0;
};


#endif //KICAD_API_HANDLER_SCH_H
