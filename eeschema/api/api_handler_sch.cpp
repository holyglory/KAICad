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

#include <api/api_handler_sch.h>
#include <sch_file_versions.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <cmath>
#include <limits>
#include <google/protobuf/util/message_differencer.h>
#include <api/api_enums.h>
#include <api/api_sch_utils.h>
#include <api/api_sch_symbol_definition.h>
#include <api/api_utils.h>
#include <api/cross_probe_client.h>
#include <api/sch_context.h>
#include <api/sch_text_presentation.h>
#include <fmt.h>
#include <fmt/ranges.h>
#include <wx/log.h>
#include <magic_enum.hpp>
#include <base_screen.h>
#include <jobs/job_export_bom.h>
#include <jobs/job_export_sch_netlist.h>
#include <jobs/job_export_sch_plot.h>
#include <kiway.h>
#include <sch_field.h>
#include <sch_group.h>
#include <font/font.h>
#include <geometry/shape_compound.h>
#include <common.h>
#include <connection_graph.h>
#include <sch_netchain.h>
#include <sch_commit.h>
#include <api/api_sch_formatting.h>
#include <api/api_sch_annotation.h>
#include <api/api_sch_erc_settings.h>
#include <api/api_sch_field_text_modes.h>
#include <sch_symbol_cache_state.h>
#include <sch_root_instance.h>
#include <richio.h>
#include <sch_edit_frame.h>
#include <sch_label.h>
#include <sch_line.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <sch_sheet_pin.h>
#include <sch_table.h>
#include <sch_tablecell.h>
#include <sch_symbol.h>
#include <schematic.h>
#include <tool/actions.h>
#include <tool/tool_manager.h>
#include <tools/sch_actions.h>
#include <tools/sch_selection_tool.h>
#include <tools/sch_move_tool.h>
#include <limits>
#include <project.h>
#include <bus_alias.h>
#include <embedded_files.h>
#include <wildcards_and_files_ext.h>
#include <wx/filename.h>
#include <wx/mstream.h>
#include <sch_draw_panel.h>
#include <drawing_sheet/ds_proxy_undo_item.h>
#include <drawing_sheet/ds_data_model.h>
#include <undo_redo_container.h>

#include <api/common/types/base_types.pb.h>
#include <trace_helpers.h>

using namespace kiapi::common::commands;
using kiapi::common::types::CommandStatus;
using kiapi::common::types::DocumentType;
using kiapi::common::types::ItemRequestStatus;


std::set<KICAD_T> API_HANDLER_SCH::s_allowedTypes = {
    SCH_JUNCTION_T,
    SCH_NO_CONNECT_T,
    SCH_BUS_WIRE_ENTRY_T,
    SCH_BUS_BUS_ENTRY_T,
    SCH_LINE_T,
    SCH_SHAPE_T,
    SCH_RULE_AREA_T,
    SCH_BITMAP_T,
    SCH_TEXTBOX_T,
    SCH_TEXT_T,
    SCH_TABLE_T,
    SCH_LABEL_T,
    SCH_GLOBAL_LABEL_T,
    SCH_GROUP_T,
    SCH_HIER_LABEL_T,
    SCH_DIRECTIVE_LABEL_T,
    SCH_SYMBOL_T,
    SCH_SHEET_T,
};


HANDLER_RESULT<types::RunJobResponse> ExecuteSchematicJob( KIWAY* aKiway, JOB& aJob )
{
    types::RunJobResponse response;
    WX_STRING_REPORTER reporter;
    int exitCode = aKiway->ProcessJob( KIWAY::FACE_SCH, &aJob, &reporter );

    for( const JOB_OUTPUT& output : aJob.GetOutputs() )
        response.add_output_path( output.m_outputPath.ToUTF8() );

    if( exitCode == 0 )
    {
        response.set_status( types::JobStatus::JS_SUCCESS );
        return response;
    }

    response.set_status( types::JobStatus::JS_ERROR );
    response.set_message( fmt::format( "Schematic export job '{}' failed with exit code {}: {}",
                                       aJob.GetType(), exitCode,
                                       reporter.GetMessages().ToStdString() ) );
    return response;
}


API_HANDLER_SCH::API_HANDLER_SCH( SCH_EDIT_FRAME* aFrame ) :
        API_HANDLER_SCH( CreateSchFrameContext( aFrame ), aFrame )
{
}


API_HANDLER_SCH::API_HANDLER_SCH( std::shared_ptr<SCH_CONTEXT> aContext,
                                  SCH_EDIT_FRAME* aFrame ) :
        API_HANDLER_EDITOR( aFrame ),
        m_frame( aFrame ),
        m_context( std::move( aContext ) )
{
    using namespace kiapi::schematic::jobs;
    using namespace kiapi::schematic::types;
    using namespace kiapi::schematic::commands;

    registerHandler<kiapi::automation::v1::ReadSchematicMetadata, kiapi::automation::v1::SchematicMetadataSnapshot>(
            &API_HANDLER_SCH::handleReadMetadata );
    registerHandler<kiapi::automation::v1::ReadSchematicSaveState, kiapi::automation::v1::SchematicSaveState>(
            &API_HANDLER_SCH::handleReadSaveState );
    registerHandler<kiapi::automation::v1::ReadSchematicScreenData, kiapi::automation::v1::SchematicScreenDataSnapshot>(
            &API_HANDLER_SCH::handleReadScreenData );
    registerHandler<kiapi::automation::v1::ReadSchematicHierarchyData, kiapi::automation::v1::SchematicHierarchyDataSnapshot>(
            &API_HANDLER_SCH::handleReadHierarchyData );
    registerHandler<kiapi::automation::v1::ReadSchematicElectricalState, kiapi::automation::v1::SchematicElectricalState>(
            &API_HANDLER_SCH::handleReadElectricalState );
    registerHandler<kiapi::automation::v1::CaptureSchematicObservation, kiapi::automation::v1::SchematicObservation>(
            &API_HANDLER_SCH::handleCaptureObservation );
    registerHandler<GetOpenDocuments, GetOpenDocumentsResponse>(
            &API_HANDLER_SCH::handleGetOpenDocuments );
    registerHandler<kiapi::automation::v1::ApplySchematicItemBatch, kiapi::automation::v1::SchematicItemBatchResult>(
            &API_HANDLER_SCH::handleApplyItemBatch );
    registerHandler<kiapi::automation::v1::InspectSchematicOperation, kiapi::automation::v1::SchematicOperationReceipt>(
            &API_HANDLER_SCH::handleInspectOperation );
    registerHandler<kiapi::automation::v1::CaptureSchematicPreview, kiapi::automation::v1::SchematicPreview>(
            &API_HANDLER_SCH::handleCapturePreview );
    registerHandler<kiapi::automation::v1::RenderSchematicViews, kiapi::automation::v1::SchematicViewSet>(
            &API_HANDLER_SCH::handleRenderViews );
    registerHandler<kiapi::automation::v1::ActivateSchematicSheet, types::DocumentSpecifier>(
            &API_HANDLER_SCH::handleActivateSheet );
    registerHandler<kiapi::automation::v1::ReadSchematicChangeJournal, kiapi::automation::v1::SchematicChangeJournal>(
            &API_HANDLER_SCH::handleReadChangeJournal );
    registerHandler<SaveDocument, google::protobuf::Empty>(
            &API_HANDLER_SCH::handleSaveDocument );
    registerHandler<SaveCopyOfDocument, google::protobuf::Empty>(
            &API_HANDLER_SCH::handleSaveCopyOfDocument );
    registerHandler<RevertDocument, google::protobuf::Empty>( &API_HANDLER_SCH::handleRevertDocument );

    registerHandler<GetItems, GetItemsResponse>( &API_HANDLER_SCH::handleGetItems );
    registerHandler<GetItemsById, GetItemsResponse>( &API_HANDLER_SCH::handleGetItemsById );
    registerHandler<GetBoundingBox, GetBoundingBoxResponse>( &API_HANDLER_SCH::handleGetBoundingBox );
    registerHandler<kiapi::automation::v1::ReadSchematicPresentationFacts, kiapi::automation::v1::SchematicPresentationFacts>(
            &API_HANDLER_SCH::handleReadPresentationFacts );

    registerHandler<GetSelection, SelectionResponse>( &API_HANDLER_SCH::handleGetSelection );
    registerHandler<ClearSelection, Empty>( &API_HANDLER_SCH::handleClearSelection );
    registerHandler<AddToSelection, SelectionResponse>( &API_HANDLER_SCH::handleAddToSelection );
    registerHandler<RemoveFromSelection, SelectionResponse>(
            &API_HANDLER_SCH::handleRemoveFromSelection );

    registerHandler<RunSchematicJobExportSvg, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportSvg );
    registerHandler<RunSchematicJobExportDxf, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportDxf );
    registerHandler<RunSchematicJobExportPdf, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportPdf );
    registerHandler<RunSchematicJobExportPs, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportPs );
    registerHandler<RunSchematicJobExportNetlist, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportNetlist );
    registerHandler<RunSchematicJobExportBOM, types::RunJobResponse>(
            &API_HANDLER_SCH::handleRunSchematicJobExportBOM );
    registerHandler<GetSchematicHierarchy, SchematicHierarchyResponse>( &API_HANDLER_SCH::handleGetSchematicHierarchy );
    registerHandler<GetPageSettings, types::PageSettings>( &API_HANDLER_SCH::handleGetPageSettings );
    registerHandler<SetPageSettings, types::PageSettings>( &API_HANDLER_SCH::handleSetPageSettings );
    registerHandler<GetSchematicNetlist, SchematicNetlistResponse>( &API_HANDLER_SCH::handleGetSchematicNetlist );
    registerHandler<CrossProbeAnnounce, CrossProbeAnnounceResponse>( &API_HANDLER_SCH::handleCrossProbeAnnounce );
    registerHandler<SyncSelection, SyncSelectionResponse>( &API_HANDLER_SCH::handleSyncSelection );
    registerHandler<HighlightNets, HighlightNetsResponse>( &API_HANDLER_SCH::handleHighlightNets );
    registerHandler<GetSchematicVariants, SchematicVariantsResponse>(
            &API_HANDLER_SCH::handleGetSchematicVariants );
    registerHandler<ExpandTextVariables, ExpandTextVariablesResponse>(
            &API_HANDLER_SCH::handleExpandTextVariables );
}


std::unique_ptr<COMMIT> API_HANDLER_SCH::createCommit()
{
    if( m_frame )
        return std::make_unique<SCH_COMMIT>( m_frame );

    return std::make_unique<SCH_COMMIT>( toolManager() );
}


SCHEMATIC* API_HANDLER_SCH::schematic() const
{
    wxCHECK( m_context, nullptr );
    return m_context->GetSchematic();
}


std::optional<ApiResponseStatus> API_HANDLER_SCH::checkForHeadless( const std::string& aCommandName ) const
{
    if( m_frame )
        return std::nullopt;

    ApiResponseStatus e;
    e.set_status( ApiStatusCode::AS_UNIMPLEMENTED );
    e.set_error_message( fmt::format( "{} is not available in headless mode", aCommandName ) );
    return e;
}


bool API_HANDLER_SCH::packSchItem( google::protobuf::Any& aOut, SCH_ITEM* aItem,
                                   const SCH_SHEET_PATH& aPath )
{
    if( aItem->Type() == SCH_SYMBOL_T )
    {
        kiapi::schematic::types::SchematicSymbolInstance symbol;

        if( !PackSymbol( &symbol, static_cast<SCH_SYMBOL*>( aItem ), aPath ) )
            return false;

        aOut.PackFrom( symbol );
    }
    else if( aItem->Type() == SCH_SHEET_T )
    {
        kiapi::schematic::types::SheetSymbol sheet;

        if( !PackSheet( &sheet, static_cast<SCH_SHEET*>( aItem ), aPath ) )
            return false;

        aOut.PackFrom( sheet );
    }
    else
    {
        aItem->Serialize( aOut );
    }

    return true;
}


std::optional<SCH_ITEM*> API_HANDLER_SCH::getItemById( const KIID& aId, SCH_SHEET_PATH* aPathOut ) const
{
    if( !schematic()->HasHierarchy() )
        schematic()->RefreshHierarchy();

    SCH_ITEM* item = schematic()->ResolveItem( aId, aPathOut, true );

    if( !item )
        return std::nullopt;

    return item;
}


tl::expected<bool, ApiResponseStatus>
API_HANDLER_SCH::validateDocumentInternal( const DocumentSpecifier& aDocument ) const
{
    if( aDocument.type() != DocumentType::DOCTYPE_SCHEMATIC )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "the requested document is not a schematic" );
        return tl::unexpected( e );
    }

    const PROJECT& prj = m_context->Prj();

    if( aDocument.project().name().compare( prj.GetProjectName().ToUTF8() ) != 0 )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "the requested project {} is not open",
                                          aDocument.project().name() ) );
        return tl::unexpected( e );
    }

    if( aDocument.project().path().compare( prj.GetProjectPath().ToUTF8() ) != 0 )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "the requested project {} is not open at path {}",
                                          aDocument.project().name(),
                                          aDocument.project().path() ) );
        return tl::unexpected( e );
    }

    if( aDocument.has_sheet_path() )
    {
        KIID_PATH path = UnpackSheetPath( aDocument.sheet_path() );

        if( !resolveBatchSheet( path ) )
        {
            ApiResponseStatus e;
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( fmt::format( "the requested sheet path {} is not valid for this schematic",
                                              path.AsString().ToStdString() ) );
            return tl::unexpected( e );
        }
    }

    return true;
}

std::optional<SCH_SHEET_PATH> API_HANDLER_SCH::resolveBatchSheet( const KIID_PATH& aPath ) const
{
    if( auto loaded = schematic()->Hierarchy().GetSheetPathByKIIDPath( aPath ) )
        return loaded;
    if( !m_atomicBatchActive || !m_atomicCreatedItems || aPath.empty() )
        return std::nullopt;
    SCH_SHEET_PATH path;
    // Public paths omit the virtual root. Start from an exact loaded prefix,
    // including the correct top-level sheet in multi-root schematics.
    for( const auto& loaded : schematic()->Hierarchy() )
    {
        const KIID_PATH prefix = loaded.Path();
        if( prefix.size() > path.size() && prefix.size() <= aPath.size()
                && std::equal( prefix.begin(), prefix.end(), aPath.begin() ) )
            path = loaded;
    }
    if( path.empty() )
        return std::nullopt;
    for( size_t i = path.size(); i < aPath.size(); ++i )
    {
        auto staged = m_atomicCreatedItems->find( { path.LastScreen(), aPath[i] } );
        SCH_ITEM* item = staged == m_atomicCreatedItems->end()
                ? path.ResolveItem( aPath[i] ) : staged->second.get();
        if( !item || item->Type() != SCH_SHEET_T )
            return std::nullopt;
        auto* sheet = static_cast<SCH_SHEET*>( item );
        if( !sheet->GetScreen() )
            return std::nullopt;
        path.push_back( sheet );
    }
    return path;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicPresentationFacts> API_HANDLER_SCH::handleReadPresentationFacts(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicPresentationFacts>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    using Fact = kiapi::automation::v1::SchematicPresentationObject;
    kiapi::automation::v1::SchematicPresentationFacts result;
    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
    result.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
    const auto sheetPath = m_context->GetCurrentSheet();
    auto* screen = sheetPath->LastScreen();
    const auto& page = screen->GetPageSettings();
    BOX2I pageBounds( VECTOR2I( 0, 0 ), VECTOR2I( page.GetWidthIU( schIUScale.IU_PER_MILS ),
                                               page.GetHeightIU( schIUScale.IU_PER_MILS ) ) );
    PackBox2( *result.mutable_page_bounds(), pageBounds, schIUScale );
    result.set_coverage_complete( false );
    result.add_limitations( "Current-sheet native geometry only; independent sheet render contexts are unfinished" );
    result.add_limitations( "Full glyph clipping/occlusion and complete revision tracking are unfinished" );

    auto add = [&]( SCH_ITEM* item, const KIID* owner )
    {
        // Groups have no separate painted body; children are represented by
        // their own screen entries. ERC markers are not persisted design content.
        if( item->Type() == SCH_GROUP_T || item->Type() == SCH_MARKER_T )
            return;

        auto* fact = result.add_objects();
        fact->mutable_id()->set_value( item->m_Uuid.AsStdString() );
        if( owner )
            fact->mutable_owner_id()->set_value( owner->AsStdString() );
        fact->set_kind( item->Type() == SCH_BITMAP_T ? Fact::IMAGE : Fact::GRAPHIC );
        fact->set_visible( true );
        BOX2I bounds = item->GetBoundingBox();

        if( item->Type() == SCH_SYMBOL_T )
            bounds = static_cast<SCH_SYMBOL*>( item )->GetBodyAndPinsBoundingBox();
        else if( item->Type() == SCH_SHEET_T )
            bounds = static_cast<SCH_SHEET*>( item )->GetBodyBoundingBox();

        if( auto* text = dynamic_cast<EDA_TEXT*>( item ) )
        {
            fact->set_kind( Fact::TEXT );
            fact->set_visible( text->IsVisible() );
            fact->set_text_height_nm( schIUScale.IUToNm( text->GetTextHeight() ) );
            fact->set_text( text->GetShownText( true ).ToStdString() );
            // Use the painter's placement and stroke width, not just the
            // unshifted font shape. Field/container semantics remain separate.
            if( item->Type() == SCH_TEXT_T && m_frame && m_frame->GetCanvas() )
            {
                const auto* settings = static_cast<const SCH_RENDER_SETTINGS*>(
                        m_frame->GetCanvas()->GetView()->GetPainter()->GetSettings() );
                if( auto painted = SchTextPresentationBounds(
                            *static_cast<SCH_TEXT*>( item ), *settings ) )
                    bounds = *painted;
            }
        }

        if( auto* field = dynamic_cast<SCH_FIELD*>( item ) )
        {
            fact->set_field_name( field->GetName().ToStdString() );
            if( owner )
                fact->mutable_owner_id()->set_value( owner->AsStdString() );
            if( field->GetId() == FIELD_T::REFERENCE )
                fact->set_kind( Fact::REFERENCE_DESIGNATOR );
        }

        // The painter skips cells covered by a merged cell. Their stored text
        // must not produce font or overflow findings for invisible content.
        if( auto* cell = dynamic_cast<SCH_TABLECELL*>( item ) )
            fact->set_visible( fact->visible() && cell->GetColSpan() > 0 && cell->GetRowSpan() > 0 );

        if( auto* textBox = dynamic_cast<SCH_TEXTBOX*>( item ) )
        {
            // The rectangle is not a text clip. Match the painter's draw origin
            // and rotation when transforming the native font-metric bounds.
            BOX2I textBounds = textBox->GetTextBox( nullptr );
            const VECTOR2I origin = textBox->GetDrawPos();
            VECTOR2I corners[] = { textBounds.GetOrigin(),
                                   VECTOR2I( textBounds.GetRight(), textBounds.GetTop() ),
                                   textBounds.GetEnd(),
                                   VECTOR2I( textBounds.GetLeft(), textBounds.GetBottom() ) };
            for( auto& corner : corners )
                RotatePoint( corner, origin, textBox->GetDrawRotation() );
            BOX2I rotated( corners[0], VECTOR2I( 0, 0 ) );
            for( const auto& corner : corners )
                rotated.Merge( corner );
            PackBox2( *fact->mutable_text_bounds(), rotated, schIUScale );
        }

        PackBox2( *fact->mutable_bounds(), bounds, schIUScale );
    };

    for( SCH_ITEM* item : screen->Items() )
    {
        add( item, nullptr );
        if( auto* line = dynamic_cast<SCH_LINE*>( item ); line && line->GetLayer() == LAYER_WIRE )
        {
            const SCH_CONNECTION* connection = line->Connection( &*sheetPath );
            if( connection && connection->NetCode() > 0 && !line->IsConnectivityDirty() )
            {
                auto* wire = result.add_wires();
                wire->mutable_id()->set_value( line->m_Uuid.AsStdString() );
                PackVector2( *wire->mutable_start(), line->GetStartPoint(), schIUScale );
                PackVector2( *wire->mutable_end(), line->GetEndPoint(), schIUScale );
                wire->set_signal_key( "native-net-code:" + std::to_string( connection->NetCode() ) );
            }
            else
                result.add_limitations( "A wire lacks a current native signal binding: " + line->m_Uuid.AsStdString() );
        }
        if( item->Type() == SCH_JUNCTION_T )
            PackVector2( *result.add_junctions(), item->GetPosition(), schIUScale );
        if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( item ) )
        {
            for( SCH_FIELD& field : symbol->GetFields() )
                add( &field, &item->m_Uuid );
        }
        else if( auto* sheet = dynamic_cast<SCH_SHEET*>( item ) )
        {
            for( SCH_FIELD& field : sheet->GetFields() )
                add( &field, &item->m_Uuid );
            for( SCH_SHEET_PIN* pin : sheet->GetPins() )
                add( pin, &item->m_Uuid );
        }
        else if( auto* table = dynamic_cast<SCH_TABLE*>( item ) )
        {
            for( SCH_TABLECELL* cell : table->GetCells() )
                add( cell, &item->m_Uuid );
        }
        else if( auto* label = dynamic_cast<SCH_LABEL_BASE*>( item ) )
        {
            for( SCH_FIELD& field : label->GetFields() )
                add( &field, &item->m_Uuid );
        }
    }

    return result;
}


HANDLER_RESULT<GetBoundingBoxResponse> API_HANDLER_SCH::handleGetBoundingBox(
        const HANDLER_CONTEXT<GetBoundingBox>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    // Native symbol text/variant bounds currently use CurrentSheet(). Never
    // pretend an offscreen repeated instance has those same rendered bounds.
    if( auto valid = validateDisplayedSheet( aCtx.Request.header().document() ); !valid )
        return tl::unexpected( valid.error() );

    auto invalid = []( const std::string& message ) -> HANDLER_RESULT<GetBoundingBoxResponse>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };

    if( aCtx.Request.mode() != BoundingBoxMode::BBM_ITEM_ONLY
            && aCtx.Request.mode() != BoundingBoxMode::BBM_ITEM_AND_CHILD_TEXT )
        return invalid( "An explicit bounding-box mode is required" );

    GetBoundingBoxResponse response;
    const bool includeText = aCtx.Request.mode() == BoundingBoxMode::BBM_ITEM_AND_CHILD_TEXT;

    for( const auto& requested : aCtx.Request.items() )
    {
        SCH_ITEM* item = m_context->GetCurrentSheet()->ResolveItem( KIID( requested.value() ) );

        if( !item )
            return invalid( "A requested object does not exist in this sheet" );

        BOX2I bounds = item->GetBoundingBox();

        if( !includeText && item->Type() == SCH_SYMBOL_T )
            bounds = static_cast<SCH_SYMBOL*>( item )->GetBodyAndPinsBoundingBox();
        else if( item->Type() == SCH_SHEET_T )
        {
            auto* sheet = static_cast<SCH_SHEET*>( item );
            bounds = sheet->GetBodyBoundingBox();

            if( includeText )
            {
                for( const SCH_FIELD& field : sheet->GetFields() )
                {
                    if( field.IsVisible() )
                        bounds.Merge( field.GetBoundingBox() );
                }
            }
        }

        response.add_items()->CopyFrom( requested );
        PackBox2( *response.add_boxes(), bounds, schIUScale );
    }

    return response;
}


// Shared admission for independent page edits and atomic model-driven batches.
// Preparing dimensions must not change process-wide custom-page defaults.
static HANDLER_RESULT<PAGE_INFO> PreparePageGeometry( const types::PageSettings& request,
                                                     PAGE_INFO proposed )
{
    auto invalid = []( const std::string& message ) -> HANDLER_RESULT<PAGE_INFO>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };
    if( !types::PageSize_IsValid( request.page_size() ) || request.page_size() == types::PS_UNKNOWN
            || ( request.orientation() != types::PO_LANDSCAPE && request.orientation() != types::PO_PORTRAIT ) )
        return invalid( "A supported page size and explicit orientation are required" );
    if( request.drawing_sheet().find( '\0' ) != std::string::npos )
        return invalid( "Drawing-sheet name cannot contain a NUL character" );
    if( request.page_size() == types::PS_USER )
    {
        if( !request.has_user_page_size() )
            return invalid( "Custom page dimensions are required in nanometres" );
        const int64_t width = request.user_page_size().x_nm();
        const int64_t height = request.user_page_size().y_nm();
        constexpr int64_t minimum = int64_t( MIN_PAGE_SIZE_MILS ) * 25400;
        constexpr int64_t maximum = int64_t( MAX_PAGE_SIZE_EESCHEMA_MILS ) * 25400;
        if( width < minimum || height < minimum || width > maximum || height > maximum )
            return invalid( "Custom page dimensions are outside the native schematic limits" );
        if( ( height > width ) != ( request.orientation() == types::PO_PORTRAIT ) )
            return invalid( "Custom dimensions and orientation contradict each other" );
        proposed.SetType( PAGE_SIZE_TYPE::User );
        proposed.SetWidthMils( double( width ) / 25400.0 );
        proposed.SetHeightMils( double( height ) / 25400.0 );
    }
    else
    {
        if( request.has_user_page_size() )
            return invalid( "Custom dimensions cannot accompany a standard page size" );
        proposed.SetType( FromProtoEnum<PAGE_SIZE_TYPE>( request.page_size() ),
                          request.orientation() == types::PO_PORTRAIT );
    }
    return proposed;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicItemBatchResult> API_HANDLER_SCH::handleApplyItemBatch(
        const HANDLER_CONTEXT<kiapi::automation::v1::ApplySchematicItemBatch>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    if( !aCtx.Request.document().has_sheet_path() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "An atomic batch requires an explicit sheet-instance path" );
        return tl::unexpected( error );
    }

    auto targetSheet = schematic()->Hierarchy().GetSheetPathByKIIDPath(
            UnpackSheetPath( aCtx.Request.document().sheet_path() ) );

    if( aCtx.Request.operations().empty() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "An atomic batch requires at least one operation" );
        return tl::unexpected( error );
    }

    auto invalidRetry = []( const std::string& message )
            -> HANDLER_RESULT<kiapi::automation::v1::SchematicItemBatchResult>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };

    const std::string& operationId = aCtx.Request.operation_id();
    const std::string& epoch = schematic()->ChangeJournal().Epoch();
    BATCH_RECEIPT* receipt = nullptr;

    if( !aCtx.Request.origin_id().empty()
            && ( aCtx.Request.origin_id().size() > 128 || aCtx.Request.origin_id().find( '\0' ) != std::string::npos
                 || operationId.empty() || !aCtx.Request.has_expected_revision() ) )
        return invalidRetry( "Synchronization origin requires a bounded non-NUL identity, operation ID and expected revision" );

    if( operationId.empty() != aCtx.Request.document_epoch().empty() )
        return invalidRetry( "Operation ID and document epoch must be supplied together" );

    if( !operationId.empty() )
    {
        if( aCtx.Request.document_epoch() != epoch )
            return invalidRetry( "Operation belongs to a different document epoch; reobserve the document" );

        if( operationId.size() > 128 )
            return invalidRetry( "Operation ID exceeds 128 bytes" );

        if( m_batchReceiptEpoch != epoch )
        {
            m_batchReceipts.clear();
            m_batchReceiptBytes = 0;
            m_batchReceiptEpoch = epoch;
        }

        auto existing = m_batchReceipts.find( operationId );

        if( existing != m_batchReceipts.end() )
        {
            // Protobuf map serialization order is not request identity. Parse
            // the retained request and compare fields (including map contents),
            // so a timeout retry cannot spuriously become a conflicting edit.
            kiapi::automation::v1::ApplySchematicItemBatch original;
            if( !original.ParseFromString( existing->second.request ) )
                return invalidRetry( "Operation receipt is unreadable; inspect the document before further edits" );
            if( !google::protobuf::util::MessageDifferencer::Equals( original, aCtx.Request ) )
                return invalidRetry( "Operation ID was already used for a different request" );

            if( !existing->second.completed )
                return invalidRetry( "Operation result is indeterminate; inspect the document before further edits" );

            if( existing->second.failure )
                return tl::unexpected( *existing->second.failure );

            return existing->second.result;
        }

    }

    // Replay is checked before admission: an already completed operation may
    // legitimately carry an older expected revision, but must never run again.
    if( aCtx.Request.has_expected_revision() )
    {
        const auto& expected = aCtx.Request.expected_revision();
        const auto& journal = schematic()->ChangeJournal();
        if( expected.epoch() != journal.Epoch() || expected.sequence() != journal.Sequence() )
            return invalidRetry( "Stale document revision; no edit was applied. Obtain a fresh observation" );
    }

    if( !operationId.empty() )
    {
        const std::string request = aCtx.Request.SerializeAsString();
        // Never evict successful identities: eviction would allow an old
        // timeout retry to apply twice. Admission stops before any mutation.
        constexpr size_t receiptBudget = 16 * 1024 * 1024;
        if( m_batchReceipts.size() >= 4096 || request.size() > receiptBudget - m_batchReceiptBytes )
            return invalidRetry( "Document retry receipt capacity exhausted; no edit was applied" );

        auto inserted = m_batchReceipts.emplace( operationId, BATCH_RECEIPT{ request, {}, false, {} } );
        m_batchReceiptBytes += request.size();
        receipt = &inserted.first->second;
    }

    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetAutomationOrigin(
            aCtx.Request.origin_id(), operationId );
    m_activeClients.insert( aCtx.ClientName );
    STAGED_ITEMS createdItems;
    m_atomicCreatedItems = &createdItems;
    m_atomicBatchActive = true;
    types::ItemHeader header;
    header.mutable_document()->CopyFrom( aCtx.Request.document() );
    kiapi::automation::v1::SchematicItemBatchResult result;

    std::optional<std::vector<std::pair<KIID, int>>> savedSelection;
    std::optional<VECTOR2I> savedReference;
    bool savedHover = false;
    auto restoreSelection = [&]()
    {
        if( !savedSelection || !m_frame )
            return;
        auto* selectionTool = toolManager()->GetTool<SCH_SELECTION_TOOL>();
        selectionTool->ClearSelection( true );
        auto restoreItem = [&]( SCH_ITEM* item )
        {
            for( const auto& [id, flags] : *savedSelection )
            {
                if( item->m_Uuid == id )
                {
                    selectionTool->AddItemToSel( item, true );
                    item->ClearFlags( STARTPOINT | ENDPOINT );
                    item->SetFlags( flags );
                    break;
                }
            }
        };
        for( SCH_ITEM* item : m_frame->GetScreen()->Items() )
        {
            restoreItem( item );
            item->RunOnChildren( restoreItem, RECURSE_MODE::RECURSE );
        }
        auto& selection = selectionTool->GetSelection();
        selection.SetIsHover( savedHover );
        if( savedReference )
            selection.SetReferencePoint( *savedReference );
        else
            selection.ClearReferencePoint();
    };

    auto rollback = [&]()
    {
        // These groups have never become screen-owned. Existing member undo
        // images intentionally do not carry group pointers, so detach before
        // Revert restores them and before createdItems destroys the groups.
        for( auto& [id, item] : createdItems )
        {
            if( item->Type() == SCH_GROUP_T )
                static_cast<SCH_GROUP*>( item.get() )->RemoveAll();
        }
        auto pending = m_commits.find( aCtx.ClientName );

        if( pending != m_commits.end() )
        {
            pending->second.second->Revert();
            m_commits.erase( pending );
        }

        m_activeClients.erase( aCtx.ClientName );
        m_atomicBatchActive = false;
        m_atomicCreatedItems = nullptr;
        m_atomicTargetPath = nullptr;
        restoreSelection();
    };

    auto reject = [&]( const std::string& message ) -> HANDLER_RESULT<kiapi::automation::v1::SchematicItemBatchResult>
    {
        rollback();
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        if( receipt )
        {
            receipt->failure = error;
            receipt->completed = true;
        }
        return tl::unexpected( error );
    };

    try
    {
        // Decode all cache replacements before mutating any native object.
        // Screen UUIDs survive sheet reparenting and disambiguate repeated paths.
        std::map<std::string, SCH_SYMBOL_CACHE_STATE> cacheCandidates;
        auto* nativeCommit = static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) );
        for( const auto& operation : aCtx.Request.operations() )
        {
            if( !operation.has_replace_library_cache() )
                continue;
            const auto& state = operation.replace_library_cache();
            const std::string& id = state.screen_id().value();
            auto known = state;
            known.DiscardUnknownFields();
            if( !google::protobuf::util::MessageDifferencer::Equals( known, state ) )
                return reject( "Cache replacement contains unsupported fields" );
            if( id.empty() || KIID( id ) == niluuid || KIID( id ).AsStdString() != id || cacheCandidates.count( id ) )
                return reject( "A cache replacement requires a unique screen UUID" );
            SCH_SYMBOL_CACHE_STATE candidate;
            if( !UnpackCachedSymbols( state.definitions(), candidate ) )
                return reject( "Cache definitions are malformed or unsupported" );
            cacheCandidates.emplace( id, std::move( candidate ) );
        }
        for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() )
        {
            if( auto* screen = path.LastScreen(); screen && cacheCandidates.count( screen->GetUuid().AsStdString() ) )
                nativeCommit->CaptureLibraryCache( *screen );
        }
        int index = 0;

        for( const auto& operation : aCtx.Request.operations() )
        {
            const std::string prefix = fmt::format( "Atomic operation {} rejected: ", index++ );
            const auto& document = operation.has_target_document() ? operation.target_document() : aCtx.Request.document();
            auto owner = document;
            auto batchOwner = aCtx.Request.document();
            owner.clear_sheet_path();
            batchOwner.clear_sheet_path();
            if( !google::protobuf::util::MessageDifferencer::Equals( owner, batchOwner ) )
                return reject( prefix + "Operation target belongs to a different document or project" );
            if( !document.has_sheet_path() )
                return reject( prefix + "Operation target requires an explicit sheet-instance path" );
            if( auto valid = validateDocument( document ); !valid )
                return reject( prefix + valid.error().error_message() );
            targetSheet = resolveBatchSheet( UnpackSheetPath( document.sheet_path() ) );
            if( !targetSheet )
                return reject( prefix + "Operation target is not a loaded or staged sheet instance" );
            m_atomicTargetPath = &*targetSheet;
            header.mutable_document()->CopyFrom( document );
            result.add_operation_targets()->CopyFrom( document );

            if( operation.has_move_connected_symbols() )
            {
                if( !m_frame )
                    return reject( prefix + "Connected movement requires an editor context" );
                if( auto valid = validateDisplayedSheet( document ); !valid )
                    return reject( prefix + valid.error().error_message() );
                if( operationId.empty() || !aCtx.Request.has_expected_revision() )
                    return reject( prefix + "Connected movement requires revision and retry identity" );
                if( !createdItems.empty() )
                    return reject( prefix + "Commit staged creations before connected movement" );

                const auto& move = operation.move_connected_symbols();
                auto known = move;
                known.DiscardUnknownFields();
                if( !google::protobuf::util::MessageDifferencer::Equals( known, move )
                        || move.symbols().empty() || !move.has_delta() )
                    return reject( prefix + "Connected movement requires symbols and a supported displacement" );
                const int64_t x = move.delta().x_nm();
                const int64_t y = move.delta().y_nm();
                // Native schematic coordinates use exactly 100 nm per IU.
                if( x % 100 || y % 100 || x / 100 < std::numeric_limits<int>::min()
                        || x / 100 > std::numeric_limits<int>::max()
                        || y / 100 < std::numeric_limits<int>::min()
                        || y / 100 > std::numeric_limits<int>::max() )
                    return reject( prefix + "Displacement is outside the exact native schematic coordinate range" );
                const VECTOR2I delta( x / 100, y / 100 );
                std::map<KIID, SCH_ITEM*> available;
                std::map<KIID, google::protobuf::Any> before;
                std::set<KIID> locked;
                for( SCH_ITEM* item : targetSheet->LastScreen()->Items() )
                {
                    available.emplace( item->m_Uuid, item );
                    if( !packSchItem( before[item->m_Uuid], item, *targetSheet )
                            || before[item->m_Uuid].type_url().empty() )
                        return reject( prefix + "Connected movement requires serializable screen items" );
                    if( item->IsLocked() )
                        locked.insert( item->m_Uuid );
                    auto bounds = item->GetBoundingBox();
                    for( auto [coordinate, offset] : { std::pair<int64_t, int64_t>{ bounds.GetLeft(), delta.x },
                            { bounds.GetRight(), delta.x }, { bounds.GetTop(), delta.y },
                            { bounds.GetBottom(), delta.y } } )
                    {
                        if( coordinate + offset < std::numeric_limits<int>::min()
                                || coordinate + offset > std::numeric_limits<int>::max() )
                            return reject( prefix + "Displacement would overflow native geometry" );
                    }
                }
                std::set<KIID> targets;
                for( const auto& symbol : move.symbols() )
                {
                    const KIID id( symbol.value() );
                    if( id == niluuid || id.AsStdString() != symbol.value() || !targets.insert( id ).second
                            || !available.count( id ) || available.at( id )->Type() != SCH_SYMBOL_T )
                        return reject( prefix + "Each target must be a distinct symbol UUID on the targeted screen" );
                    if( available.at( id )->IsLocked() )
                        return reject( prefix + "A requested symbol is locked" );
                }
                if( delta == VECTOR2I( 0, 0 ) )
                    continue;
                auto* selectionTool = toolManager()->GetTool<SCH_SELECTION_TOOL>();
                if( !savedSelection )
                {
                    savedSelection.emplace();
                    auto& selection = selectionTool->GetSelection();
                    savedHover = selection.IsHover();
                    if( selection.HasReferencePoint() )
                        savedReference = selection.GetReferencePoint();
                    for( EDA_ITEM* item : selection )
                        savedSelection->emplace_back( item->m_Uuid, item->GetFlags() & ( STARTPOINT | ENDPOINT ) );
                }
                selectionTool->ClearSelection( true );
                for( const KIID& id : targets )
                    selectionTool->AddItemToSel( available.at( id ), true );
                wxString failure;
                if( !toolManager()->GetTool<SCH_MOVE_TOOL>()->DragSelectionBy( nativeCommit, delta, failure ) )
                    return reject( prefix + failure.ToStdString() );

                std::map<KIID, google::protobuf::Any> after;
                for( SCH_ITEM* item : targetSheet->LastScreen()->Items() )
                {
                    if( !packSchItem( after[item->m_Uuid], item, *targetSheet )
                            || after[item->m_Uuid].type_url().empty() )
                        return reject( prefix + "Connected movement produced an unsupported screen item" );
                }
                for( const KIID& id : locked )
                {
                    if( !after.count( id ) || !google::protobuf::util::MessageDifferencer::Equals( before.at( id ), after.at( id ) ) )
                        return reject( prefix + "Connected movement would change a locked item" );
                }
                for( const auto& [id, item] : after )
                {
                    if( !before.count( id ) || !google::protobuf::util::MessageDifferencer::Equals( before.at( id ), item ) )
                        result.add_items()->CopyFrom( item );
                }
                for( const auto& [id, item] : before )
                {
                    if( !after.count( id ) )
                        result.add_removed()->set_value( id.AsStdString() );
                }
            }
            else if( operation.has_create() || operation.has_update() )
            {
                google::protobuf::RepeatedPtrField<google::protobuf::Any> items;
                items.Add()->CopyFrom( operation.has_create() ? operation.create() : operation.update() );
                std::string failure;
                int count = 0;
                auto applied = handleCreateUpdateItemsInternal( operation.has_create(), aCtx.ClientName,
                        header, items, [&]( ItemStatus status, google::protobuf::Any item )
                        {
                            ++count;

                            if( status.code() != ItemStatusCode::ISC_OK )
                                failure = status.error_message().empty() ? "Item validation failed" : status.error_message();
                            else
                                result.add_items()->CopyFrom( item );
                        } );

                if( !applied )
                    return reject( prefix + applied.error().error_message() );

                if( count != 1 || !failure.empty() )
                    return reject( prefix + ( failure.empty() ? "No item result" : failure ) );
            }
            else if( operation.has_remove() )
            {
                KIID id( operation.remove().value() );
                auto staged = createdItems.find( { targetSheet->LastScreen(), id } );

                if( staged != createdItems.end() )
                {
                    if( staged->second->IsLocked() )
                        return reject( prefix + "Removal target is locked" );

                    if( staged->second->Type() == SCH_SHEET_T )
                    {
                        SCH_SCREEN* child = static_cast<SCH_SHEET*>( staged->second.get() )->GetScreen();
                        if( std::any_of( createdItems.begin(), createdItems.end(),
                                [child]( const auto& entry ) { return entry.first.first == child; } ) )
                            return reject( prefix + "Remove staged child contents before cancelling their sheet" );
                    }

                    // Stage(Remove) cancels the deferred Add without taking
                    // ownership; the request then destroys its staged object.
                    getCurrentCommit( aCtx.ClientName )->Remove(
                            staged->second.get(), targetSheet->LastScreen() );
                    createdItems.erase( staged );
                    result.add_removed()->CopyFrom( operation.remove() );
                    continue;
                }

                SCH_ITEM* item = targetSheet->ResolveItem( id );

                if( !item || item->IsLocked()
                        || ( getCurrentCommit( aCtx.ClientName )->GetStatus( item, targetSheet->LastScreen() )
                             & CHT_TYPE ) == CHT_REMOVE )
                    return reject( prefix + "Removal target is absent, on another sheet, or locked" );

                HANDLER_CONTEXT<DeleteItems> removal;
                removal.ClientName = aCtx.ClientName;
                removal.Request.mutable_header()->CopyFrom( header );
                removal.Request.add_item_ids()->CopyFrom( operation.remove() );
                auto removed = handleDeleteItems( removal );

                if( !removed )
                    return reject( prefix + removed.error().error_message() );

                if( removed->deleted_items_size() != 1
                        || removed->deleted_items( 0 ).status() != ItemDeletionStatus::IDS_OK )
                    return reject( prefix + "Removal could not be applied" );

                result.add_removed()->CopyFrom( operation.remove() );
            }
            else if( operation.has_replace_library_cache() )
            {
                SCH_SCREEN* screen = targetSheet->LastScreen();
                const auto& state = operation.replace_library_cache();
                if( state.screen_id().value() != screen->GetUuid().AsStdString() )
                    return reject( prefix + "Cache screen UUID does not match the operation target" );
                auto& candidate = cacheCandidates.at( state.screen_id().value() );
                google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol> before, after;
                if( !PackCachedSymbols( before, screen->GetLibSymbols() )
                        || !PackCachedSymbols( after, candidate.Symbols() ) )
                    return reject( prefix + "Current cache contains unsupported definitions" );
                nativeCommit->CaptureLibraryCache( *screen );
                bool equal = before.size() == after.size();
                for( int i = 0; equal && i < before.size(); ++i )
                    equal = google::protobuf::util::MessageDifferencer::Equals( before.Get( i ), after.Get( i ) );
                if( !equal )
                {
                    nativeCommit->ReplaceLibraryCache( *screen, candidate );
                    result.set_library_cache_changed( true );
                }
            }
            else if( operation.has_set_erc_settings() )
            {
                SCH_ERC_SETTINGS::PREPARED candidate;
                std::string failure;
                if( !SCH_ERC_SETTINGS::Prepare( operation.set_erc_settings(), *schematic(), candidate, failure ) )
                    return reject( prefix + failure );
                const bool changed = SCH_ERC_SETTINGS::Capture( *schematic() ).SerializeAsString()
                                     != candidate.canonical.SerializeAsString();
                if( changed && !nativeCommit->SetErcSettings( candidate, failure ) )
                    return reject( prefix + failure );
                result.set_erc_settings_changed( result.erc_settings_changed() || changed );
            }
            else if( operation.has_replace_embedded_files() )
            {
                const auto& state = operation.replace_embedded_files();
                EMBEDDED_FILES candidate;
                candidate.SetAreFontsEmbedded( state.embedded_fonts() );

                if( auto error = UnpackEmbeddedFiles( state.files(), candidate ) )
                    return reject( prefix + error->ToStdString() );

                auto* current = schematic()->GetEmbeddedFiles();
                STRING_FORMATTER before, after;
                current->WriteEmbeddedFiles( before, true );
                candidate.WriteEmbeddedFiles( after, true );
                if( before.GetString() != after.GetString()
                        || current->GetAreFontsEmbedded() != candidate.GetAreFontsEmbedded() )
                {
                    candidate.UpdateFontFiles();
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->ReplaceEmbeddedFiles( candidate );
                    result.set_embedded_files_changed( true );
                }
            }
            else if( operation.has_set_page_settings() )
            {
                const auto& desired = operation.set_page_settings();
                SCH_SCREEN* screen = targetSheet->LastScreen();
                auto page = PreparePageGeometry( desired, screen->GetPageSettings() );
                if( !page )
                    return reject( prefix + page.error().error_message() );
                STRING_FORMATTER before, after;
                screen->GetPageSettings().Format( &before );
                page->Format( &after );
                wxString name = wxString::FromUTF8( desired.drawing_sheet() );
                if( before.GetString() != after.GetString()
                        || schematic()->Settings().m_SchDrawingSheetFileName != name )
                {
                    DS_DATA_MODEL preparedLayout;
                    wxString loadError, serializedLayout;
                    if( !preparedLayout.LoadFromName( name, project().GetProjectPath(), &project(),
                            { schematic()->GetEmbeddedFiles() }, &loadError ) )
                        return reject( prefix + "Drawing sheet could not be loaded: " + loadError.ToStdString() );
                    preparedLayout.SaveInString( &serializedLayout );
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetPageSettings(
                            screen, *page, name, serializedLayout );
                    result.set_page_settings_changed( true );
                }
            }
            else if( operation.has_replace_net_chains() )
            {
                const auto& desired = operation.replace_net_chains();
                auto known = desired;
                known.DiscardUnknownFields();
                if( known.ByteSizeLong() != desired.ByteSizeLong() )
                    return reject( prefix + "Net chains contain unsupported fields" );
                std::map<wxString, CONNECTION_GRAPH::NET_CHAIN_DEFINITION> definitions;
                const auto current = schematic()->ConnectionGraph()->GetNetChainDefinitions();
                for( const auto& packed : desired.definitions() )
                {
                    wxString name = wxString::FromUTF8( packed.name() );
                    auto noNul = []( const std::string& value ) { return value.find( '\0' ) == std::string::npos; };
                    if( !SCH_NETCHAIN::IsValidName( name ) || !noNul( packed.name() )
                            || packed.name().find_first_of( "\t\r\n" ) != std::string::npos
                            || definitions.count( name ) )
                        return reject( prefix + "Net chain names must be valid and unique" );
                    if( packed.committed() )
                        return reject( prefix + "Committed membership is computed, not writable; clear it in replacement declarations" );
                    if( !noNul( packed.from().reference() ) || !noNul( packed.from().pin() )
                            || !noNul( packed.to().reference() ) || !noNul( packed.to().pin() )
                            || !noNul( packed.net_class() ) )
                        return reject( prefix + "Net chain fields cannot contain NUL" );
                    auto& definition = definitions[name];
                    definition.terminals = { { wxString::FromUTF8( packed.from().reference() ), wxString::FromUTF8( packed.from().pin() ) },
                                             { wxString::FromUTF8( packed.to().reference() ), wxString::FromUTF8( packed.to().pin() ) } };
                    definition.netClass = wxString::FromUTF8( packed.net_class() );
                    if( packed.has_color() )
                    {
                        for( double channel : { packed.color().r(), packed.color().g(), packed.color().b(), packed.color().a() } )
                            if( !std::isfinite( channel ) || channel < 0.0 || channel > 1.0 )
                                return reject( prefix + "Net chain color channels must be finite values from zero to one" );
                        definition.color = UnpackColor( packed.color() );
                    }
                    for( const auto& value : packed.member_nets() )
                    {
                        wxString net = wxString::FromUTF8( value );
                        if( net.IsEmpty() || !noNul( value ) || net.StartsWith( SCH_NETCHAIN::SYNTHETIC_NET_PREFIX )
                                || !definition.memberNets.insert( net ).second )
                            return reject( prefix + "Net chain member nets must be unique nonempty persisted names" );
                    }
                    if( auto prior = current.find( name ); prior != current.end()
                        && ( !prior->second.excludedNets.empty() || !prior->second.excludedPins.empty() ) && !packed.has_exclusions() )
                        return reject( prefix + "Existing removal restrictions require an explicit supported representation" );
                    for( const auto& value : packed.exclusions().net_names() )
                    {
                        wxString net = wxString::FromUTF8( value );
                        if( net.IsEmpty() || !noNul( value ) || net.StartsWith( SCH_NETCHAIN::SYNTHETIC_NET_PREFIX )
                            || definition.memberNets.contains( net ) || !definition.excludedNets.insert( net ).second )
                            return reject( prefix + "Excluded net names must be unique persisted names disjoint from retained members" );
                    }
                    auto validId = []( const std::string& id )
                    { return id.size() == 36 && KIID( id ) != niluuid && KIID( id ).AsStdString() == id; };
                    for( const auto& anchor : packed.exclusions().pins() )
                    {
                        if( !validId( anchor.pin().value() ) || anchor.path().path().empty() )
                            return reject( prefix + "Excluded pins require exact nonempty UUIDs and sheet paths" );
                        KIID_PATH path;
                        for( const auto& id : anchor.path().path() )
                        {
                            if( !validId( id.value() ) ) return reject( prefix + "Excluded pin sheet path contains an invalid UUID" );
                            path.push_back( KIID( id.value() ) );
                        }
                        if( !definition.excludedPins.emplace( path, KIID( anchor.pin().value() ) ).second )
                            return reject( prefix + "Excluded pin identities must be unique" );
                    }
                }
                bool equal = current.size() == definitions.size() && std::equal( current.begin(), current.end(), definitions.begin(),
                    []( const auto& a, const auto& b )
                    {
                        const auto& x = a.second; const auto& y = b.second;
                        return a.first == b.first && x.terminals.first.ref == y.terminals.first.ref
                                && x.terminals.first.pin == y.terminals.first.pin && x.terminals.second.ref == y.terminals.second.ref
                                && x.terminals.second.pin == y.terminals.second.pin && x.netClass == y.netClass
                                && x.color == y.color && x.memberNets == y.memberNets && x.excludedNets == y.excludedNets
                                && x.excludedPins == y.excludedPins;
                    } );
                if( !equal )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetNetChainDefinitions( definitions );
                    result.set_net_chains_changed( true );
                }
            }
            else if( operation.has_replace_net_chain_classes() )
            {
                const auto& desired = operation.replace_net_chain_classes();
                auto known = desired;
                known.DiscardUnknownFields();
                if( known.ByteSizeLong() != desired.ByteSizeLong() )
                    return reject( prefix + "Net chain classes contain unsupported fields" );
                std::set<wxString> definitions;
                auto validText = []( const std::string& text )
                { return !text.empty() && text.find( '\0' ) == std::string::npos; };
                for( const auto& name : desired.definitions() )
                    if( !validText( name ) || !definitions.insert( wxString::FromUTF8( name ) ).second )
                        return reject( prefix + "Class definitions must be unique nonempty names without NUL" );
                std::map<wxString, wxString> assignments;
                for( const auto& [chain, name] : desired.assignments() )
                {
                    wxString className = wxString::FromUTF8( name );
                    if( !validText( chain ) || !validText( name ) || !definitions.contains( className ) )
                        return reject( prefix + "Class assignments require nonempty chain names and declared classes" );
                    assignments.emplace( wxString::FromUTF8( chain ), className );
                }
                auto settings = project().GetProjectFile().NetSettings();
                if( !settings ) return reject( prefix + "Project net settings are unavailable" );
                if( settings->GetNetChainClassDefinitions() != definitions || settings->GetNetChainClasses() != assignments )
                {
                    if( !static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetNetChainClasses( definitions, assignments ) )
                        return reject( prefix + "Cannot stage project class changes" );
                    result.set_net_chain_classes_changed( true );
                }
            }
            else if( operation.has_set_annotation() )
            {
                const auto& desired = operation.set_annotation();
                std::string failure;
                if( !SCH_ANNOTATION::Validate( desired, failure ) )
                    return reject( prefix + failure );
                if( SCH_ANNOTATION::Capture( schematic()->Settings() ).SerializeAsString()
                        != desired.SerializeAsString() )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetAnnotation( desired );
                    result.set_annotation_changed( true );
                }
            }
            else if( operation.has_set_formatting() )
            {
                const auto& desired = operation.set_formatting();
                std::string failure;
                if( !SCH_FORMATTING::Validate( desired, failure ) )
                    return reject( prefix + failure );
                if( SCH_FORMATTING::Capture( schematic()->Settings() ).SerializeAsString()
                        != desired.SerializeAsString() )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetFormatting( desired );
                    result.set_formatting_changed( true );
                }
            }
            else if( operation.has_set_drawing_ratios() )
            {
                const auto& desired = operation.set_drawing_ratios();
                auto known = desired;
                known.DiscardUnknownFields();
                if( known.ByteSizeLong() != desired.ByteSizeLong() )
                    return reject( prefix + "Drawing ratios contain unsupported fields" );
                std::array<double, 5> ratios{ desired.dash_length_ratio(), desired.gap_length_ratio(),
                        desired.text_offset_ratio(), desired.label_size_ratio(), desired.overbar_height_ratio() };
                if( std::any_of( ratios.begin(), ratios.end(), []( double value )
                                { return !std::isfinite( value ) || value < 0; } )
                        || ratios[0] + ratios[1] <= 0 || ratios[2] > 2 || ratios[3] > 2 )
                    return reject( prefix + "Drawing ratios must be finite and nonnegative, dash plus gap positive, text/label ratios at most 2" );
                if( schematic()->Settings().DrawingRatios() != ratios )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetDrawingRatios( ratios );
                    result.set_drawing_ratios_changed( true );
                }
            }
            else if( operation.has_replace_variant_registry() )
            {
                const auto& desired = operation.replace_variant_registry();
                auto known = desired;
                known.DiscardUnknownFields();
                if( known.ByteSizeLong() != desired.ByteSizeLong() )
                    return reject( prefix + "Variant registry contains unsupported fields" );
                std::map<wxString, wxString> descriptions;
                for( const auto& [name, value] : desired.descriptions() )
                {
                    const wxString nativeName = wxString::FromUTF8( name );
                    wxString trimmed = nativeName;
                    trimmed.Trim().Trim( false );
                    if( name.empty() || trimmed != nativeName || name.find( '\0' ) != std::string::npos
                            || value.find( '\0' ) != std::string::npos )
                        return reject( prefix + "Variant names must be nonempty and trimmed; names/descriptions cannot contain NUL" );
                    descriptions.emplace( nativeName, wxString::FromUTF8( value ) );
                }
                if( schematic()->Settings().m_VariantDescriptions != descriptions )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetVariantRegistry( descriptions );
                    result.set_variant_registry_changed( true );
                }
            }
            else if( operation.has_replace_text_variables() )
            {
                const auto& desired = operation.replace_text_variables();
                auto known = desired;
                known.DiscardUnknownFields();
                if( known.ByteSizeLong() != desired.ByteSizeLong() )
                    return reject( prefix + "Text variables contain unsupported fields" );
                std::map<wxString, wxString> variables;
                for( const auto& [name, value] : desired.variables() )
                {
                    if( name.empty() || name.find( '\0' ) != std::string::npos
                            || value.find( '\0' ) != std::string::npos )
                        return reject( prefix + "Text variable names must be nonempty and names/values cannot contain NUL" );
                    variables.emplace( wxString::FromUTF8( name ), wxString::FromUTF8( value ) );
                }
                if( project().GetTextVars() != variables )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetTextVariables( variables );
                    result.set_text_variables_changed( true );
                }
            }
            else if( operation.has_replace_bus_aliases() )
            {
                std::vector<std::shared_ptr<BUS_ALIAS>> aliases;
                std::set<std::string> definitions;
                for( const auto& packed : operation.replace_bus_aliases().aliases() )
                {
                    auto known = packed;
                    known.DiscardUnknownFields();
                    if( known.SerializeAsString() != packed.SerializeAsString() )
                        return reject( prefix + "Bus alias contains unsupported fields" );
                    wxString name = wxString::FromUTF8( packed.name() );
                    if( name.IsEmpty() || name != name.Strip( wxString::both )
                            || packed.name().find( '\0' ) != std::string::npos )
                        return reject( prefix + "Bus alias names must be nonempty, trimmed and contain no NUL" );
                    auto alias = std::make_shared<BUS_ALIAS>();
                    alias->SetName( name );
                    for( const auto& member : packed.members() )
                    {
                        wxString value = wxString::FromUTF8( member );
                        if( value.IsEmpty() || value != value.Strip( wxString::both )
                                || member.find( '\0' ) != std::string::npos )
                            return reject( prefix + "Bus alias members must be nonempty, trimmed and contain no NUL" );
                        alias->AddMember( value );
                    }
                    if( !definitions.insert( packed.name() ).second )
                        return reject( prefix + "Duplicate bus alias names cannot be preserved by project saving" );
                    aliases.push_back( alias );
                }
                const auto& current = schematic()->GetAllBusAliases();
                if( current.size() != aliases.size() || !std::equal( current.begin(), current.end(), aliases.begin(),
                        []( const auto& a, const auto& b ) { return a->GetName() == b->GetName() && a->Members() == b->Members(); } ) )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetBusAliases( aliases );
                    result.set_bus_aliases_changed( true );
                }
            }
            else if( operation.has_set_title_block() )
            {
                const auto& desired = operation.set_title_block();
                for( const std::string* value : { &desired.title(), &desired.date(), &desired.revision(),
                        &desired.company(), &desired.comment1(), &desired.comment2(), &desired.comment3(),
                        &desired.comment4(), &desired.comment5(), &desired.comment6(), &desired.comment7(),
                        &desired.comment8(), &desired.comment9() } )
                {
                    if( value->find( '\0' ) != std::string::npos )
                        return reject( prefix + "Title-block text cannot contain a NUL character" );
                }
                TITLE_BLOCK title;
                title.SetTitle( wxString::FromUTF8( desired.title() ) );
                title.SetDate( wxString::FromUTF8( desired.date() ) );
                title.SetRevision( wxString::FromUTF8( desired.revision() ) );
                title.SetCompany( wxString::FromUTF8( desired.company() ) );
                title.SetComment( 0, wxString::FromUTF8( desired.comment1() ) );
                title.SetComment( 1, wxString::FromUTF8( desired.comment2() ) );
                title.SetComment( 2, wxString::FromUTF8( desired.comment3() ) );
                title.SetComment( 3, wxString::FromUTF8( desired.comment4() ) );
                title.SetComment( 4, wxString::FromUTF8( desired.comment5() ) );
                title.SetComment( 5, wxString::FromUTF8( desired.comment6() ) );
                title.SetComment( 6, wxString::FromUTF8( desired.comment7() ) );
                title.SetComment( 7, wxString::FromUTF8( desired.comment8() ) );
                title.SetComment( 8, wxString::FromUTF8( desired.comment9() ) );
                SCH_SCREEN* screen = targetSheet->LastScreen();
                STRING_FORMATTER before, after;
                screen->GetTitleBlock().Format( &before );
                title.Format( &after );
                if( before.GetString() != after.GetString() )
                {
                    static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetTitleBlock( screen, title );
                    result.set_title_block_changed( true );
                }
            }
            else if( operation.has_set_root_instance() )
            {
                const auto& desired = operation.set_root_instance();
                std::optional<wxString> page;
                if( desired.has_page_number() )
                {
                    page = wxString::FromUTF8( desired.page_number() );
                    if( page->empty() || desired.page_number().find_first_of( " \t\r\n" ) != std::string::npos
                            || desired.page_number().find( '\0' ) != std::string::npos )
                        return reject( prefix + "Root page number must be nonempty and contain no whitespace" );
                }
                for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() )
                {
                    if( path.LastScreen() != targetSheet->LastScreen() )
                        continue;
                    SCH_SHEET* sheet = path.Last();
                    if( sheet->HasRootInstance() != page.has_value()
                            || ( page && sheet->GetRootInstance().m_PageNumber != *page ) )
                    {
                        static_cast<SCH_COMMIT*>( getCurrentCommit( aCtx.ClientName ) )->SetRootInstance( sheet, page );
                        result.set_root_instance_changed( true );
                    }
                }
            }
            else
                return reject( prefix + "Operation kind is missing" );
        }

        wxString cacheFailure;
        if( !nativeCommit->ValidateLibraryCaches( cacheFailure ) )
            return reject( cacheFailure.ToStdString() );

        if( schematic()->ChangeJournal().Sequence() == std::numeric_limits<uint64_t>::max() )
            return reject( "Document revision exhausted; reopen the document before applying edits" );

        // Allocate the epoch and reserve the largest serialized sequence before
        // the commit. Updating the numeric field afterward cannot allocate.
        result.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
        result.mutable_revision()->set_sequence( std::numeric_limits<uint64_t>::max() );
        result.set_tracking_complete( false );

        // Allocate the retained result before the irreversible commit. An
        // exceptional failure keeps the identity indeterminate, never retryable
        // as a new mutation. Result inspection/recovery remains separate work.
        if( receipt )
        {
            const size_t resultBytes = result.ByteSizeLong();
            if( resultBytes > 16 * 1024 * 1024 - m_batchReceiptBytes )
                return reject( "Document retry result capacity exhausted; no edit was applied" );
            receipt->result.CopyFrom( result );
            m_batchReceiptBytes += resultBytes;
        }

        pushCurrentCommit( aCtx.ClientName, wxString::FromUTF8( aCtx.Request.description().empty()
                ? "Atomic schematic edit" : aCtx.Request.description() ) );

        result.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
        if( receipt )
            receipt->result.mutable_revision()->set_sequence( result.revision().sequence() );

        // Successfully pushed additions are now owned by their native screen.
        for( auto& [id, item] : createdItems )
            item.release();

        restoreSelection();
        m_atomicBatchActive = false;
        m_atomicCreatedItems = nullptr;
        m_atomicTargetPath = nullptr;
        if( receipt )
            receipt->completed = true;
        return result;
    }
    catch( ... )
    {
        rollback();
        throw;
    }
}


HANDLER_RESULT<kiapi::automation::v1::SchematicOperationReceipt> API_HANDLER_SCH::handleInspectOperation(
        const HANDLER_CONTEXT<kiapi::automation::v1::InspectSchematicOperation>& aCtx )
{
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto invalid = []( const std::string& message )
            -> HANDLER_RESULT<kiapi::automation::v1::SchematicOperationReceipt>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };

    if( aCtx.Request.document_epoch() != schematic()->ChangeJournal().Epoch() )
        return invalid( "Operation belongs to a different document epoch; reobserve the document" );
    if( aCtx.Request.operation_id().empty() || aCtx.Request.operation_id().size() > 128
            || !aCtx.Request.document().has_sheet_path() )
        return invalid( "An operation ID and explicit sheet-instance path are required" );

    if( aCtx.Request.has_expected_request() )
    {
        const auto& expected = aCtx.Request.expected_request();
        if( expected.operation_id() != aCtx.Request.operation_id()
                || expected.document_epoch() != aCtx.Request.document_epoch()
                || !google::protobuf::util::MessageDifferencer::Equals(
                        expected.document(), aCtx.Request.document() ) )
            return invalid( "Expected operation must identify the same document, epoch and operation ID" );
    }

    using Receipt = kiapi::automation::v1::SchematicOperationReceipt;
    Receipt result;
    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.set_document_epoch( aCtx.Request.document_epoch() );
    result.set_operation_id( aCtx.Request.operation_id() );
    result.set_state( Receipt::NOT_FOUND );
    auto found = m_batchReceipts.find( aCtx.Request.operation_id() );
    if( m_batchReceiptEpoch != aCtx.Request.document_epoch() || found == m_batchReceipts.end() )
        return result;

    const BATCH_RECEIPT& receipt = found->second;
    kiapi::automation::v1::ApplySchematicItemBatch original;
    if( !original.ParseFromString( receipt.request )
            || original.document().SerializeAsString() != aCtx.Request.document().SerializeAsString() )
        return invalid( "Operation ID does not belong to the requested document target" );

    if( aCtx.Request.has_expected_request() )
    {
        if( !google::protobuf::util::MessageDifferencer::Equals(
                    original, aCtx.Request.expected_request() ) )
            return invalid( "Operation receipt belongs to a different saved request" );
        result.set_expected_request_verified( true );
    }

    if( !receipt.completed )
        result.set_state( Receipt::INDETERMINATE );
    else if( receipt.failure )
    {
        result.set_state( Receipt::REJECTED );
        result.set_native_status_code( receipt.failure->status() );
        result.set_failure_message( receipt.failure->error_message() );
    }
    else
    {
        result.set_state( Receipt::COMPLETED );
        result.mutable_result()->CopyFrom( receipt.result );
    }
    return result;
}


std::optional<ApiResponseStatus> API_HANDLER_SCH::validateSnapshotSchema( uint32_t aVersion )
{
    if( aVersion <= 5 ) return std::nullopt;
    ApiResponseStatus error;
    error.set_status( ApiStatusCode::AS_BAD_REQUEST );
    error.set_error_message( "Unsupported schematic snapshot schema version" );
    return error;
}


void API_HANDLER_SCH::projectSnapshotSchema(
        kiapi::schematic::types::SchematicMetadata& aMetadata, uint32_t aVersion )
{
    if( aVersion < 2 && aMetadata.has_erc_settings() )
    {
        aMetadata.clear_erc_settings();
        aMetadata.add_unrepresented_state( "erc_settings_require_snapshot_schema_2" );
    }
    if( aVersion < 3 && aMetadata.has_net_chain_classes() )
    {
        aMetadata.clear_net_chain_classes();
        aMetadata.add_unrepresented_state( "net_chain_classes_require_snapshot_schema_3" );
    }
    if( aVersion < 4 && !aMetadata.net_chains().empty() )
    {
        for( auto& chain : *aMetadata.mutable_net_chains() ) chain.clear_exclusions();
        aMetadata.add_unrepresented_state( "net_chain_exclusions_require_snapshot_schema_4" );
    }
    if( aVersion < 5 && aMetadata.has_annotation() )
    {
        aMetadata.clear_annotation();
        aMetadata.add_unrepresented_state( "annotation_requires_snapshot_schema_5" );
    }
}


HANDLER_RESULT<kiapi::automation::v1::SchematicObservation> API_HANDLER_SCH::handleCaptureObservation(
        const HANDLER_CONTEXT<kiapi::automation::v1::CaptureSchematicObservation>& aCtx )
{
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicScreenData> query;
    query.ClientName = aCtx.ClientName;
    query.Request.mutable_document()->CopyFrom( aCtx.Request.document() );
    // Compare full current state across rendering, even for legacy clients.
    query.Request.set_schema_version( 5 );
    auto before = handleReadScreenData( query );
    if( !before )
        return tl::unexpected( before.error() );

    HANDLER_CONTEXT<kiapi::automation::v1::CaptureSchematicPreview> capture;
    capture.ClientName = aCtx.ClientName;
    capture.Request.mutable_document()->CopyFrom( aCtx.Request.document() );
    // GetScreenshot performs a synchronous repaint. Capture checks the sheet,
    // document cursor and viewport before returning an image.
    auto preview = handleCapturePreview( capture );
    if( !preview )
        return tl::unexpected( preview.error() );
    auto after = handleReadScreenData( query );
    if( !after )
        return tl::unexpected( after.error() );
    // Compare supported content as well as the cursor: revision tracking is
    // not yet complete, and rendering must not return a mixed-state result.
    if( !google::protobuf::util::MessageDifferencer::Equals( *before, *after )
        || after->revision().epoch() != preview->revision().epoch()
        || after->revision().sequence() != preview->revision().sequence() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_NOT_READY );
        error.set_error_message( "Schematic changed during observation; retry" );
        return tl::unexpected( error );
    }
    kiapi::automation::v1::SchematicObservation result;
    result.mutable_snapshot()->Swap( &*after );
    result.mutable_preview()->Swap( &*preview );
    projectSnapshotSchema( *result.mutable_snapshot()->mutable_data()->mutable_metadata(),
                           aCtx.Request.schema_version() );
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicSaveState> API_HANDLER_SCH::handleReadSaveState(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicSaveState>& aCtx )
{
    if( auto busy = checkForStableObservation() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    if( !aCtx.Request.document().has_sheet_path()
            || UnpackSheetPath( aCtx.Request.document().sheet_path() ).empty() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "An explicit loaded sheet instance is required" );
        return tl::unexpected( error );
    }

    kiapi::automation::v1::SchematicSaveState result;
    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
    result.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
    result.set_tracking_complete( false );
    result.set_project_settings_checked( false );
    std::vector<SCH_SHEET_PATH> paths;
    for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() ) paths.push_back( path );
    std::sort( paths.begin(), paths.end(), []( const auto& a, const auto& b ) { return a.Path() < b.Path(); } );
    for( const SCH_SHEET_PATH& path : paths )
    {
        if( !path.LastScreen() || !path.LastScreen()->IsContentModified() ) continue;
        auto* document = result.add_modified_sheet_instances();
        document->CopyFrom( aCtx.Request.document() );
        PackSheetPath( *document->mutable_sheet_path(), path.Path() );
    }
    result.set_unsaved_schematic_changes( result.modified_sheet_instances_size() != 0 );
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicScreenDataSnapshot> API_HANDLER_SCH::handleReadScreenData(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicScreenData>& aCtx )
{
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    if( auto busy = checkForStableObservation() ) return tl::unexpected( *busy );
    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    auto data = readScreenDataForPath( *m_context->GetCurrentSheet(), aCtx.Request.document() );
    if( !data ) return tl::unexpected( data.error() );
    kiapi::automation::v1::SchematicScreenDataSnapshot result;
    result.mutable_data()->CopyFrom( *data );
    projectSnapshotSchema( *result.mutable_data()->mutable_metadata(), aCtx.Request.schema_version() );
    result.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
    result.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
    result.set_tracking_complete( false );
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicHierarchyDataSnapshot> API_HANDLER_SCH::handleReadHierarchyData(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicHierarchyData>& aCtx )
{
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    if( auto busy = checkForStableObservation() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    if( !aCtx.Request.document().has_sheet_path()
        || UnpackSheetPath( aCtx.Request.document().sheet_path() ).empty()
        || !resolveBatchSheet( UnpackSheetPath( aCtx.Request.document().sheet_path() ) ) )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "An explicit loaded schematic sheet instance is required" );
        return tl::unexpected( error );
    }

    // Do not navigate the UI or yield between instances. Every path is packed
    // from the same native state, including repeated-instance presentation.
    std::vector<SCH_SHEET_PATH> paths;
    for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() ) paths.push_back( path );
    std::sort( paths.begin(), paths.end(), []( const auto& a, const auto& b ) { return a.Path() < b.Path(); } );
    kiapi::automation::v1::SchematicHierarchyDataSnapshot result;
    auto* data = result.mutable_data();
    data->mutable_document()->CopyFrom( aCtx.Request.document() );
    if( !paths.empty() ) PackSheetPath( *data->mutable_document()->mutable_sheet_path(), paths.front().Path() );
    for( const SCH_SHEET_PATH& path : paths )
    {
        auto document = aCtx.Request.document();
        PackSheetPath( *document.mutable_sheet_path(), path.Path() );
        auto screen = readScreenDataForPath( path, document );
        if( !screen ) return tl::unexpected( screen.error() );
        projectSnapshotSchema( *screen->mutable_metadata(), aCtx.Request.schema_version() );
        data->add_instances()->CopyFrom( *screen );
    }
    result.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
    result.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
    result.set_tracking_complete( false );
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicElectricalState> API_HANDLER_SCH::handleReadElectricalState(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicElectricalState>& aCtx )
{
    using namespace kiapi::automation::v1;
    auto reject = []( ApiStatusCode code, const std::string& message ) -> HANDLER_RESULT<SchematicElectricalState>
    {
        ApiResponseStatus error; error.set_status( code ); error.set_error_message( message );
        return tl::unexpected( error );
    };
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    HANDLER_CONTEXT<ReadSchematicHierarchyData> query;
    query.ClientName = aCtx.ClientName;
    query.Request.mutable_document()->CopyFrom( aCtx.Request.document() );
    query.Request.set_schema_version( 5 );
    auto before = handleReadHierarchyData( query );
    if( !before ) return tl::unexpected( before.error() );
    if( aCtx.Request.has_expected_revision()
        && !google::protobuf::util::MessageDifferencer::Equals( aCtx.Request.expected_revision(), before->revision() ) )
        return reject( ApiStatusCode::AS_BAD_REQUEST, "Stale schematic electrical observation revision" );
    if( !schematic()->ConnectionGraph() )
        return reject( ApiStatusCode::AS_NOT_READY, "Schematic connectivity is unavailable" );
    const SCH_SHEET_PATH humanPath = *context()->GetCurrentSheet();
    std::map<SCH_SCREEN*, bool> savedFlags;
    for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() )
        savedFlags.emplace( path.LastScreen(), path.LastScreen()->IsContentModified() );

    // Refresh computed connectivity only: no CleanUp, annotation, file save,
    // native commit, or progress callback that can yield to user edits.
    schematic()->ConnectionGraph()->Recalculate( schematic()->Hierarchy(), true );
    HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicNetlist> netQuery;
    netQuery.ClientName = aCtx.ClientName;
    netQuery.Request.mutable_document()->CopyFrom( before->data().document() );
    auto netlist = handleGetSchematicNetlist( netQuery );
    if( !netlist ) return tl::unexpected( netlist.error() );
    auto after = handleReadHierarchyData( query );
    if( !after ) return tl::unexpected( after.error() );
    if( !google::protobuf::util::MessageDifferencer::Equals( *before, *after )
        || *context()->GetCurrentSheet() != humanPath
        || std::ranges::any_of( savedFlags, []( const auto& entry )
               { return entry.first->IsContentModified() != entry.second; } ) )
        return reject( ApiStatusCode::AS_NOT_READY, "Schematic changed during electrical observation; retry" );

    SchematicElectricalState result;
    result.mutable_hierarchy()->Swap( &*after );
    std::vector<std::pair<std::string, kiapi::schematic::types::SchematicNet>> nets;
    for( const auto& source : netlist->nets() )
    {
        kiapi::schematic::types::SchematicNet net;
        net.set_name( source.name() );
        std::map<std::vector<std::string>, std::set<std::string>> memberships;
        for( const auto& sheet : source.sheets() )
        {
            std::vector<std::string> path;
            for( const auto& id : sheet.path().path() ) path.push_back( id.value() );
            for( const auto& id : sheet.items() ) memberships[path].insert( id.value() );
        }
        for( const auto& [path, ids] : memberships )
        {
            auto* sheet = net.add_sheets();
            for( const auto& id : path ) sheet->mutable_path()->add_path()->set_value( id );
            for( const auto& id : ids ) sheet->add_items()->set_value( id );
        }
        nets.emplace_back( net.SerializeAsString(), std::move( net ) );
    }
    std::sort( nets.begin(), nets.end(), []( const auto& a, const auto& b ) { return a.first < b.first; } );
    for( auto& [key, net] : nets ) result.add_nets()->Swap( &net );
    for( auto& screen : *result.mutable_hierarchy()->mutable_data()->mutable_instances() )
        projectSnapshotSchema( *screen.mutable_metadata(), aCtx.Request.schema_version() );
    result.add_limitations( "Net names are computed labels, not persistent net identities" );
    result.add_limitations( "Scalar native net memberships exclude bus containers and unconnected artwork without a pin driver" );
    result.add_limitations( "Complete native serializer and revision coverage remain unfinished; this read is not mutation admission" );
    return result;
}


HANDLER_RESULT<kiapi::schematic::types::SchematicScreenData> API_HANDLER_SCH::readScreenDataForPath(
        const SCH_SHEET_PATH& path, const types::DocumentSpecifier& aDocument )
{
    auto metadata = readMetadataForPath( path, aDocument );
    if( !metadata ) return tl::unexpected( metadata.error() );
    kiapi::schematic::types::SchematicScreenData result;
    auto* data = &result;
    data->mutable_metadata()->CopyFrom( metadata->metadata() );
    for( const auto& [key, library] : path.LastScreen()->GetLibSymbols() )
    {
        kiapi::schematic::types::SchematicCachedSymbol cached;
        if( !library || !PackCachedSymbol( cached, key, *library ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Schematic cache contains an unsupported definition: "
                                     + key.ToStdString() );
            return tl::unexpected( error );
        }
        data->add_cached_symbols()->Swap( &cached );
    }
    std::vector<SCH_ITEM*> items;
    for( SCH_ITEM* item : path.LastScreen()->Items() )
    {
        // ERC markers are computed diagnostics, not saved schematic objects.
        if( item->Type() != SCH_MARKER_T )
            items.push_back( item );
    }
    std::sort( items.begin(), items.end(), []( const SCH_ITEM* a, const SCH_ITEM* b )
               { return a->m_Uuid < b->m_Uuid; } );
    for( SCH_ITEM* item : items )
    {
        google::protobuf::Any packed;
        if( s_allowedTypes.contains( item->Type() ) && packSchItem( packed, item, path )
            && !packed.type_url().empty() )
        {
            data->add_items()->Swap( &packed );
        }
        else
        {
            auto* missing = data->add_unrepresented_items();
            missing->mutable_id()->set_value( item->m_Uuid.AsStdString() );
            missing->set_native_type( static_cast<int>( item->Type() ) );
            missing->set_reason( "No supported native serializer for this screen object" );
        }
    }
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicMetadataSnapshot> API_HANDLER_SCH::handleReadMetadata(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicMetadata>& aCtx )
{
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto result = readMetadataForPath( *m_context->GetCurrentSheet(), aCtx.Request.document() );
    if( !result ) return tl::unexpected( result.error() );
    projectSnapshotSchema( *result->mutable_metadata(), aCtx.Request.schema_version() );
    return result;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicMetadataSnapshot> API_HANDLER_SCH::readMetadataForPath(
        const SCH_SHEET_PATH& aPath, const types::DocumentSpecifier& aDocument )
{
    kiapi::automation::v1::SchematicMetadataSnapshot result;
    auto* metadata = result.mutable_metadata();
    metadata->mutable_document()->CopyFrom( aDocument );
    metadata->mutable_screen_id()->set_value(
            aPath.LastScreen()->GetUuid().AsStdString() );
    metadata->set_loaded_native_format_version(
            std::max( 0, aPath.LastScreen()->GetFileFormatVersionAtLoad() ) );
    metadata->set_writer_native_format_version( SEXPR_SCHEMATIC_FILE_VERSION );
    const PAGE_INFO& page = aPath.LastScreen()->GetPageSettings();
    auto* packedPage = metadata->mutable_page();
    packedPage->set_page_size( ToProtoEnum<PAGE_SIZE_TYPE, types::PageSize>( page.GetType() ) );
    if( page.IsCustom() )
        PackVector2( *packedPage->mutable_user_page_size(), page.GetSizeIU( pcbIUScale.IU_PER_MILS ) );
    packedPage->set_orientation( page.IsPortrait() ? types::PO_PORTRAIT : types::PO_LANDSCAPE );
    packedPage->set_drawing_sheet( getDrawingSheetFileName().ToUTF8() );
    const TITLE_BLOCK& title = aPath.LastScreen()->GetTitleBlock();
    auto* packedTitle = metadata->mutable_title_block();
    packedTitle->set_title( title.GetTitle().ToUTF8() );
    packedTitle->set_date( title.GetDate().ToUTF8() );
    packedTitle->set_revision( title.GetRevision().ToUTF8() );
    packedTitle->set_company( title.GetCompany().ToUTF8() );
    packedTitle->set_comment1( title.GetComment( 0 ).ToUTF8() );
    packedTitle->set_comment2( title.GetComment( 1 ).ToUTF8() );
    packedTitle->set_comment3( title.GetComment( 2 ).ToUTF8() );
    packedTitle->set_comment4( title.GetComment( 3 ).ToUTF8() );
    packedTitle->set_comment5( title.GetComment( 4 ).ToUTF8() );
    packedTitle->set_comment6( title.GetComment( 5 ).ToUTF8() );
    packedTitle->set_comment7( title.GetComment( 6 ).ToUTF8() );
    packedTitle->set_comment8( title.GetComment( 7 ).ToUTF8() );
    packedTitle->set_comment9( title.GetComment( 8 ).ToUTF8() );

    for( const auto& [name, value] : project().GetTextVars() )
        ( *metadata->mutable_text_variables() )[std::string( name.ToUTF8() )] = value.ToUTF8();

    // Persisted project entries only. The UI registry additionally includes
    // inferred symbol variants and changes when the hierarchy cache refreshes.
    // Symbol/instance variant declarations are serialized with their owners.
    for( const auto& [name, description] : schematic()->Settings().m_VariantDescriptions )
        ( *metadata->mutable_variant_descriptions() )[std::string( name.ToUTF8() )] = description.ToUTF8();

    for( const auto& alias : schematic()->GetAllBusAliases() )
    {
        auto* packed = metadata->add_bus_aliases();
        packed->set_name( alias->GetName().ToUTF8() );

        for( const auto& member : alias->Members() )
            packed->add_members( member.ToUTF8() );
    }

    metadata->set_embedded_fonts( schematic()->GetAreFontsEmbedded() );
    PackEmbeddedFiles( *metadata->mutable_embedded_files(), *schematic()->GetEmbeddedFiles() );

    auto* rootRecord = metadata->mutable_root_instance();
    const auto rootState = ResolveRootInstance( schematic(), *aPath.Last() );
    if( rootState.conflict )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "Conflicting root page numbers for one shared schematic file; set a root page number explicitly" );
        return tl::unexpected( error );
    }
    if( rootState.pageNumber )
        rootRecord->set_page_number( rootState.pageNumber->ToUTF8() );

    if( CONNECTION_GRAPH* graph = schematic()->ConnectionGraph() )
    {
        for( const auto& [name, definition] : graph->GetNetChainDefinitions() )
        {
            auto& chain = *metadata->add_net_chains();
            const auto& terminals = definition.terminals;
            chain.set_name( name.ToUTF8() );
            chain.mutable_from()->set_reference( terminals.first.ref.ToUTF8() );
            chain.mutable_from()->set_pin( terminals.first.pin.ToUTF8() );
            chain.mutable_to()->set_reference( terminals.second.ref.ToUTF8() );
            chain.mutable_to()->set_pin( terminals.second.pin.ToUTF8() );
            chain.set_net_class( definition.netClass.ToUTF8() );
            if( definition.color != KIGFX::COLOR4D::UNSPECIFIED )
                PackColor( *chain.mutable_color(), definition.color );
            for( const wxString& net : definition.memberNets )
                chain.add_member_nets( net.ToUTF8() );
            chain.set_committed( definition.committed );
            auto* exclusions = chain.mutable_exclusions();
            for( const wxString& net : definition.excludedNets ) exclusions->add_net_names( net.ToUTF8() );
            for( const auto& [path, pin] : definition.excludedPins )
            {
                auto* anchor = exclusions->add_pins();
                anchor->mutable_pin()->set_value( pin.AsStdString() );
                for( const KIID& id : path ) anchor->mutable_path()->add_path()->set_value( id.AsStdString() );
            }
        }
    }

    *metadata->mutable_formatting() = SCH_FORMATTING::Capture( schematic()->Settings() );
    *metadata->mutable_annotation() = SCH_ANNOTATION::Capture( schematic()->Settings() );
    if( auto settings = project().GetProjectFile().NetSettings() )
    {
        auto* classes = metadata->mutable_net_chain_classes();
        for( const wxString& name : settings->GetNetChainClassDefinitions() )
            classes->add_definitions( name.ToUTF8() );
        for( const auto& [chain, name] : settings->GetNetChainClasses() )
            ( *classes->mutable_assignments() )[chain.ToStdString( wxConvUTF8 )] = name.ToStdString( wxConvUTF8 );
    }
    *metadata->mutable_erc_settings() = SCH_ERC_SETTINGS::Capture( *schematic() );
    const auto ratios = schematic()->Settings().DrawingRatios();
    auto* drawing = metadata->mutable_drawing_ratios();
    drawing->set_dash_length_ratio( ratios[0] );
    drawing->set_gap_length_ratio( ratios[1] );
    drawing->set_text_offset_ratio( ratios[2] );
    drawing->set_label_size_ratio( ratios[3] );
    drawing->set_overbar_height_ratio( ratios[4] );

    for( const char* missing : { "complete_project_settings", "shared_screen_root_ownership",
                                "library_cache", "net_chains" } )
        metadata->add_unrepresented_state( missing );

    const auto& journal = schematic()->ChangeJournal();
    result.mutable_revision()->set_epoch( journal.Epoch() );
    result.mutable_revision()->set_sequence( journal.Sequence() );
    result.set_tracking_complete( false );
    return result;
}


HANDLER_RESULT<types::PageSettings> API_HANDLER_SCH::handleGetPageSettings(
        const HANDLER_CONTEXT<GetPageSettings>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    return API_HANDLER_EDITOR::handleGetPageSettings( aCtx );
}


HANDLER_RESULT<types::PageSettings> API_HANDLER_SCH::handleSetPageSettings(
        const HANDLER_CONTEXT<SetPageSettings>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    auto invalid = []( const std::string& message ) -> HANDLER_RESULT<types::PageSettings>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };

    if( !aCtx.Request.has_page_settings() )
        return invalid( "Page settings are required" );

    const auto& request = aCtx.Request.page_settings();
    auto proposed = PreparePageGeometry( request, *getPageSettings() );
    if( !proposed )
        return tl::unexpected( proposed.error() );

    HANDLER_CONTEXT<GetPageSettings> query;
    query.ClientName = aCtx.ClientName;
    query.Request.mutable_document()->CopyFrom( aCtx.Request.document() );
    auto before = API_HANDLER_EDITOR::handleGetPageSettings( query );

    if( !before )
        return before;

    types::PageSettings normalized = request;
    normalized.DiscardUnknownFields();

    if( before->SerializeAsString() == normalized.SerializeAsString() )
        return before;

    wxString name = wxString::FromUTF8( request.drawing_sheet() );
    DS_DATA_MODEL preparedLayout;
    wxString loadError;

    if( !preparedLayout.LoadFromName( name, project().GetProjectPath(), &project(),
                                     { schematic()->GetEmbeddedFiles() }, &loadError ) )
        return invalid( "Drawing sheet could not be loaded: " + loadError.ToStdString() );

    wxString serializedLayout;
    preparedLayout.SaveInString( &serializedLayout );
    std::unique_ptr<DS_PROXY_UNDO_ITEM> undo;

    if( m_frame )
        undo = std::make_unique<DS_PROXY_UNDO_ITEM>( m_frame );

    // Apply the prevalidated layout rather than reopening a file that could
    // have changed since validation.
    DS_DATA_MODEL::GetTheInstance().SetPageLayout( serializedLayout.ToUTF8() );
    setPageSettings( *proposed );
    BASE_SCREEN::m_DrawingSheetFileName = name;
    schematic()->Settings().m_SchDrawingSheetFileName = name;

    if( m_frame )
    {
        PICKED_ITEMS_LIST command;
        command.SetDescription( _( "Edit Page Settings" ) );
        command.PushItem( ITEM_PICKER( m_frame->GetScreen(), undo.get(), UNDO_REDO::PAGESETTINGS ) );
        m_frame->SaveCopyInUndoList( command, UNDO_REDO::PAGESETTINGS, false );
        undo.release();
        m_frame->GetCanvas()->GetView()->MarkDirty();
        m_frame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
    }

    onModified();
    schematic()->RecordCommittedChange( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT, "Edit Page Settings" );
    return API_HANDLER_EDITOR::handleGetPageSettings( query );
}


HANDLER_RESULT<bool> API_HANDLER_SCH::validateDisplayedSheet( const DocumentSpecifier& aDocument )
{
    if( auto valid = validateDocument( aDocument ); !valid )
        return valid;

    auto current = m_context->GetCurrentSheet();

    if( !current || !aDocument.has_sheet_path()
            || UnpackSheetPath( aDocument.sheet_path() ) != current->Path() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "An explicit currently displayed sheet is required" );
        return tl::unexpected( error );
    }

    return true;
}


HANDLER_RESULT<types::TitleBlockInfo> API_HANDLER_SCH::handleGetTitleBlockInfo(
        const HANDLER_CONTEXT<GetTitleBlockInfo>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    return API_HANDLER_EDITOR::handleGetTitleBlockInfo( aCtx );
}


HANDLER_RESULT<google::protobuf::Empty> API_HANDLER_SCH::handleSetTitleBlockInfo(
        const HANDLER_CONTEXT<SetTitleBlockInfo>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDisplayedSheet( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    if( !aCtx.Request.has_title_block() )
        return API_HANDLER_EDITOR::handleSetTitleBlockInfo( aCtx );

    HANDLER_CONTEXT<GetTitleBlockInfo> query;
    query.ClientName = aCtx.ClientName;
    query.Request.mutable_document()->CopyFrom( aCtx.Request.document() );
    auto before = API_HANDLER_EDITOR::handleGetTitleBlockInfo( query );

    if( !before )
        return tl::unexpected( before.error() );

    types::TitleBlockInfo requested = aCtx.Request.title_block();
    requested.DiscardUnknownFields();

    if( before->SerializeAsString() == requested.SerializeAsString() )
        return google::protobuf::Empty();

    // Use the same page/title-block snapshot and undo entry as the native
    // Page Settings dialog. Build it before modifying the live title block.
    std::unique_ptr<DS_PROXY_UNDO_ITEM> undo;

    if( m_frame )
        undo = std::make_unique<DS_PROXY_UNDO_ITEM>( m_frame );

    auto result = API_HANDLER_EDITOR::handleSetTitleBlockInfo( aCtx );

    if( !result )
        return result;

    if( m_frame )
    {
        PICKED_ITEMS_LIST command;
        command.SetDescription( _( "Edit Title Block" ) );
        command.PushItem( ITEM_PICKER( m_frame->GetScreen(), undo.get(), UNDO_REDO::PAGESETTINGS ) );
        m_frame->SaveCopyInUndoList( command, UNDO_REDO::PAGESETTINGS, false );
        undo.release(); // The native undo list now owns the snapshot.
        m_frame->GetCanvas()->GetView()->MarkDirty();
        m_frame->GetCanvas()->GetView()->UpdateAllItems( KIGFX::REPAINT );
        m_frame->GetCanvas()->Refresh();
    }

    schematic()->RecordCommittedChange( DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT,
                                        "Edit Title Block" );
    return result;
}


std::optional<ApiResponseStatus> API_HANDLER_SCH::checkForStableObservation()
{
    if( auto busy = checkForBusy() )
        return busy;

    // Staged API changes already alter native objects before EndCommit. A
    // screenshot at that point would show uncommitted state while the journal
    // still describes the previous accepted edit. This applies to every client,
    // including the client that owns the pending transaction.
    if( !m_commits.empty() )
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BUSY );
        error.set_error_message( "Finish or cancel the staged schematic transaction before observing" );
        return error;
    }

    // GUI tools also stage native geometry before committing it. Selection and
    // highlighting are harmless; transient editing flags are not a committed
    // revision. Include child fields/pins and visit shared screens only once.
    std::set<SCH_SCREEN*> screens;
    std::set<SCH_ITEM*> seen;
    std::vector<SCH_ITEM*> pending;

    if( schematic() )
    {
        for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() )
        {
            SCH_SCREEN* screen = path.LastScreen();
            if( screen && screens.insert( screen ).second )
                for( SCH_ITEM* item : screen->Items() )
                    pending.push_back( item );
        }
    }

    while( !pending.empty() )
    {
        SCH_ITEM* item = pending.back();
        pending.pop_back();
        if( !item || !seen.insert( item ).second )
            continue;

        // HasFlag(mask) requires every bit; any one transient flag is enough.
        if( item->GetFlags() & ( IN_EDIT | IS_MOVING | IS_NEW ) )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BUSY );
            error.set_error_message( "Finish or cancel the current schematic edit before observing or applying automation changes" );
            return error;
        }
        item->RunOnChildren( [&]( SCH_ITEM* child ) { pending.push_back( child ); },
                             RECURSE_MODE::NO_RECURSE );
    }

    return std::nullopt;
}


HANDLER_RESULT<kiapi::automation::v1::SchematicChangeJournal> API_HANDLER_SCH::handleReadChangeJournal(
        const HANDLER_CONTEXT<kiapi::automation::v1::ReadSchematicChangeJournal>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    const DOCUMENT_CHANGE_JOURNAL& journal = schematic()->ChangeJournal();
    auto history = journal.ReadAfter( aCtx.Request.document_epoch(), aCtx.Request.after_sequence() );
    kiapi::automation::v1::SchematicChangeJournal result;
    result.set_document_epoch( journal.Epoch() );
    result.set_sequence( journal.Sequence() );
    result.set_reset_required( history.resetRequired );
    result.set_tracking_complete( false );

    for( const auto& entry : history.entries )
    {
        auto* change = result.add_changes();
        change->set_sequence( entry.sequence );
        change->set_description( entry.description );
        change->set_origin_id( entry.originId );
        change->set_operation_id( entry.operationId );

        switch( entry.kind )
        {
        case DOCUMENT_CHANGE_JOURNAL::KIND::COMMIT:
            change->set_kind( kiapi::automation::v1::SchematicChange::COMMIT );
            break;
        case DOCUMENT_CHANGE_JOURNAL::KIND::UNDO:
            change->set_kind( kiapi::automation::v1::SchematicChange::UNDO );
            break;
        case DOCUMENT_CHANGE_JOURNAL::KIND::REDO:
            change->set_kind( kiapi::automation::v1::SchematicChange::REDO );
            break;
        }
    }

    return result;
}


HANDLER_RESULT<types::DocumentSpecifier> API_HANDLER_SCH::handleActivateSheet(
        const HANDLER_CONTEXT<kiapi::automation::v1::ActivateSchematicSheet>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    if( auto headless = checkForHeadless( "ActivateSchematicSheet" ) )
        return tl::unexpected( *headless );

    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );

    ApiResponseStatus error;
    error.set_status( ApiStatusCode::AS_BAD_REQUEST );

    if( !aCtx.Request.document().has_sheet_path() )
    {
        error.set_error_message( "Navigation requires an explicit sheet-instance path" );
        return tl::unexpected( error );
    }

    auto target = schematic()->Hierarchy().GetSheetPathByKIIDPath(
            UnpackSheetPath( aCtx.Request.document().sheet_path() ) );

    if( !target )
    {
        error.set_error_message( "The requested sheet instance is not present" );
        return tl::unexpected( error );
    }

    if( m_frame->GetCurrentSheet().Path() != target->Path() )
        toolManager()->RunAction<SCH_SHEET_PATH*>( SCH_ACTIONS::changeSheet, &*target );

    if( m_frame->GetCurrentSheet().Path() != target->Path() )
    {
        error.set_status( ApiStatusCode::AS_NOT_READY );
        error.set_error_message( "The native editor did not activate the requested sheet" );
        return tl::unexpected( error );
    }

    return aCtx.Request.document();
}


HANDLER_RESULT<kiapi::automation::v1::SchematicPreview> API_HANDLER_SCH::handleCapturePreview(
        const HANDLER_CONTEXT<kiapi::automation::v1::CaptureSchematicPreview>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> valid = validateDocument( aCtx.Request.document() );

    if( !valid )
        return tl::unexpected( valid.error() );

    ApiResponseStatus error;
    error.set_status( ApiStatusCode::AS_NOT_READY );

    if( !m_frame || !m_frame->GetCanvas() )
    {
        error.set_error_message( "A graphical schematic canvas is required" );
        return tl::unexpected( error );
    }

    // validateDocument accepts hierarchy members; a canvas image describes only
    // the displayed sheet, so reject requests for another sheet explicitly.
    if( UnpackSheetPath( aCtx.Request.document().sheet_path() )
            != m_frame->GetCurrentSheet().Path() )
    {
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( "Preview target must be the currently displayed sheet" );
        return tl::unexpected( error );
    }

    const DOCUMENT_CHANGE_JOURNAL& journal = schematic()->ChangeJournal();
    const std::string epoch = journal.Epoch();
    const uint64_t sequence = journal.Sequence();
    auto* view = m_frame->GetCanvas()->GetView();
    const VECTOR2D center = view->GetCenter();
    const double scale = view->GetScale();
    // Capture every published view property before repainting. A layer toggle,
    // resize or mirrored transform need not advance the document journal.
    auto captureViewport = [&]()
    {
        kiapi::automation::v1::SchematicViewport viewport;
        const double nmPerIU = 1000000.0 / schIUScale.IU_PER_MM;
        const VECTOR2D origin = view->ToWorld( VECTOR2D( 0, 0 ) );
        const VECTOR2D pixelX = view->ToWorld( VECTOR2D( 1, 0 ) ) - origin;
        const VECTOR2D pixelY = view->ToWorld( VECTOR2D( 0, 1 ) ) - origin;
        viewport.set_origin_x_nm( origin.x * nmPerIU );
        viewport.set_origin_y_nm( origin.y * nmPerIU );
        viewport.set_pixel_x_dx_nm( pixelX.x * nmPerIU );
        viewport.set_pixel_x_dy_nm( pixelX.y * nmPerIU );
        viewport.set_pixel_y_dx_nm( pixelY.x * nmPerIU );
        viewport.set_pixel_y_dy_nm( pixelY.y * nmPerIU );
        viewport.set_canvas_has_keyboard_focus( m_frame->GetCanvas()->HasFocus() );

        for( int layer = SCH_LAYER_ID_START; layer < SCH_LAYER_ID_END; ++layer )
            if( view->IsLayerVisible( layer ) )
                viewport.add_visible_native_layers( layer );

        return viewport;
    };
    const auto capturedViewport = captureViewport();
    wxImage image;

    if( !m_frame->GetCanvas()->GetScreenshot( image ) )
    {
        error.set_error_message( "The canvas has no completed render to capture" );
        return tl::unexpected( error );
    }

    wxMemoryOutputStream stream;

    if( !image.SaveFile( stream, wxBITMAP_TYPE_PNG ) )
    {
        error.set_error_message( "The canvas could not be encoded as PNG" );
        return tl::unexpected( error );
    }

    kiapi::automation::v1::SchematicPreview result;
    if( journal.Epoch() != epoch || journal.Sequence() != sequence
            || view->GetCenter() != center || view->GetScale() != scale
            || !google::protobuf::util::MessageDifferencer::Equals( capturedViewport,
                                                                   captureViewport() )
            || m_frame->GetCurrentSheet().Path()
                    != UnpackSheetPath( aCtx.Request.document().sheet_path() ) )
    {
        error.set_error_message( "The document or viewport changed during capture; retry" );
        return tl::unexpected( error );
    }

    result.mutable_revision()->set_epoch( epoch );
    result.mutable_revision()->set_sequence( sequence );
    result.set_tracking_complete( false );
    result.mutable_viewport()->CopyFrom( capturedViewport );

    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.set_width_pixels( image.GetWidth() );
    result.set_height_pixels( image.GetHeight() );
    result.mutable_png()->resize( stream.GetSize() );
    stream.CopyTo( result.mutable_png()->data(), result.png().size() );
    return result;
}


HANDLER_RESULT<google::protobuf::Empty> API_HANDLER_SCH::handleSaveDocument( const HANDLER_CONTEXT<SaveDocument>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    // Validate every file before the normal multi-file save begins, without
    // opening a modal error dialog through the automation interface.
    for( const SCH_SHEET_PATH& path : schematic()->Hierarchy() )
        if( ResolveRootInstance( schematic(), *path.Last() ).conflict )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Conflicting root page numbers for one shared schematic file; no files were saved" );
            return tl::unexpected( error );
        }

    if( !context()->SaveSchematic() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "failed to save schematic" );
        return tl::unexpected( e );
    }

    return google::protobuf::Empty();
}


HANDLER_RESULT<google::protobuf::Empty>
API_HANDLER_SCH::handleSaveCopyOfDocument( const HANDLER_CONTEXT<SaveCopyOfDocument>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    wxFileName schematicPath( project().AbsolutePath( wxString::FromUTF8( aCtx.Request.path() ) ) );

    if( !schematicPath.IsOk() || !schematicPath.IsDirWritable() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message(
                fmt::format( "save path '{}' could not be opened", schematicPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    if( schematicPath.FileExists() && ( !schematicPath.IsFileWritable() || !aCtx.Request.options().overwrite() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "save path '{}' exists and cannot be overwritten",
                                          schematicPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    if( schematicPath.GetExt() != FILEEXT::KiCadSchematicFileExtension )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "save path '{}' must have a kicad_sch extension",
                                          schematicPath.GetFullPath().ToStdString() ) );
        return tl::unexpected( e );
    }

    bool includeProject = true;

    if( aCtx.Request.has_options() )
        includeProject = aCtx.Request.options().include_project();

    if( !context()->SaveSchematicCopy( schematicPath.GetFullPath(), includeProject ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "failed to save schematic copy" );
        return tl::unexpected( e );
    }

    return google::protobuf::Empty();
}


HANDLER_RESULT<google::protobuf::Empty>
API_HANDLER_SCH::handleRevertDocument( const HANDLER_CONTEXT<RevertDocument>& aCtx )
{
    if( aCtx.Request.document().type() != DocumentType::DOCTYPE_SCHEMATIC )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    if( !m_commits.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BUSY );
        e.set_error_message( "cannot revert while a commit is open" );
        return tl::unexpected( e );
    }

    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "RevertDocument" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    wxFileName fn = project().AbsolutePath( schematic()->GetFileName() );

    if( m_frame->GetCurrentSheet().Last() != &schematic()->Root() )
    {
        SCH_SHEET_PATH rootSheetPath = schematic()->Hierarchy().at( 0 );
        m_frame->GetToolManager()->RunAction<SCH_SHEET_PATH*>( SCH_ACTIONS::changeSheet, &rootSheetPath );
    }

    SCH_SCREENS screenList( schematic()->Root() );

    for( SCH_SCREEN* screen = screenList.GetFirst(); screen; screen = screenList.GetNext() )
        screen->SetContentModified( false );

    m_frame->ReleaseFile();
    if( !m_frame->OpenProjectFiles( std::vector<wxString>( 1, fn.GetFullPath() ), KICTL_REVERT ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "Schematic reload failed; inspect native diagnostics and reobserve the editor" );
        return tl::unexpected( e );
    }

    return google::protobuf::Empty();
}


HANDLER_RESULT<GetOpenDocumentsResponse> API_HANDLER_SCH::handleGetOpenDocuments(
        const HANDLER_CONTEXT<GetOpenDocuments>& aCtx )
{
    if( aCtx.Request.type() != DocumentType::DOCTYPE_SCHEMATIC )
    {
        ApiResponseStatus e;

        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    GetOpenDocumentsResponse response;
    common::types::DocumentSpecifier doc;

    wxFileName fn( m_context->GetCurrentFileName() );

    doc.set_type( DocumentType::DOCTYPE_SCHEMATIC );

    if( std::optional<SCH_SHEET_PATH> path = m_context->GetCurrentSheet() )
        PackSheetPath( *doc.mutable_sheet_path(), path->Path() );

    PackProject( *doc.mutable_project(), m_context->Prj() );

    response.mutable_documents()->Add( std::move( doc ) );
    return response;
}


void API_HANDLER_SCH::filterValidSchTypes( std::set<KICAD_T>& aTypeList )
{
    std::erase_if( aTypeList,
                   []( KICAD_T aType )
                   {
                       return !s_allowedTypes.contains( aType );
                   } );
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_SCH::handleGetItems( const HANDLER_CONTEXT<GetItems>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( HANDLER_RESULT<std::optional<KIID>> valid = validateItemHeaderDocument( aCtx.Request.header() );
        !valid.has_value() )
    {
        return tl::unexpected( valid.error() );
    }

    std::set<KICAD_T> typesRequested, typesInserted;

    for( KICAD_T type : parseRequestedItemTypes( aCtx.Request.types() ) )
        typesRequested.insert( type );

    filterValidSchTypes( typesRequested );

    if( typesRequested.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Schematic object" );
        return tl::unexpected( e );
    }

    SCH_SHEET_LIST hierarchy = schematic()->Hierarchy();
    std::optional<SCH_SHEET_PATH> pathFilter;

    if( aCtx.Request.header().document().has_sheet_path() )
    {
        KIID_PATH kp = UnpackSheetPath( aCtx.Request.header().document().sheet_path() );
        pathFilter = hierarchy.GetSheetPathByKIIDPath( kp );
    }

    std::map<KICAD_T, std::vector<std::pair<EDA_ITEM*, SCH_SHEET_PATH>>> itemMap;

    auto processScreen =
        [&]( const SCH_SHEET_PATH& aPath )
        {
            const SCH_SCREEN* aScreen = aPath.LastScreen();

            for( SCH_ITEM* aItem : aScreen->Items() )
            {
                itemMap[ aItem->Type() ].emplace_back( aItem, aPath );

                aItem->RunOnChildren(
                        [&]( SCH_ITEM* aChild )
                        {
                            itemMap[ aChild->Type() ].emplace_back( aChild, aPath );
                        },
                        RECURSE_MODE::NO_RECURSE );
            }
        };

    if( pathFilter )
    {
        processScreen( *pathFilter );
    }
    else
    {
        for( const SCH_SHEET_PATH& path : hierarchy )
            processScreen( path );
    }

    GetItemsResponse response;
    google::protobuf::Any any;

    for( KICAD_T type : parseRequestedItemTypes( aCtx.Request.types() ) )
    {
        if( !s_allowedTypes.contains( type ) )
            continue;

        if( typesInserted.contains( type ) )
            continue;

        for( const auto& [item, itemPath] : itemMap[type] )
        {
            if( packSchItem( any, static_cast<SCH_ITEM*>( item ), itemPath ) )
                response.mutable_items()->Add( std::move( any ) );
        }
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<GetItemsResponse> API_HANDLER_SCH::handleGetItemsById( const HANDLER_CONTEXT<GetItemsById>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    SCH_SHEET_LIST hierarchy = schematic()->Hierarchy();
    std::optional<SCH_SHEET_PATH> pathFilter;

    if( aCtx.Request.header().document().has_sheet_path() )
    {
        KIID_PATH kp = UnpackSheetPath( aCtx.Request.header().document().sheet_path() );
        pathFilter = hierarchy.GetSheetPathByKIIDPath( kp );
    }

    GetItemsResponse response;
    SCH_ITEM* item = nullptr;
    google::protobuf::Any any;

    for( const types::KIID& idProto : aCtx.Request.items() )
    {
        KIID id( idProto.value() );

        SCH_SHEET_PATH itemPath;

        if( pathFilter )
        {
            item = pathFilter->ResolveItem( id );
            itemPath = *pathFilter;
        }
        else
        {
            item = hierarchy.ResolveItem( id, &itemPath, true );
        }

        if( !item || !s_allowedTypes.contains( item->Type() ) )
            continue;

        if( item->Type() == SCH_SYMBOL_T )
        {
            kiapi::schematic::types::SchematicSymbolInstance symbol;

            if( !PackSymbol( &symbol, static_cast<SCH_SYMBOL*>( item ), itemPath ) )
                continue;

            any.PackFrom( symbol );
        }
        else if( item->Type() == SCH_SHEET_T )
        {
            kiapi::schematic::types::SheetSymbol sheet;

            if( !PackSheet( &sheet, static_cast<SCH_SHEET*>( item ), itemPath ) )
                continue;

            any.PackFrom( sheet );
        }
        else
        {
            item->Serialize( any );
        }

        response.mutable_items()->Add( std::move( any ) );
    }

    if( response.items().empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested IDs were found or valid" );
        return tl::unexpected( e );
    }

    response.set_status( ItemRequestStatus::IRS_OK );
    return response;
}


HANDLER_RESULT<SelectionResponse>
API_HANDLER_SCH::handleGetSelection( const HANDLER_CONTEXT<GetSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "GetSelection" ) )
        return tl::unexpected( *headless );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    std::set<KICAD_T> filter;

    for( KICAD_T type : parseRequestedItemTypes( aCtx.Request.types() ) )
        filter.insert( type );

    SCH_SELECTION_TOOL* tool = m_context->GetToolManager()->GetTool<SCH_SELECTION_TOOL>();
    SCH_SHEET_PATH path = m_context->GetCurrentSheet().value_or( SCH_SHEET_PATH() );

    SelectionResponse response;
    google::protobuf::Any any;

    for( EDA_ITEM* item : tool->GetSelection() )
    {
        if( filter.empty() || filter.contains( item->Type() ) )
        {
            if( packSchItem( any, static_cast<SCH_ITEM*>( item ), path ) )
                response.mutable_items()->Add( std::move( any ) );
        }
    }

    return response;
}


HANDLER_RESULT<Empty>
API_HANDLER_SCH::handleClearSelection( const HANDLER_CONTEXT<ClearSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "ClearSelection" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    m_context->GetToolManager()->RunAction( ACTIONS::selectionClear );
    m_frame->Refresh();

    return Empty();
}


HANDLER_RESULT<SelectionResponse>
API_HANDLER_SCH::handleAddToSelection( const HANDLER_CONTEXT<AddToSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "AddToSelection" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    SCH_SELECTION_TOOL* tool = m_context->GetToolManager()->GetTool<SCH_SELECTION_TOOL>();
    SCH_SHEET_PATH current = m_context->GetCurrentSheet().value_or( SCH_SHEET_PATH() );

    EDA_ITEMS toAdd;

    for( const types::KIID& id : aCtx.Request.items() )
    {
        SCH_SHEET_PATH itemPath;

        // Selection only operates on the currently-displayed sheet; off-sheet items are skipped
        if( std::optional<SCH_ITEM*> item = getItemById( KIID( id.value() ), &itemPath );
            item && itemPath == current )
        {
            toAdd.push_back( *item );
        }
    }

    tool->AddItemsToSel( &toAdd );
    m_frame->Refresh();

    SelectionResponse response;
    google::protobuf::Any any;

    for( EDA_ITEM* item : tool->GetSelection() )
    {
        if( packSchItem( any, static_cast<SCH_ITEM*>( item ), current ) )
            response.mutable_items()->Add( std::move( any ) );
    }

    return response;
}


HANDLER_RESULT<SelectionResponse>
API_HANDLER_SCH::handleRemoveFromSelection( const HANDLER_CONTEXT<RemoveFromSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "RemoveFromSelection" ) )
        return tl::unexpected( *headless );

    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    if( !validateItemHeaderDocument( aCtx.Request.header() ) )
    {
        ApiResponseStatus e;
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }

    SCH_SELECTION_TOOL* tool = m_context->GetToolManager()->GetTool<SCH_SELECTION_TOOL>();
    SCH_SHEET_PATH current = m_context->GetCurrentSheet().value_or( SCH_SHEET_PATH() );

    EDA_ITEMS toRemove;

    for( const types::KIID& id : aCtx.Request.items() )
    {
        SCH_SHEET_PATH itemPath;

        if( std::optional<SCH_ITEM*> item = getItemById( KIID( id.value() ), &itemPath );
            item && itemPath == current )
        {
            toRemove.push_back( *item );
        }
    }

    tool->RemoveItemsFromSel( &toRemove );
    m_frame->Refresh();

    SelectionResponse response;
    google::protobuf::Any any;

    for( EDA_ITEM* item : tool->GetSelection() )
    {
        if( packSchItem( any, static_cast<SCH_ITEM*>( item ), current ) )
            response.mutable_items()->Add( std::move( any ) );
    }

    return response;
}


HANDLER_RESULT<std::unique_ptr<EDA_ITEM>> API_HANDLER_SCH::createItemForType( KICAD_T aType, EDA_ITEM* aContainer )
{
    if( !aContainer )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "Tried to create an item in a null container" );
        return tl::unexpected( e );
    }

    if( !s_allowedTypes.contains( aType ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "type {} is not supported by the schematic API handler",
                                          magic_enum::enum_name( aType ) ) );
        return tl::unexpected( e );
    }

    if( aType == SCH_PIN_T && !dynamic_cast<SCH_SYMBOL*>( aContainer ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "Tried to create a pin in {}, which is not a symbol",
                                          aContainer->GetFriendlyName().ToStdString() ) );
        return tl::unexpected( e );
    }
    else if( aType == SCH_SHEET_T && !dynamic_cast<SCH_SCREEN*>( aContainer ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "Tried to create a sheet symbol in {}, which is not a "
                                          "schematic sheet",
                                          aContainer->GetFriendlyName().ToStdString() ) );
        return tl::unexpected( e );
    }
    else if( aType == SCH_SYMBOL_T && !dynamic_cast<SCH_SCREEN*>( aContainer ) )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "Tried to create a symbol in {}, which is not a "
                                          "schematic sheet",
                                          aContainer->GetFriendlyName().ToStdString() ) );
        return tl::unexpected( e );
    }

    std::unique_ptr<EDA_ITEM> created = CreateItemForType( aType, aContainer );

    if( created && !created->GetParent() )
        created->SetParent( aContainer );

    if( !created )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( fmt::format( "Tried to create an item of type {}, which is unhandled",
                                          magic_enum::enum_name( aType ) ) );
        return tl::unexpected( e );
    }

    return created;
}


HANDLER_RESULT<ItemRequestStatus> API_HANDLER_SCH::handleCreateUpdateItemsInternal( bool aCreate,
        const std::string& aClientName,
        const types::ItemHeader &aHeader,
        const google::protobuf::RepeatedPtrField<google::protobuf::Any>& aItems,
        std::function<void( ItemStatus, google::protobuf::Any )> aItemHandler )
{
    ApiResponseStatus e;

    auto containerResult = validateItemHeaderDocument( aHeader );

    if( !containerResult && containerResult.error().status() == ApiStatusCode::AS_UNHANDLED )
    {
        // No message needed for AS_UNHANDLED; this is an internal flag for the API server
        e.set_status( ApiStatusCode::AS_UNHANDLED );
        return tl::unexpected( e );
    }
    else if( !containerResult )
    {
        e.CopyFrom( containerResult.error() );
        return tl::unexpected( e );
    }

    SCH_SHEET_LIST hierarchy = schematic()->Hierarchy();
    SCH_SCREEN* targetScreen = schematic()->GetCurrentScreen();
    SCH_SHEET_PATH targetPath = m_context->GetCurrentSheet().value_or( *hierarchy.begin() );

    if( aHeader.document().has_sheet_path() )
    {
        KIID_PATH kp = UnpackSheetPath( aHeader.document().sheet_path() );
        if( std::optional<SCH_SHEET_PATH> path = resolveBatchSheet( kp ) )
        {
            targetPath = *path;
            targetScreen = targetPath.LastScreen();
        }
    }

    SCH_COMMIT* commit = static_cast<SCH_COMMIT*>( getCurrentCommit( aClientName ) );
    bool connectivityChanged = false;   // an in-place symbol update invalidated the net graph

    for( const google::protobuf::Any& anyItem : aItems )
    {
        ItemStatus status;
        std::optional<KICAD_T> type = TypeNameFromAny( anyItem );

        if( !type )
        {
            status.set_code( ItemStatusCode::ISC_INVALID_TYPE );
            status.set_error_message( fmt::format( "Could not decode a valid type from {}",
                                                   anyItem.type_url() ) );
            aItemHandler( status, anyItem );
            continue;
        }

        EDA_ITEM* container = targetScreen;

        if( !SchematicFieldTextModesArePersistable( anyItem ) )
        {
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( "Schematic fields require native multiline text mode for save/reopen fidelity" );
            return tl::unexpected( e );
        }

        HANDLER_RESULT<std::unique_ptr<EDA_ITEM>> creationResult = createItemForType( *type, container );

        if( !creationResult )
        {
            status.set_code( ItemStatusCode::ISC_INVALID_TYPE );
            status.set_error_message( creationResult.error().error_message() );
            aItemHandler( status, anyItem );
            continue;
        }

        std::unique_ptr<EDA_ITEM> item( std::move( *creationResult ) );

        bool unpacked = false;

        // Retained past the unpack: the placement data they carry is applied once the item is
        // in the schematic.
        kiapi::schematic::types::SchematicSymbolInstance symbolProto;
        kiapi::schematic::types::SheetSymbol            sheetProto;

        if( *type == SCH_SYMBOL_T )
        {
            unpacked = anyItem.UnpackTo( &symbolProto )
                       && UnpackSymbol( static_cast<SCH_SYMBOL*>( item.get() ), symbolProto );
        }
        else if( *type == SCH_GROUP_T )
        {
            kiapi::schematic::types::Group groupProto;
            unpacked = anyItem.UnpackTo( &groupProto );
            if( unpacked )
            {
                auto metadata = groupProto;
                metadata.clear_items();
                google::protobuf::Any metadataAny;
                metadataAny.PackFrom( metadata );
                unpacked = item->Deserialize( metadataAny );
                auto* group = static_cast<SCH_GROUP*>( item.get() );
                for( const auto& memberId : groupProto.items() )
                {
                    KIID id( memberId.value() );
                    SCH_ITEM* member = nullptr;
                    if( m_atomicCreatedItems )
                    {
                        auto staged = m_atomicCreatedItems->find( { targetScreen, id } );
                        if( staged != m_atomicCreatedItems->end() )
                            member = staged->second.get();
                    }
                    if( !member )
                        member = targetPath.ResolveItem( id );
                    if( !member || id.AsStdString() != memberId.value() || id == group->m_Uuid
                            || member->GetParent() != targetScreen
                            || ( member->GetParentGroup()
                                 && member->GetParentGroup()->AsEdaItem()->m_Uuid != group->m_Uuid )
                            || ( aCreate && member->IsLocked() ) || !member->IsGroupableType()
                            || ( commit->GetStatus( member, targetScreen ) & CHT_TYPE ) == CHT_REMOVE
                            || !group->GetItems().insert( member ).second )
                    {
                        unpacked = false;
                        break;
                    }
                    if( member->Type() == SCH_GROUP_T )
                    {
                        SCH_ITEM* existingGroup = targetPath.ResolveItem( group->m_Uuid );
                        if( existingGroup && static_cast<SCH_GROUP*>( member )->ContainsItem( existingGroup ) )
                        {
                            unpacked = false;
                            break;
                        }
                    }
                }
                if( aCreate && groupProto.items().empty() )
                    unpacked = false; // Empty groups are not persisted by the native writer.
            }
        }
        else if( *type == SCH_SHEET_T )
        {
            unpacked = anyItem.UnpackTo( &sheetProto );

            if( unpacked )
            {
                SCH_SHEET* sheet = static_cast<SCH_SHEET*>( item.get() );

                if( tl::expected<bool, ApiResponseStatus> result = UnpackSheet( sheet, sheetProto );
                    result.has_value() )
                {
                    unpacked = *result;
                }
                else
                {
                    return tl::unexpected( result.error() );
                }
            }
        }
        else
        {
            unpacked = item->Deserialize( anyItem );
        }

        if( !unpacked )
        {
            e.set_status( ApiStatusCode::AS_BAD_REQUEST );
            e.set_error_message( fmt::format( "could not unpack {} from request",
                                              item->GetClass().ToStdString() ) );
            return tl::unexpected( e );
        }

        if( m_atomicBatchActive )
        {
            auto descriptionsMatch = [&]( const auto& variants )
            {
                for( const auto& variant : variants.variants() )
                {
                    if( !variant.has_description() ) continue;
                    const auto& descriptions = schematic()->Settings().m_VariantDescriptions;
                    auto found = descriptions.find( wxString::FromUTF8( variant.name() ) );
                    if( ( found == descriptions.end() && !variant.description().empty() )
                            || ( found != descriptions.end()
                                 && found->second != wxString::FromUTF8( variant.description() ) ) )
                        return false;
                }
                return true;
            };
            if( !descriptionsMatch( symbolProto.variants() ) || !descriptionsMatch( sheetProto.variants() ) )
            {
                status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                status.set_error_message( "Project variant descriptions require a dedicated metadata operation" );
                aItemHandler( status, anyItem );
                continue;
            }
        }

        if( std::vector<wxString> removed = item->RemoveConflictingCustomProperties(); !removed.empty() )
        {
            auto as_str =
                []( const wxString& aIn )
                {
                    return std::string( aIn.ToUTF8() );
                };

            status.set_code( ItemStatusCode::ISC_INVALID_DATA );
            status.set_error_message( fmt::format(
                    "Invalid custom properties for item {}: property name(s) '{}' already in use",
                    item->m_Uuid.AsStdString(), fmt::join( std::views::transform( removed, as_str ), ", " ) ) );

            aItemHandler( status, anyItem );
            continue;
        }

        SCH_ITEM* existingItem = nullptr;
        SCH_SHEET_PATH existingPath;

        if( m_atomicCreatedItems )
        {
            auto staged = m_atomicCreatedItems->find( { targetScreen, item->m_Uuid } );

            if( staged != m_atomicCreatedItems->end() )
                existingItem = staged->second.get();
        }

        if( !existingItem )
            existingItem = targetPath.ResolveItem( item->m_Uuid );

        // Deferred removals still exist in the native screen, but are absent
        // from the ordered batch's logical state. A later create may replace
        // that identity; an update or second removal must not resurrect it.
        if( m_atomicBatchActive && existingItem
                && ( commit->GetStatus( existingItem, targetScreen ) & CHT_TYPE ) == CHT_REMOVE )
            existingItem = nullptr;

        if( existingItem )
            existingPath = targetPath;

        if( aCreate && existingItem )
        {
            status.set_code( ItemStatusCode::ISC_EXISTING );
            status.set_error_message( fmt::format( "an item with UUID {} already exists",
                                                   item->m_Uuid.AsStdString() ) );
            aItemHandler( status, anyItem );
            continue;
        }
        else if( !aCreate && !existingItem )
        {
            status.set_code( ItemStatusCode::ISC_NONEXISTENT );
            status.set_error_message( fmt::format( "an item with UUID {} does not exist",
                                                   item->m_Uuid.AsStdString() ) );
            aItemHandler( status, anyItem );
            continue;
        }

        if( m_atomicBatchActive && existingItem && existingItem->IsLocked() )
        {
            status.set_code( ItemStatusCode::ISC_IMMUTABLE );
            status.set_error_message( "An atomic edit cannot modify a locked item" );
            aItemHandler( status, anyItem );
            continue;
        }

        if( !aCreate )
        {
            SCH_SCREEN* itemScreen = existingPath.LastScreen();

            if( itemScreen != targetScreen )
            {
                status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                status.set_error_message( fmt::format( "item {} exists on a different sheet than targeted",
                                                       item->m_Uuid.AsStdString() ) );
                aItemHandler( status, anyItem );
                continue;
            }
        }

        if( *type == SCH_SHEET_T )
        {
            SCH_SHEET* sheet = static_cast<SCH_SHEET*>( item.get() );
            if( sheetProto.has_child_screen_id()
                    && ( !KIID::SniffTest( wxString::FromUTF8( sheetProto.child_screen_id().value() ) )
                         || KIID( sheetProto.child_screen_id().value() ).AsStdString() != sheetProto.child_screen_id().value()
                         || KIID( sheetProto.child_screen_id().value() ) == niluuid ) )
            {
                status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                status.set_error_message( "Child screen identity must be a canonical nonempty UUID" );
                aItemHandler( status, anyItem );
                continue;
            }

            if( !aCreate )
            {
                auto* existingSheet = static_cast<SCH_SHEET*>( existingItem );
                if( sheet->GetFileName() != existingSheet->GetFileName()
                        || ( sheetProto.has_child_screen_id()
                             && ( !existingSheet->GetScreen()
                                  || sheetProto.child_screen_id().value() != existingSheet->GetScreen()->GetUuid().AsStdString() ) ) )
                {
                    status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                    status.set_error_message( "Changing a referenced child screen requires an explicit hierarchy transaction" );
                    aItemHandler( status, anyItem );
                    continue;
                }
            }

            SCH_SHEET_PATH parentPath;

            if( aCreate )
                parentPath = targetPath;
            else
                parentPath = existingPath;

            wxString destFilePath = parentPath.LastScreen()->GetFileName();

            if( aCreate && !sheet->GetScreen() )
            {
                wxFileName requested( ExpandEnvVarSubstitutions( sheet->GetFileName(), &project() ) );

                if( sheet->GetFileName().IsEmpty()
                        || !requested.MakeAbsolute( wxFileName( destFilePath ).GetPath() ) )
                {
                    status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                    status.set_error_message( "A sheet requires a resolvable schematic filename" );
                    aItemHandler( status, anyItem );
                    continue;
                }

                requested.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE );

                for( const SCH_SHEET_PATH& loaded : hierarchy )
                {
                    if( wxFileName( loaded.LastScreen()->GetFileName() ) == requested )
                    {
                        sheet->SetScreen( loaded.LastScreen() );
                        break;
                    }
                }
                if( !sheet->GetScreen() && m_atomicCreatedItems )
                {
                    // A prior create in this batch may have established this
                    // child screen without attaching it to the hierarchy yet.
                    for( const auto& [id, pending] : *m_atomicCreatedItems )
                    {
                        if( pending->Type() != SCH_SHEET_T ) continue;
                        SCH_SCREEN* screen = static_cast<SCH_SHEET*>( pending.get() )->GetScreen();
                        if( screen && wxFileName( screen->GetFileName() ) == requested )
                        {
                            sheet->SetScreen( screen );
                            break;
                        }
                    }
                }

                if( !sheet->GetScreen() )
                {
                    // Never substitute a blank screen for an existing unloaded file.
                    if( requested.FileExists() )
                    {
                        status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                        status.set_error_message( "Importing an unloaded sheet file is not yet supported" );
                        aItemHandler( status, anyItem );
                        continue;
                    }

                    bool identityInUse = false;
                    if( sheetProto.has_child_screen_id() )
                    {
                        for( const SCH_SHEET_PATH& loaded : hierarchy )
                            if( loaded.LastScreen()->GetUuid().AsStdString() == sheetProto.child_screen_id().value() )
                                identityInUse = true;
                        if( m_atomicCreatedItems )
                            for( const auto& [id, pending] : *m_atomicCreatedItems )
                            {
                                if( pending->Type() != SCH_SHEET_T ) continue;
                                SCH_SCREEN* screen = static_cast<SCH_SHEET*>( pending.get() )->GetScreen();
                                if( screen && screen->GetUuid().AsStdString() == sheetProto.child_screen_id().value() )
                                    identityInUse = true;
                            }
                    }
                    if( identityInUse )
                    {
                        status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                        status.set_error_message( "Child screen identity already belongs to another loaded file" );
                        aItemHandler( status, anyItem );
                        continue;
                    }

                    sheet->SetScreen( new SCH_SCREEN( schematic() ) );
                    sheet->GetScreen()->SetFileName( requested.GetFullPath() );
                    if( sheetProto.has_child_screen_id() )
                        sheet->GetScreen()->SetUuid( KIID( sheetProto.child_screen_id().value() ) );
                }
                if( sheetProto.has_child_screen_id()
                        && sheet->GetScreen()->GetUuid().AsStdString() != sheetProto.child_screen_id().value() )
                {
                    status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                    status.set_error_message( "Child screen identity does not match the referenced file" );
                    aItemHandler( status, anyItem );
                    continue;
                }
            }

            if( !destFilePath.IsEmpty() )
            {
                SCH_SHEET_LIST schematicSheets = schematic()->Hierarchy();
                SCH_SHEET_LIST loadedSheets( sheet );

                if( schematicSheets.TestForRecursion( loadedSheets, destFilePath ) )
                {
                    status.set_code( ItemStatusCode::ISC_INVALID_DATA );
                    status.set_error_message( "sheet update would create recursive hierarchy" );
                    aItemHandler( status, anyItem );
                    continue;
                }
            }
        }

        status.set_code( ItemStatusCode::ISC_OK );
        google::protobuf::Any newItem;

        if( aCreate )
        {
            SCH_ITEM* createdItem = static_cast<SCH_ITEM*>( item.release() );

            if( m_atomicCreatedItems )
                m_atomicCreatedItems->emplace( std::make_pair( targetScreen, createdItem->m_Uuid ),
                                              std::unique_ptr<SCH_ITEM>( createdItem ) );

            if( createdItem->Type() == SCH_GROUP_T )
            {
                auto* group = static_cast<SCH_GROUP*>( createdItem );
                const auto members = group->GetItems();
                for( EDA_ITEM* member : members )
                {
                    // Mirror the native Group Items tool: membership changes
                    // belong to the same commit as the new group, including
                    // rollback and members created earlier in this batch.
                    commit->Modify( member, targetScreen, RECURSE_MODE::NO_RECURSE );
                    group->AddItem( member );
                }
            }

            commit->Add( createdItem, targetScreen );

            if( !createdItem )
            {
                e.set_status( ApiStatusCode::AS_BAD_REQUEST );
                e.set_error_message( "could not add the requested item to its parent container" );
                return tl::unexpected( e );
            }

            if( createdItem->Type() == SCH_SYMBOL_T )
            {
                SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( createdItem );
                kiapi::schematic::types::SchematicSymbolInstance packed;

                ApplySymbolInstance( symbol, symbolProto, targetPath, schematic() );

                if( PackSymbol( &packed, symbol, targetPath ) )
                    newItem.PackFrom( packed );
            }
            else if( createdItem->Type() == SCH_SHEET_T )
            {
                SCH_SHEET* sheet = static_cast<SCH_SHEET*>( createdItem );
                kiapi::schematic::types::SheetSymbol packed;

                if( sheetProto.page_number().empty() )
                    sheetProto.set_page_number( hierarchy.GetNextPageNumber().ToUTF8() );

                ApplySheetInstance( sheet, sheetProto, targetPath, schematic() );

                if( PackSheet( &packed, sheet, targetPath ) )
                    newItem.PackFrom( packed );
            }
            else
            {
                createdItem->Serialize( newItem );
            }
        }
        else
        {
            // SwapItemData hands the item the temporary's (empty) instance list, so keep the
            // placements to restore afterwards.
            std::vector<SCH_SYMBOL_INSTANCE> symbolPlacements;
            std::vector<SCH_SHEET_INSTANCE>  sheetPlacements;

            if( existingItem->Type() == SCH_SYMBOL_T )
                symbolPlacements = static_cast<SCH_SYMBOL*>( existingItem )->GetInstances();
            else if( existingItem->Type() == SCH_SHEET_T )
                sheetPlacements = static_cast<SCH_SHEET*>( existingItem )->GetInstances();

            if( existingItem->Type() == SCH_GROUP_T )
            {
                auto* oldGroup = static_cast<SCH_GROUP*>( existingItem );
                auto* newGroup = static_cast<SCH_GROUP*>( item.get() );
                std::unordered_set<EDA_ITEM*> changedMembers;
                for( EDA_ITEM* member : oldGroup->GetItems() )
                    if( !newGroup->GetItems().count( member ) ) changedMembers.insert( member );
                for( EDA_ITEM* member : newGroup->GetItems() )
                    if( !oldGroup->GetItems().count( member ) ) changedMembers.insert( member );
                bool locked = std::any_of( changedMembers.begin(), changedMembers.end(),
                                          []( EDA_ITEM* member ) { return member->IsLocked(); } );
                if( locked )
                {
                    status.set_code( ItemStatusCode::ISC_IMMUTABLE );
                    status.set_error_message( "Group membership cannot change for a locked member" );
                    aItemHandler( status, anyItem );
                    continue;
                }
                for( EDA_ITEM* member : changedMembers )
                    commit->Modify( member, targetScreen, RECURSE_MODE::NO_RECURSE );
            }
            commit->Modify( existingItem, targetScreen );
            // Symbol replacement swaps pin allocations even for an electrically
            // identical payload. Remove the old addresses before the temporary
            // owning them is destroyed; graph entries must never outlive pins.
            if( existingItem->Type() == SCH_SYMBOL_T )
            {
                CONNECTION_GRAPH* graph = schematic()->ConnectionGraph();
                graph->RemoveItem( existingItem );
                for( SCH_PIN* pin : static_cast<SCH_SYMBOL*>( existingItem )->GetPins() )
                    graph->RemoveItem( pin );
            }
            existingItem->SwapItemData( static_cast<SCH_ITEM*>( item.get() ) );

            if( existingItem->IsConnectable() )
            {
                existingItem->SetConnectivityDirty();
                connectivityChanged = true;
            }

            if( existingItem->Type() == SCH_SYMBOL_T )
            {
                SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( existingItem );
                kiapi::schematic::types::SchematicSymbolInstance packed;

                if( !symbolProto.has_instance_records() )
                {
                    for( const SCH_SYMBOL_INSTANCE& placement : symbolPlacements )
                        symbol->AddHierarchicalReference( placement );
                }

                ApplySymbolInstance( symbol, symbolProto, existingPath, schematic() );
                for( SCH_PIN* pin : symbol->GetPins() )
                    pin->SetConnectivityDirty();

                if( PackSymbol( &packed, symbol, existingPath ) )
                    newItem.PackFrom( packed );
            }
            else if( existingItem->Type() == SCH_SHEET_T )
            {
                SCH_SHEET* sheet = static_cast<SCH_SHEET*>( existingItem );
                kiapi::schematic::types::SheetSymbol packed;

                if( !sheetProto.has_instance_records() )
                {
                    for( const SCH_SHEET_INSTANCE& placement : sheetPlacements )
                        sheet->AddInstance( placement );
                }

                ApplySheetInstance( sheet, sheetProto, existingPath, schematic() );

                if( PackSheet( &packed, sheet, existingPath ) )
                    newItem.PackFrom( packed );
            }
            else
            {
                existingItem->Serialize( newItem );
            }
        }

        aItemHandler( status, newItem );
    }

    if( !m_activeClients.contains( aClientName ) )
    {
        pushCurrentCommit( aClientName, aCreate ? _( "Created items via API" )
                                                : _( "Modified items via API" ) );
    }

    // Staged edits have not yet updated every screen index. Rebuilding after
    // each item exposes an intermediate graph and can cache a false disconnect
    // before a following wire operation. SCH_COMMIT rebuilds the final state.
    if( m_frame && connectivityChanged && !m_activeClients.contains( aClientName ) )
        m_frame->RecalculateConnections( nullptr, LOCAL_CLEANUP );

    return ItemRequestStatus::IRS_OK;
}


void API_HANDLER_SCH::deleteItemsInternal( std::map<KIID, ItemDeletionStatus>& aItemsToDelete,
                                           const std::string& aClientName )
{
    SCH_SHEET_LIST hierarchy = schematic()->Hierarchy();
    COMMIT* commit = getCurrentCommit( aClientName );

    for( auto& [id, status] : aItemsToDelete )
    {
        SCH_SHEET_PATH path;
        SCH_ITEM* item = nullptr;
        if( m_atomicBatchActive && m_atomicTargetPath )
        {
            path = *m_atomicTargetPath;
            item = path.ResolveItem( id );
        }
        else
            item = hierarchy.ResolveItem( id, &path, true );

        if( !item )
            continue;

        if( !s_allowedTypes.contains( item->Type() ) )
        {
            status = ItemDeletionStatus::IDS_IMMUTABLE;
            continue;
        }

        if( item->Type() == SCH_GROUP_T )
        {
            auto* group = static_cast<SCH_GROUP*>( item );
            if( group->IsLocked() || ( group->GetParentGroup() && group->GetParentGroup()->AsEdaItem()->IsLocked() )
                    || std::ranges::any_of( group->GetItems(), []( EDA_ITEM* member ) { return member->IsLocked(); } ) )
            {
                status = ItemDeletionStatus::IDS_IMMUTABLE;
                continue;
            }
            // Preserve each surviving member's ownership in the native undo
            // wrapper. Group deletion must not imply deletion of its contents.
            for( EDA_ITEM* member : group->GetItems() )
            {
                if( ( commit->GetStatus( member, path.LastScreen() ) & CHT_TYPE ) != CHT_REMOVE )
                    commit->Modify( member, path.LastScreen() );
            }
            if( EDA_GROUP* parent = group->GetParentGroup() )
            {
                if( ( commit->GetStatus( parent->AsEdaItem(), path.LastScreen() ) & CHT_TYPE ) != CHT_REMOVE )
                    commit->Modify( parent->AsEdaItem(), path.LastScreen() );
            }
        }

        commit->Remove( item, path.LastScreen() );
        if( item->Type() == SCH_GROUP_T )
        {
            // Keep the original member list for the deletion's undo wrapper,
            // but release live ownership now so later operations in the same
            // batch may attach surviving members to another group.
            auto* group = static_cast<SCH_GROUP*>( item );
            for( EDA_ITEM* member : group->GetItems() )
                if( member->GetParentGroup() == group ) member->SetParentGroup( nullptr );
        }
        status = ItemDeletionStatus::IDS_OK;
    }

    if( !m_activeClients.contains( aClientName ) )
        pushCurrentCommit( aClientName, _( "Deleted items via API" ) );
}


std::optional<EDA_ITEM*> API_HANDLER_SCH::getItemFromDocument( const DocumentSpecifier& aDocument, const KIID& aId )
{
    if( !validateDocument( aDocument ) )
        return std::nullopt;

    SCH_ITEM* item = schematic()->Hierarchy().ResolveItem( aId, nullptr, true );

    if( !item)
        return std::nullopt;

    return item;
}


std::optional<TITLE_BLOCK*> API_HANDLER_SCH::getTitleBlock()
{
    wxCHECK( m_context->GetCurrentSheet(), std::nullopt );
    return &m_context->GetCurrentSheet()->LastScreen()->GetTitleBlock();
}


std::optional<PAGE_INFO> API_HANDLER_SCH::getPageSettings()
{
    wxCHECK( m_context->GetCurrentSheet(), std::nullopt );
    return m_context->GetCurrentSheet()->LastScreen()->GetPageSettings();
}


bool API_HANDLER_SCH::setPageSettings( const PAGE_INFO& aPageInfo )
{
    wxCHECK( m_context->GetCurrentSheet(), false );
    m_context->GetCurrentSheet()->LastScreen()->SetPageSettings( aPageInfo );
    return true;
}


wxString API_HANDLER_SCH::getDrawingSheetFileName()
{
    return BASE_SCREEN::m_DrawingSheetFileName;
}


void API_HANDLER_SCH::setDrawingSheetFileName( const wxString& aFileName )
{
    BASE_SCREEN::m_DrawingSheetFileName = aFileName;
    schematic()->Settings().m_SchDrawingSheetFileName = aFileName;

    if( m_frame )
        m_frame->LoadDrawingSheet();
}


void API_HANDLER_SCH::onModified()
{
    if( m_frame )
    {
        m_frame->Refresh();
        m_frame->OnModify();
    }
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportSvg(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportSvg>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    auto plotJob = std::make_unique<JOB_EXPORT_SCH_PLOT_SVG>();
    plotJob->m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        plotJob->SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    const kiapi::schematic::jobs::SchematicPlotSettings& settings = aCtx.Request.plot_settings();

    plotJob->m_drawingSheet = wxString::FromUTF8( settings.drawing_sheet() );
    plotJob->m_defaultFont = wxString::FromUTF8( settings.default_font() );
    plotJob->m_variant = wxString::FromUTF8( settings.variant() );
    plotJob->m_plotAll = settings.plot_all();
    plotJob->m_plotDrawingSheet = settings.plot_drawing_sheet();
    plotJob->m_show_hop_over = settings.show_hop_over();
    plotJob->m_blackAndWhite = settings.black_and_white();
    plotJob->m_useBackgroundColor = settings.use_background_color();
    plotJob->m_minPenWidth = settings.min_pen_width();
    plotJob->m_theme = wxString::FromUTF8( settings.theme() );

    plotJob->m_plotPages.clear();

    for( const std::string& page : settings.plot_pages() )
        plotJob->m_plotPages.push_back( wxString::FromUTF8( page ) );

    if( aCtx.Request.plot_settings().page_size() != kiapi::schematic::jobs::SchematicJobPageSize::SJPS_UNKNOWN )
    {
        plotJob->m_pageSizeSelect = FromProtoEnum<JOB_PAGE_SIZE>( aCtx.Request.plot_settings().page_size() );
    }

    return ExecuteSchematicJob( m_context->GetKiway(), *plotJob );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportDxf(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportDxf>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    auto plotJob = std::make_unique<JOB_EXPORT_SCH_PLOT_DXF>();
    plotJob->m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        plotJob->SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    const kiapi::schematic::jobs::SchematicPlotSettings& settings = aCtx.Request.plot_settings();

    plotJob->m_drawingSheet = wxString::FromUTF8( settings.drawing_sheet() );
    plotJob->m_defaultFont = wxString::FromUTF8( settings.default_font() );
    plotJob->m_variant = wxString::FromUTF8( settings.variant() );
    plotJob->m_plotAll = settings.plot_all();
    plotJob->m_plotDrawingSheet = settings.plot_drawing_sheet();
    plotJob->m_show_hop_over = settings.show_hop_over();
    plotJob->m_blackAndWhite = settings.black_and_white();
    plotJob->m_useBackgroundColor = settings.use_background_color();
    plotJob->m_minPenWidth = settings.min_pen_width();
    plotJob->m_theme = wxString::FromUTF8( settings.theme() );

    plotJob->m_plotPages.clear();

    for( const std::string& page : settings.plot_pages() )
        plotJob->m_plotPages.push_back( wxString::FromUTF8( page ) );

    if( aCtx.Request.plot_settings().page_size() != kiapi::schematic::jobs::SchematicJobPageSize::SJPS_UNKNOWN )
    {
        plotJob->m_pageSizeSelect = FromProtoEnum<JOB_PAGE_SIZE>( aCtx.Request.plot_settings().page_size() );
    }

    return ExecuteSchematicJob( m_context->GetKiway(), *plotJob );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportPdf(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportPdf>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    auto plotJob = std::make_unique<JOB_EXPORT_SCH_PLOT_PDF>( false );
    plotJob->m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        plotJob->SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    const kiapi::schematic::jobs::SchematicPlotSettings& settings = aCtx.Request.plot_settings();

    plotJob->m_drawingSheet = wxString::FromUTF8( settings.drawing_sheet() );
    plotJob->m_defaultFont = wxString::FromUTF8( settings.default_font() );
    plotJob->m_variant = wxString::FromUTF8( settings.variant() );
    plotJob->m_plotAll = settings.plot_all();
    plotJob->m_plotDrawingSheet = settings.plot_drawing_sheet();
    plotJob->m_show_hop_over = settings.show_hop_over();
    plotJob->m_blackAndWhite = settings.black_and_white();
    plotJob->m_useBackgroundColor = settings.use_background_color();
    plotJob->m_minPenWidth = settings.min_pen_width();
    plotJob->m_theme = wxString::FromUTF8( settings.theme() );

    plotJob->m_plotPages.clear();

    for( const std::string& page : settings.plot_pages() )
        plotJob->m_plotPages.push_back( wxString::FromUTF8( page ) );

    if( aCtx.Request.plot_settings().page_size() != kiapi::schematic::jobs::SchematicJobPageSize::SJPS_UNKNOWN )
    {
        plotJob->m_pageSizeSelect = FromProtoEnum<JOB_PAGE_SIZE>( aCtx.Request.plot_settings().page_size() );
    }

    plotJob->m_PDFPropertyPopups = aCtx.Request.property_popups();
    plotJob->m_PDFHierarchicalLinks = aCtx.Request.hierarchical_links();
    plotJob->m_PDFMetadata = aCtx.Request.include_metadata();

    return ExecuteSchematicJob( m_context->GetKiway(), *plotJob );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportPs(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportPs>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    auto plotJob = std::make_unique<JOB_EXPORT_SCH_PLOT_PS>();
    plotJob->m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        plotJob->SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    const kiapi::schematic::jobs::SchematicPlotSettings& settings = aCtx.Request.plot_settings();

    plotJob->m_drawingSheet = wxString::FromUTF8( settings.drawing_sheet() );
    plotJob->m_defaultFont = wxString::FromUTF8( settings.default_font() );
    plotJob->m_variant = wxString::FromUTF8( settings.variant() );
    plotJob->m_plotAll = settings.plot_all();
    plotJob->m_plotDrawingSheet = settings.plot_drawing_sheet();
    plotJob->m_show_hop_over = settings.show_hop_over();
    plotJob->m_blackAndWhite = settings.black_and_white();
    plotJob->m_useBackgroundColor = settings.use_background_color();
    plotJob->m_minPenWidth = settings.min_pen_width();
    plotJob->m_theme = wxString::FromUTF8( settings.theme() );

    plotJob->m_plotPages.clear();

    for( const std::string& page : settings.plot_pages() )
        plotJob->m_plotPages.push_back( wxString::FromUTF8( page ) );

    if( aCtx.Request.plot_settings().page_size() != kiapi::schematic::jobs::SchematicJobPageSize::SJPS_UNKNOWN )
    {
        plotJob->m_pageSizeSelect = FromProtoEnum<JOB_PAGE_SIZE>( aCtx.Request.plot_settings().page_size() );
    }

    return ExecuteSchematicJob( m_context->GetKiway(), *plotJob );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportNetlist(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportNetlist>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    if( aCtx.Request.format() == kiapi::schematic::jobs::SchematicNetlistFormat::SNF_UNKNOWN )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "RunSchematicJobExportNetlist requires a valid format" );
        return tl::unexpected( e );
    }

    JOB_EXPORT_SCH_NETLIST netlistJob;
    netlistJob.m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        netlistJob.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    netlistJob.format = FromProtoEnum<JOB_EXPORT_SCH_NETLIST::FORMAT>( aCtx.Request.format() );

    if( !aCtx.Request.variant_name().empty() )
        netlistJob.m_variantNames.emplace_back( wxString::FromUTF8( aCtx.Request.variant_name() ) );

    return ExecuteSchematicJob( m_context->GetKiway(), netlistJob );
}


HANDLER_RESULT<types::RunJobResponse> API_HANDLER_SCH::handleRunSchematicJobExportBOM(
        const HANDLER_CONTEXT<kiapi::schematic::jobs::RunSchematicJobExportBOM>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForBusy() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.job_settings().document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    JOB_EXPORT_BOM bomJob;
    bomJob.m_filename = m_context->GetCurrentFileName();

    if( !aCtx.Request.job_settings().output_path().empty() )
        bomJob.SetConfiguredOutputPath( wxString::FromUTF8( aCtx.Request.job_settings().output_path() ) );

    bomJob.m_bomFmtPresetName = wxString::FromUTF8( aCtx.Request.format().preset_name() );
    bomJob.m_fieldDelimiter = wxString::FromUTF8( aCtx.Request.format().field_delimiter() );
    bomJob.m_stringDelimiter = wxString::FromUTF8( aCtx.Request.format().string_delimiter() );
    bomJob.m_refDelimiter = wxString::FromUTF8( aCtx.Request.format().ref_delimiter() );
    bomJob.m_refRangeDelimiter = wxString::FromUTF8( aCtx.Request.format().ref_range_delimiter() );
    bomJob.m_keepTabs = aCtx.Request.format().keep_tabs();
    bomJob.m_keepLineBreaks = aCtx.Request.format().keep_line_breaks();
    bomJob.m_includeByteOrderMark = aCtx.Request.format().include_byte_order_mark();

    bomJob.m_bomPresetName = wxString::FromUTF8( aCtx.Request.fields().preset_name() );
    bomJob.m_sortField = wxString::FromUTF8( aCtx.Request.fields().sort_field() );
    bomJob.m_filterString = wxString::FromUTF8( aCtx.Request.fields().filter() );

    switch( aCtx.Request.fields().filter_scope() )
    {
    case kiapi::schematic::jobs::BOMFilterScope::BFS_VISIBLE:
        bomJob.m_filterScope = BOM_FILTER_SCOPE::VISIBLE;
        break;

    case kiapi::schematic::jobs::BOMFilterScope::BFS_ALL:
        bomJob.m_filterScope = BOM_FILTER_SCOPE::ALL;
        break;

    case kiapi::schematic::jobs::BOMFilterScope::BFS_REFERENCE:
    default:
        bomJob.m_filterScope = BOM_FILTER_SCOPE::REFERENCE;
        break;
    }

    if( aCtx.Request.fields().sort_direction() == kiapi::schematic::jobs::BOMSortDirection::BSD_ASCENDING )
    {
        bomJob.m_sortAsc = true;
    }
    else if( aCtx.Request.fields().sort_direction() == kiapi::schematic::jobs::BOMSortDirection::BSD_DESCENDING )
    {
        bomJob.m_sortAsc = false;
    }

    for( const kiapi::schematic::jobs::BOMField& field : aCtx.Request.fields().fields() )
    {
        bomJob.m_fieldsOrdered.emplace_back( wxString::FromUTF8( field.name() ) );
        bomJob.m_fieldsLabels.emplace_back( wxString::FromUTF8( field.label() ) );

        if( field.group_by() )
            bomJob.m_fieldsGroupBy.emplace_back( wxString::FromUTF8( field.name() ) );
    }

    bomJob.m_excludeDNP = aCtx.Request.exclude_dnp();
    bomJob.m_groupSymbols = aCtx.Request.group_symbols();

    if( !aCtx.Request.variant_name().empty() )
        bomJob.m_variantNames.emplace_back( wxString::FromUTF8( aCtx.Request.variant_name() ) );

    return ExecuteSchematicJob( m_context->GetKiway(), bomJob );
}


void API_HANDLER_SCH::packSheetInstance( kiapi::schematic::types::SheetInstance* aInstance, SCH_SHEET_PATH& aPath,
                                          SCH_SHEET* aSheet )
{
    aPath.push_back( aSheet );

    PackSheetPath( *aInstance->mutable_path(), aPath.Path() );

    wxString sheetName = aSheet->GetShownName( false );

    if( sheetName.IsEmpty() && aSheet->GetScreen() )
    {
        wxFileName fn( aSheet->GetScreen()->GetFileName() );
        sheetName = fn.GetName();
    }

    aInstance->set_name( sheetName.ToUTF8() );
    aInstance->set_filename( aSheet->GetFileName().ToUTF8() );
    aInstance->set_page_number( aPath.GetPageNumber().ToUTF8() );

    if( aSheet->GetScreen() )
    {
        std::vector<SCH_ITEM*> childSheets;
        aSheet->GetScreen()->GetSheets( &childSheets );

        std::ranges::sort( childSheets,
                           [&]( SCH_ITEM* a, SCH_ITEM* b )
                           {
                               SCH_SHEET_PATH pathA = aPath;
                               pathA.push_back( static_cast<SCH_SHEET*>( a ) );

                               SCH_SHEET_PATH pathB = aPath;
                               pathB.push_back( static_cast<SCH_SHEET*>( b ) );

                               return pathA.ComparePageNum( pathB ) < 0;
                           } );

        for( SCH_ITEM* childItem : childSheets )
        {
            SCH_SHEET* childSheet = static_cast<SCH_SHEET*>( childItem );
            kiapi::schematic::types::SheetInstance* childInstance = aInstance->add_children();
            packSheetInstance( childInstance, aPath, childSheet );
        }
    }

    aPath.pop_back();
}


HANDLER_RESULT<kiapi::schematic::commands::SchematicHierarchyResponse> API_HANDLER_SCH::handleGetSchematicHierarchy(
        const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicHierarchy>& aCtx )
{
    if( auto busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    kiapi::schematic::commands::SchematicHierarchyResponse response;
    response.mutable_document()->CopyFrom( aCtx.Request.document() );

    if( !schematic()->HasHierarchy() )
        schematic()->RefreshHierarchy();

    SCH_SHEET_PATH path;
    std::vector<SCH_SHEET*> topLevelSheets = schematic()->GetTopLevelSheets();

    std::ranges::sort( topLevelSheets,
               [&]( SCH_SHEET* a, SCH_SHEET* b )
               {
                   SCH_SHEET_PATH pathA;
                   pathA.push_back( a );

                   SCH_SHEET_PATH pathB;
                   pathB.push_back( b );

                   return pathA.ComparePageNum( pathB ) < 0;
               } );

    for( SCH_SHEET* topSheet : topLevelSheets )
    {
        kiapi::schematic::types::SheetInstance* instance = response.add_top_level_sheets();
        packSheetInstance( instance, path, topSheet );
    }

    return response;
}


HANDLER_RESULT<kiapi::schematic::commands::SchematicNetlistResponse>
API_HANDLER_SCH::handleGetSchematicNetlist( const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicNetlist>& aCtx )
{
    if( std::optional<ApiResponseStatus> busy = checkForStableObservation() )
        return tl::unexpected( *busy );

    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    std::vector<KICAD_T> types = parseRequestedItemTypes( aCtx.Request.types() );
    const bool filterByType = aCtx.Request.types_size() > 0;

    if( filterByType && types.empty() )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "none of the requested types are valid for a Schematic object" );
        return tl::unexpected( e );
    }

    std::set<KICAD_T> typeFilter( types.begin(), types.end() );

    CONNECTION_GRAPH* connectionGraph = schematic()->ConnectionGraph();

    if( !connectionGraph )
    {
        ApiResponseStatus e;
        e.set_status( ApiStatusCode::AS_BAD_REQUEST );
        e.set_error_message( "schematic has no connection graph" );
        return tl::unexpected( e );
    }

    kiapi::schematic::commands::SchematicNetlistResponse response;
    response.mutable_document()->CopyFrom( aCtx.Request.document() );

    for( const auto& [key, subgraphList] : connectionGraph->GetNetMap() )
    {
        if( subgraphList.empty() )
            continue;

        CONNECTION_SUBGRAPH* firstSubgraph = subgraphList[0];

        if( firstSubgraph->GetDriverConnection() && firstSubgraph->GetDriverConnection()->IsBus() )
            continue;

        if( firstSubgraph->GetDriverPriority() < CONNECTION_SUBGRAPH::PRIORITY::PIN )
            continue;

        kiapi::schematic::types::SchematicNet* net = response.add_nets();
        net->set_name( key.Name.ToUTF8() );

        for( CONNECTION_SUBGRAPH* subGraph : subgraphList )
        {
            kiapi::schematic::types::SchematicNetSheetContents* sheetContents = net->add_sheets();
            PackSheetPath( *sheetContents->mutable_path(), subGraph->GetSheet().Path() );

            for( SCH_ITEM* item : subGraph->GetItems() )
            {
                if( filterByType && !typeFilter.contains( item->Type() ) )
                    continue;

                sheetContents->add_items()->set_value( item->m_Uuid.AsStdString() );
            }
        }
    }

    return response;
}


// TODO(JE) factor out
HANDLER_RESULT<CrossProbeAnnounceResponse> API_HANDLER_SCH::handleCrossProbeAnnounce(
        const HANDLER_CONTEXT<CrossProbeAnnounce>& aCtx )
{
    wxLogTrace( traceApi, "Received announce from frame %d at %s",
                aCtx.Request.frame_type(), aCtx.Request.socket_path() );

    CROSS_PROBE_CLIENT::RegisterPeer( static_cast<FRAME_T>( aCtx.Request.frame_type() ),
                                      aCtx.Request.socket_path() );

    CrossProbeAnnounceResponse response;
    response.set_status( CPS_OK );
    return response;
}


bool findSymbolsAndPins( const SCH_SHEET_LIST& aSchematicSheetList, const SCH_SHEET_PATH& aSheetPath,
                         std::unordered_map<wxString, std::vector<SCH_REFERENCE>>&             aSyncSymMap,
                         std::unordered_map<wxString, std::unordered_map<wxString, SCH_PIN*>>& aSyncPinMap,
                         const wxString& aVariantName = wxEmptyString, bool aRecursive = false )
{
    if( aRecursive )
    {
        // Iterate over children
        for( const SCH_SHEET_PATH& candidate : aSchematicSheetList )
        {
            if( candidate == aSheetPath || !candidate.IsContainedWithin( aSheetPath ) )
                continue;

            findSymbolsAndPins( aSchematicSheetList, candidate, aSyncSymMap, aSyncPinMap, aVariantName, aRecursive );
        }
    }

    SCH_REFERENCE_LIST references;

    aSheetPath.GetSymbols( references, SYMBOL_FILTER_NON_POWER, true );

    for( unsigned ii = 0; ii < references.GetCount(); ii++ )
    {
        SCH_REFERENCE& schRef = references[ii];

        if( schRef.IsSplitNeeded() )
            schRef.Split();

        SCH_SYMBOL* symbol = schRef.GetSymbol();
        wxString    refNum = schRef.GetRefNumber();
        wxString    fullRef = schRef.GetRef() + refNum;

        // Skip power symbols
        if( fullRef.StartsWith( wxS( "#" ) ) )
            continue;

        // Unannotated symbols are not supported
        if( refNum.compare( wxS( "?" ) ) == 0 )
            continue;

        // Look for whole footprint
        auto symMatchIt = aSyncSymMap.find( fullRef );

        if( symMatchIt != aSyncSymMap.end() )
        {
            symMatchIt->second.emplace_back( schRef );

            // Whole footprint was selected, no need to select pins
            continue;
        }

        // Look for pins
        auto symPinMatchIt = aSyncPinMap.find( fullRef );

        if( symPinMatchIt != aSyncPinMap.end() )
        {
            std::unordered_map<wxString, SCH_PIN*>& pinMap = symPinMatchIt->second;
            std::vector<SCH_PIN*>                   pinsOnSheet = symbol->GetPins( &aSheetPath );

            for( SCH_PIN* pin : pinsOnSheet )
            {
                int pinUnit = pin->GetLibPin()->GetUnit();

                if( pinUnit > 0 && pinUnit != schRef.GetUnit() )
                    continue;

                // Reverse-map the requested pad back to the owning pin (issue #2282).  A pin may
                // resolve to several pads via the map; match the first that pcbnew asked for.
                for( const wxString& pad :
                     ExpandStackedPinNotation( pin->GetEffectivePadNumber( aSheetPath, aVariantName ) ) )
                {
                    auto pinIt = pinMap.find( pad );

                    if( pinIt != pinMap.end() )
                    {
                        pinIt->second = pin;
                        break;
                    }
                }
            }
        }
    }

    return false;
}


bool sheetContainsOnlyWantedItems(
        const SCH_SHEET_LIST& aSchematicSheetList, const SCH_SHEET_PATH& aSheetPath,
        std::unordered_map<wxString, std::vector<SCH_REFERENCE>>&             aSyncSymMap,
        std::unordered_map<wxString, std::unordered_map<wxString, SCH_PIN*>>& aSyncPinMap,
        std::unordered_map<SCH_SHEET_PATH, bool>&                             aCache )
{
    auto cacheIt = aCache.find( aSheetPath );

    if( cacheIt != aCache.end() )
        return cacheIt->second;

    // Iterate over children
    for( const SCH_SHEET_PATH& candidate : aSchematicSheetList )
    {
        if( candidate == aSheetPath || !candidate.IsContainedWithin( aSheetPath ) )
            continue;

        bool childRet = sheetContainsOnlyWantedItems( aSchematicSheetList, candidate, aSyncSymMap,
                                                      aSyncPinMap, aCache );

        if( !childRet )
        {
            aCache.emplace( aSheetPath, false );
            return false;
        }
    }

    SCH_REFERENCE_LIST references;
    aSheetPath.GetSymbols( references, SYMBOL_FILTER_NON_POWER, true );

    if( references.GetCount() == 0 )    // Empty sheet, obviously do not contain wanted items
    {
        aCache.emplace( aSheetPath, false );
        return false;
    }

    for( unsigned ii = 0; ii < references.GetCount(); ii++ )
    {
        SCH_REFERENCE& schRef = references[ii];

        if( schRef.IsSplitNeeded() )
            schRef.Split();

        wxString refNum = schRef.GetRefNumber();
        wxString fullRef = schRef.GetRef() + refNum;

        // Skip power symbols
        if( fullRef.StartsWith( wxS( "#" ) ) )
            continue;

        // Unannotated symbols are not supported
        if( refNum.compare( wxS( "?" ) ) == 0 )
            continue;

        if( aSyncSymMap.find( fullRef ) == aSyncSymMap.end() )
        {
            aCache.emplace( aSheetPath, false );
            return false; // Some symbol is not wanted.
        }

        if( aSyncPinMap.find( fullRef ) != aSyncPinMap.end() )
        {
            aCache.emplace( aSheetPath, false );
            return false; // Looking for specific pins, so can't be mapped
        }
    }

    aCache.emplace( aSheetPath, true );
    return true;
}


std::optional<std::tuple<SCH_SHEET_PATH, SCH_ITEM*, std::vector<SCH_ITEM*>>>
findItemsFromSyncSelection( const SCHEMATIC& aSchematic,
                            const kiapi::common::commands::SyncSelection& aSync )
{
    std::unordered_map<wxString, std::vector<SCH_REFERENCE>>             syncSymMap;
    std::unordered_map<wxString, std::unordered_map<wxString, SCH_PIN*>> syncPinMap;
    std::unordered_map<SCH_SHEET_PATH, bool>                             fullyWantedCache;

    std::optional<wxString>                                    focusSymbol;
    std::optional<std::pair<wxString, wxString>>               focusPin;
    std::unordered_map<SCH_SHEET_PATH, std::vector<SCH_ITEM*>> focusItemResults;

    const SCH_SHEET_LIST allSheetsList = aSchematic.Hierarchy();

    // In orderedSheets, the current sheet comes first.
    std::vector<SCH_SHEET_PATH> orderedSheets;
    orderedSheets.reserve( allSheetsList.size() );
    orderedSheets.push_back( aSchematic.CurrentSheet() );

    for( const SCH_SHEET_PATH& sheetPath : allSheetsList )
    {
        if( sheetPath != aSchematic.CurrentSheet() )
            orderedSheets.push_back( sheetPath );
    }

    const bool focusOnFirst = ( aSync.mode() == kiapi::common::commands::SSM_ITEMS_AND_NETS ) && aSync.has_focus_item();

    for( const kiapi::common::commands::SelectionSpec& spec : aSync.items() )
    {
        switch( spec.spec_case() )
        {
        case kiapi::common::commands::SelectionSpec::kFootprint:
        {
            wxString symRef = wxString::FromUTF8( spec.footprint().reference() );
            syncSymMap[symRef] = std::vector<SCH_REFERENCE>();
            break;
        }

        case kiapi::common::commands::SelectionSpec::kPad:
        {
            wxString symRef = wxString::FromUTF8( spec.pad().reference() );
            wxString padNum = wxString::FromUTF8( spec.pad().number() );
            syncPinMap[symRef][padNum] = nullptr;
            break;
        }

        default:
            break;
        }
    }

    if( focusOnFirst )
    {
        const kiapi::common::commands::SelectionSpec& focusSpec = aSync.focus_item();

        if( focusSpec.has_footprint() )
            focusSymbol = wxString::FromUTF8( focusSpec.footprint().reference() );
        else if( focusSpec.has_pad() )
            focusPin = std::make_pair( wxString::FromUTF8( focusSpec.pad().reference() ),
                                       wxString::FromUTF8( focusSpec.pad().number() ) );
    }

    // Lambda definitions
    auto flattenSyncMaps =
            [&syncSymMap, &syncPinMap]() -> std::vector<SCH_ITEM*>
            {
                std::vector<SCH_ITEM*> allVec;

                for( const auto& [symRef, symbols] : syncSymMap )
                {
                    for( const SCH_REFERENCE& ref : symbols )
                        allVec.push_back( ref.GetSymbol() );
                }

                for( const auto& [symRef, pinMap] : syncPinMap )
                {
                    for( const auto& [padNum, pin] : pinMap )
                    {
                        if( pin )
                            allVec.push_back( pin );
                    }
                }

                return allVec;
            };

    auto clearSyncMaps =
            [&syncSymMap, &syncPinMap]()
            {
                for( auto& [symRef, symbols] : syncSymMap )
                    symbols.clear();

                for( auto& [reference, pins] : syncPinMap )
                {
                    for( auto& [number, pin] : pins )
                        pin = nullptr;
                }
            };

    auto syncMapsValuesEmpty =
            [&syncSymMap, &syncPinMap]() -> bool
            {
                for( const auto& [symRef, symbols] : syncSymMap )
                {
                    if( symbols.size() > 0 )
                        return false;
                }

                for( const auto& [symRef, pins] : syncPinMap )
                {
                    for( const auto& [padNum, pin] : pins )
                    {
                        if( pin )
                            return false;
                    }
                }

                return true;
            };

    auto checkFocusItems =
            [&]( const SCH_SHEET_PATH& aSheet )
            {
                if( focusSymbol )
                {
                    auto findIt = syncSymMap.find( *focusSymbol );

                    if( findIt != syncSymMap.end() )
                    {
                        if( findIt->second.size() > 0 )
                            focusItemResults[aSheet].push_back( findIt->second.front().GetSymbol() );
                    }
                }
                else if( focusPin )
                {
                    auto findIt = syncPinMap.find( focusPin->first );

                    if( findIt != syncPinMap.end() )
                    {
                        if( findIt->second[focusPin->second] )
                            focusItemResults[aSheet].push_back( findIt->second[focusPin->second] );
                    }
                }
            };

    auto makeRetForSheet =
            [&]( const SCH_SHEET_PATH& aSheet, SCH_ITEM* aFocusItem )
            {
                clearSyncMaps();

                // Fill sync maps
                findSymbolsAndPins( allSheetsList, aSheet, syncSymMap, syncPinMap, aSchematic.GetCurrentVariant() );
                std::vector<SCH_ITEM*> itemsVector = flattenSyncMaps();

                // Add fully wanted sheets to vector
                for( SCH_ITEM* item : aSheet.LastScreen()->Items().OfType( SCH_SHEET_T ) )
                {
                    KIID_PATH kiidPath = aSheet.Path();
                    kiidPath.push_back( item->m_Uuid );

                    std::optional<SCH_SHEET_PATH> subsheetPath =
                            allSheetsList.GetSheetPathByKIIDPath( kiidPath );

                    if( !subsheetPath )
                        continue;

                    if( sheetContainsOnlyWantedItems( allSheetsList, *subsheetPath, syncSymMap,
                                                      syncPinMap, fullyWantedCache ) )
                    {
                        itemsVector.push_back( item );
                    }
                }

                return std::make_tuple( aSheet, aFocusItem, itemsVector );
            };

    if( focusOnFirst )
    {
        for( const SCH_SHEET_PATH& sheetPath : orderedSheets )
        {
            clearSyncMaps();

            findSymbolsAndPins( allSheetsList, sheetPath, syncSymMap, syncPinMap, aSchematic.GetCurrentVariant() );

            checkFocusItems( sheetPath );
        }

        if( focusItemResults.size() > 0 )
        {
            for( const SCH_SHEET_PATH& sheetPath : orderedSheets )
            {
                const std::vector<SCH_ITEM*>& items = focusItemResults[sheetPath];

                if( !items.empty() )
                    return makeRetForSheet( sheetPath, items.front() );
            }
        }
    }
    else
    {
        for( const SCH_SHEET_PATH& sheetPath : orderedSheets )
        {
            clearSyncMaps();

            findSymbolsAndPins( allSheetsList, sheetPath, syncSymMap, syncPinMap, aSchematic.GetCurrentVariant() );

            if( !syncMapsValuesEmpty() )
            {
                // Something found on sheet
                return makeRetForSheet( sheetPath, nullptr );
            }
        }
    }

    return std::nullopt;
}


HANDLER_RESULT<SyncSelectionResponse> API_HANDLER_SCH::handleSyncSelection(
        const HANDLER_CONTEXT<SyncSelection>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "SyncSelection" ) )
        return tl::unexpected( *headless );

    SyncSelectionResponse response;

    const CROSS_PROBING_SETTINGS& settings = m_frame->eeconfig()->m_CrossProbing;

    if( !settings.on_selection && aCtx.Request.context() != SyncSelectionContext::SSC_EXPLICIT )
    {
        response.set_status( CPS_DISABLED );
        response.set_message( "implicit selection sync disabled by user" );
        return response;
    }

    // A request carrying no items asks for nothing to be selected, so there is nothing to find.
    if( aCtx.Request.items_size() == 0 )
    {
        m_frame->SetSyncingSelection( true ); // recursion guard

        m_frame->GetToolManager()->GetTool<SCH_SELECTION_TOOL>()->SyncSelection( std::nullopt, nullptr, {} );

        m_frame->SetSyncingSelection( false );

        response.set_status( CPS_OK );
        return response;
    }

    std::optional<std::tuple<SCH_SHEET_PATH, SCH_ITEM*, std::vector<SCH_ITEM*>>> findRet =
                    findItemsFromSyncSelection( *schematic(), aCtx.Request );

    if( findRet )
    {
        auto& [sheetPath, focusItem, items] = *findRet;

        m_frame->SetSyncingSelection( true ); // recursion guard

        m_frame->GetToolManager()->GetTool<SCH_SELECTION_TOOL>()->SyncSelection( sheetPath, focusItem, items );

        m_frame->SetSyncingSelection( false );

        if( m_frame->eeconfig()->m_CrossProbing.flash_selection )
        {
            wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE): flash enabled, items=%zu",
                        items.size() );

            if( items.empty() )
            {
                wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE): nothing to flash" );
            }
            else
            {
                std::vector<SCH_ITEM*> itemPtrs;
                std::copy( items.begin(), items.end(), std::back_inserter( itemPtrs ) );

                m_frame->StartCrossProbeFlash( itemPtrs );
            }
        }
        else
        {
            wxLogTrace( traceCrossProbeFlash, "MAIL_SELECTION(_FORCE): flash disabled" );
        }
    }

    response.set_status( CPS_OK );
    return response;
}


HANDLER_RESULT<HighlightNetsResponse> API_HANDLER_SCH::handleHighlightNets(
        const HANDLER_CONTEXT<HighlightNets>& aCtx )
{
    if( std::optional<ApiResponseStatus> headless = checkForHeadless( "HighlightNets" ) )
        return tl::unexpected( *headless );

    HighlightNetsResponse response;
    CROSS_PROBING_SETTINGS& crossProbingSettings = m_frame->eeconfig()->m_CrossProbing;

    if( aCtx.ClientName == StandaloneCrossProbeClientName
        || aCtx.ClientName == KiwayClientName )
    {
        if( !crossProbingSettings.auto_highlight )
        {
            response.set_status( CPS_DISABLED );
            return response;
        }
    }

    wxString net;

    if( aCtx.Request.net_name_size() > 0 )
        net = wxString::FromUTF8( aCtx.Request.net_name( 0 ) );

    m_frame->HandleRemoteNetHighlight( net );

    response.set_status( CPS_OK );
    return response;
}



HANDLER_RESULT<kiapi::schematic::commands::SchematicVariantsResponse>
API_HANDLER_SCH::handleGetSchematicVariants(
        const HANDLER_CONTEXT<kiapi::schematic::commands::GetSchematicVariants>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    kiapi::schematic::commands::SchematicVariantsResponse response;
    response.mutable_document()->CopyFrom( aCtx.Request.document() );

    for( const wxString& name : schematic()->GetVariantNames() )
    {
        kiapi::schematic::commands::SchematicVariant* variant = response.add_variants();
        variant->set_name( name.ToUTF8() );
        variant->set_description( schematic()->GetVariantDescription( name ).ToUTF8() );
    }

    response.set_current_variant( schematic()->GetCurrentVariant().ToUTF8() );

    return response;
}


HANDLER_RESULT<ExpandTextVariablesResponse>
API_HANDLER_SCH::handleExpandTextVariables( const HANDLER_CONTEXT<ExpandTextVariables>& aCtx )
{
    HANDLER_RESULT<bool> documentValidation = validateDocument( aCtx.Request.document() );

    if( !documentValidation )
        return tl::unexpected( documentValidation.error() );

    SCH_SHEET_PATH path = m_context->GetCurrentSheet().value_or( *schematic()->Hierarchy().begin() );

    if( aCtx.Request.document().has_sheet_path() )
    {
        KIID_PATH kiidPath = UnpackSheetPath( aCtx.Request.document().sheet_path() );

        if( std::optional<SCH_SHEET_PATH> resolvedPath = schematic()->Hierarchy().GetSheetPathByKIIDPath( kiidPath ) )
        {
            path = *resolvedPath;
        }
    }

    ExpandTextVariablesResponse reply;

    std::function<bool( wxString* )> textResolver =
        [&]( wxString* token ) -> bool
        {
            return schematic()->ResolveTextVar( &path, token, 0 );
        };

    PROJECT& project = m_context->Prj();

    for( const std::string& textMsg : aCtx.Request.text() )
    {
        wxString text = ExpandTextVars( wxString::FromUTF8( textMsg ), &textResolver );

        if( aCtx.Request.expand_env_vars() )
            text = ExpandEnvVarSubstitutions( text, &project );

        reply.add_text( text.ToUTF8() );
    }

    return reply;
}
