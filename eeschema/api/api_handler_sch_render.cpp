/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <api/api_handler_sch.h>
#include <api/api_utils.h>
#include <api/sch_context.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_draw_item.h>
#include <drawing_sheet/ds_painter.h>
#include <gal/cairo/cairo_print.h>
#include <google/protobuf/util/message_differencer.h>
#include <sch_connection.h>
#include <sch_draw_panel.h>
#include <sch_edit_frame.h>
#include <sch_painter.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <schematic.h>
#include <wx/mstream.h>
#include <algorithm>
#include <cmath>
#include <map>
#include <set>

namespace
{
const std::map<int, const char*> artworkLayers = {
    { LAYER_WIRE, "wires" }, { LAYER_BUS, "buses" }, { LAYER_JUNCTION, "junctions" },
    { LAYER_LOCLABEL, "local_labels" }, { LAYER_GLOBLABEL, "global_labels" },
    { LAYER_HIERLABEL, "hierarchical_labels" }, { LAYER_PINNUM, "pin_numbers" },
    { LAYER_PINNAM, "pin_names" }, { LAYER_REFERENCEPART, "references" },
    { LAYER_VALUEPART, "values" }, { LAYER_FIELDS, "fields" },
    { LAYER_INTERSHEET_REFS, "intersheet_references" }, { LAYER_NETCLASS_REFS, "netclass_references" },
    { LAYER_RULE_AREAS, "rule_areas" }, { LAYER_DEVICE, "symbols" }, { LAYER_NOTES, "notes" },
    { LAYER_NOTES_BACKGROUND, "note_backgrounds" }, { LAYER_PIN, "pins" },
    { LAYER_SHEET, "sheets" }, { LAYER_SHEETNAME, "sheet_names" },
    { LAYER_SHEETFILENAME, "sheet_filenames" }, { LAYER_SHEETFIELDS, "sheet_fields" },
    { LAYER_SHEETLABEL, "sheet_pins" }, { LAYER_NOCONNECT, "no_connects" },
    { LAYER_DNP_MARKER, "dnp_markers" }, { LAYER_EXCLUDED_FROM_SIM, "simulation_exclusions" },
    { LAYER_SHAPES_BACKGROUND, "shape_backgrounds" }, { LAYER_DEVICE_BACKGROUND, "symbol_backgrounds" },
    { LAYER_SHEET_BACKGROUND, "sheet_backgrounds" }, { LAYER_BUS_JUNCTION, "bus_junctions" },
    { LAYER_DRAW_BITMAPS, "images" }, { LAYER_DRAWINGSHEET, "drawing_sheet" },
    { LAYER_SCHEMATIC_PAGE_LIMITS, "page_boundary" }
};

void drawPage( KIGFX::GAL& aGal, SCH_RENDER_SETTINGS& aSettings, DS_DATA_MODEL& aLayout,
               SCH_EDIT_FRAME& aFrame, const SCH_SHEET_PATH& aPath, bool aBorderOnly )
{
    SCH_SCREEN* screen = aPath.LastScreen();
    KIGFX::DS_PAINTER painter( &aGal );
    auto* settings = static_cast<KIGFX::DS_RENDER_SETTINGS*>( painter.GetSettings() );
    settings->SetNormalColor( aSettings.GetLayerColor( LAYER_SCHEMATIC_DRAWINGSHEET ) );
    settings->SetPageBorderColor( aSettings.GetLayerColor( LAYER_SCHEMATIC_PAGE_LIMITS ) );
    settings->SetDefaultFont( aSettings.GetDefaultFont() );
    if( aBorderOnly )
    {
        painter.DrawBorder( &screen->GetPageSettings(), schIUScale.IU_PER_MILS );
        return;
    }
    DS_DRAW_ITEM_LIST items( schIUScale );
    items.SetDefaultPenSize( aSettings.GetDrawingSheetLineWidth() );
    items.SetIsFirstPage( aPath.GetVirtualPageNumber() == 1 );
    items.SetPageNumber( aPath.GetPageNumber() );
    items.SetSheetCount( screen->GetPageCount() );
    items.SetFileName( screen->GetFileName() );
    items.SetSheetName( aPath.Last()->GetName() );
    items.SetSheetPath( aPath.PathHumanReadable() );
    items.SetSheetLayer( aSettings.GetLayerName() );
    auto& schematic = aFrame.Schematic();
    items.SetVariantName( schematic.GetCurrentVariant() );
    items.SetVariantDesc( schematic.GetVariantDescription( schematic.GetCurrentVariant() ) );
    items.SetProject( &schematic.Project() );
    items.SetProperties( schematic.GetProperties() );
    items.BuildDrawItemsList( screen->GetPageSettings(), screen->GetTitleBlock(), &aLayout );
    for( auto* item = items.GetFirst(); item; item = items.GetNext() )
        painter.Draw( item, LAYER_DRAWINGSHEET );
}
}

HANDLER_RESULT<kiapi::automation::v1::SchematicViewSet> API_HANDLER_SCH::handleRenderViews(
        const HANDLER_CONTEXT<kiapi::automation::v1::RenderSchematicViews>& aCtx )
{
    using namespace kiapi::automation::v1;
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) )
        return tl::unexpected( *error );
    auto reject = []( const std::string& message ) -> HANDLER_RESULT<SchematicViewSet>
    {
        ApiResponseStatus error;
        error.set_status( ApiStatusCode::AS_BAD_REQUEST );
        error.set_error_message( message );
        return tl::unexpected( error );
    };
    if( auto busy = checkForStableObservation() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid )
        return tl::unexpected( valid.error() );
    if( !aCtx.Request.document().has_sheet_path()
            || UnpackSheetPath( aCtx.Request.document().sheet_path() ).empty() )
        return reject( "An explicit loaded sheet instance is required" );
    const auto requestedPath = resolveBatchSheet( UnpackSheetPath( aCtx.Request.document().sheet_path() ) );
    if( !requestedPath || !requestedPath->LastScreen() )
        return reject( "The requested sheet instance is not loaded" );
    const SCH_SHEET_PATH path = *requestedPath;
    if( !m_frame || !m_frame->GetCanvas() ) return reject( "A loaded schematic editor is required" );
    if( aCtx.Request.views_size() < 1 || aCtx.Request.views_size() > 4 )
        return reject( "Request one to four independent views" );
    std::set<std::string> keys;
    uint64_t pixels = 0;
    constexpr double maxCoordinateNm = 100000000000.0;
    for( const auto& request : aCtx.Request.views() )
    {
        if( request.key().empty() || request.key().size() > 64 || !keys.insert( request.key() ).second )
            return reject( "Each view requires a distinct key of at most 64 bytes" );
        if( request.width_pixels() < 64 || request.height_pixels() < 64
                || request.width_pixels() > 2048 || request.height_pixels() > 2048 )
            return reject( "Each image dimension must be between 64 and 2048 pixels" );
        pixels += uint64_t( request.width_pixels() ) * request.height_pixels();
        if( pixels > 8388608 ) return reject( "The view batch exceeds 8388608 output pixels" );
        const auto& region = request.region();
        const double x = region.position().x_nm(), y = region.position().y_nm();
        const double w = region.size().x_nm(), h = region.size().y_nm();
        if( w < 100 || h < 100 || std::abs( x ) > maxCoordinateNm || std::abs( y ) > maxCoordinateNm
                || w > maxCoordinateNm || h > maxCoordinateNm
                || std::abs( x + w ) > maxCoordinateNm || std::abs( y + h ) > maxCoordinateNm )
            return reject( "A positive region within supported schematic coordinates is required" );
        std::set<int> layers;
        for( int layer : request.native_layers() )
            if( !artworkLayers.contains( layer ) || !layers.insert( layer ).second )
                return reject( "Layers must be distinct supported artwork layer IDs" );
    }

    auto snapshot = [&]() -> HANDLER_RESULT<SchematicScreenDataSnapshot>
    {
        auto data = readScreenDataForPath( path, aCtx.Request.document() );
        if( !data ) return tl::unexpected( data.error() );
        SchematicScreenDataSnapshot value;
        value.mutable_data()->Swap( &*data );
        value.mutable_revision()->set_epoch( schematic()->ChangeJournal().Epoch() );
        value.mutable_revision()->set_sequence( schematic()->ChangeJournal().Sequence() );
        value.set_tracking_complete( false );
        return value;
    };
    auto before = snapshot();
    if( !before ) return tl::unexpected( before.error() );
    const SCH_SHEET_PATH humanPath = *context()->GetCurrentSheet();
    auto* humanView = m_frame->GetCanvas()->GetView();
    const VECTOR2D humanCenter = humanView->GetCenter();
    const double humanScale = humanView->GetScale();

    // Painter paths update caches, pin flags and active URLs. Paint private
    // object copies; never attach real editor items to a second KIGFX::VIEW.
    std::map<KIID, SCH_ITEM*> originals;
    for( SCH_ITEM* item : path.LastScreen()->Items() )
    {
        originals.emplace( item->m_Uuid, item );
        item->RunOnChildren( [&]( SCH_ITEM* child ) { originals.emplace( child->m_Uuid, child ); },
                             RECURSE_MODE::NO_RECURSE );
    }
    auto prepare = [&]( SCH_ITEM* item )
    {
        item->ClearFlags();
        item->SetIsRollover( false, VECTOR2I() );
        if( auto original = originals.find( item->m_Uuid ); original != originals.end() )
            if( auto* connection = original->second->Connection( &path ) )
                item->InitializeConnection( path, schematic()->ConnectionGraph() )->Clone( *connection );
    };
    std::vector<std::unique_ptr<SCH_ITEM>> items;
    for( SCH_ITEM* item : path.LastScreen()->Items() )
    {
        if( item->Type() == SCH_GROUP_T || item->Type() == SCH_MARKER_T ) continue;
        auto copy = std::unique_ptr<SCH_ITEM>( static_cast<SCH_ITEM*>( item->Clone() ) );
        prepare( copy.get() );
        copy->RunOnChildren( prepare, RECURSE_MODE::NO_RECURSE );
        items.push_back( std::move( copy ) );
    }
    std::sort( items.begin(), items.end(), []( const auto& a, const auto& b ) { return a->m_Uuid < b->m_Uuid; } );
    SchematicViewSet result;
    for( const auto& [id, name] : artworkLayers )
    {
        auto* layer = result.add_available_layers(); layer->set_id( id ); layer->set_name( name );
    }
    result.add_limitations( "Loaded schematic sheet instances only; PCB/3D views are unfinished" );
    result.add_limitations( "Printing-style artwork excludes transient selection, hover, grid and analysis overlays" );
    result.add_limitations( "Supported snapshot coverage and native revision tracking remain incomplete" );
    try
    {
        // The normal frame builder regenerates cached items on its template.
        // Own that template and its coordinate environment for this request;
        // never swap DS_DATA_MODEL's global/alternate instance.
        auto layout = DS_DATA_MODEL::GetTheInstance().CloneForRendering();
        for( const auto& request : aCtx.Request.views() )
        {
            auto* rendered = result.add_views(); rendered->set_key( request.key() );
            auto* preview = rendered->mutable_preview();
            preview->mutable_document()->CopyFrom( aCtx.Request.document() );
            preview->mutable_revision()->CopyFrom( before->revision() );
            preview->set_width_pixels( request.width_pixels() );
            preview->set_height_pixels( request.height_pixels() );
            preview->set_tracking_complete( false );
            wxImage image( request.width_pixels(), request.height_pixels() ); image.InitAlpha();
            KIGFX::GAL_DISPLAY_OPTIONS options;
            {
                auto gal = KIGFX::CAIRO_PRINT_GAL::Create( options, &image, 96.0 );
                gal->SetWorldUnitLength( 1.0 / ( 25.4 * schIUScale.IU_PER_MM ) );
                gal->SetScreenSize( VECTOR2I( request.width_pixels(), request.height_pixels() ) );
                gal->SetNativePaperSize( VECTOR2D( request.width_pixels() / 96.0,
                                                   request.height_pixels() / 96.0 ), true );
                const auto& region = request.region();
                const double nmPerPixel = std::max( double( region.size().x_nm() ) / request.width_pixels(),
                                                    double( region.size().y_nm() ) / request.height_pixels() );
                gal->SetZoomFactor( 25400000.0 / ( 96.0 * nmPerPixel ) );
                const double nmPerIU = 1000000.0 / schIUScale.IU_PER_MM;
                gal->SetLookAtPoint( VECTOR2D( region.position().x_nm() + region.size().x_nm() / 2.0,
                                               region.position().y_nm() + region.size().y_nm() / 2.0 ) / nmPerIU );
                KIGFX::SCH_PAINTER painter( gal.get() );
                *painter.GetSettings() = *static_cast<SCH_RENDER_SETTINGS*>( humanView->GetPainter()->GetSettings() );
                painter.GetSettings()->SetIsPrinting( true );
                painter.SetSchematic( schematic() );
                painter.SetSheetPath( path );
                gal->SetClearColor( painter.GetSettings()->GetBackgroundColor() );
                gal->BeginDrawing();
                auto* viewport = preview->mutable_viewport();
                const auto& matrix = gal->GetScreenWorldMatrix();
                const VECTOR2D origin = matrix * VECTOR2D( 0, 0 );
                const VECTOR2D dx = matrix * VECTOR2D( 1, 0 ) - origin;
                const VECTOR2D dy = matrix * VECTOR2D( 0, 1 ) - origin;
                viewport->set_origin_x_nm( origin.x * nmPerIU ); viewport->set_origin_y_nm( origin.y * nmPerIU );
                viewport->set_pixel_x_dx_nm( dx.x * nmPerIU ); viewport->set_pixel_x_dy_nm( dx.y * nmPerIU );
                viewport->set_pixel_y_dx_nm( dy.x * nmPerIU ); viewport->set_pixel_y_dy_nm( dy.y * nmPerIU );
                std::vector<int> layers( request.native_layers().begin(), request.native_layers().end() );
                if( layers.empty() ) for( const auto& [id, name] : artworkLayers ) layers.push_back( id );
                humanView->SortLayers( layers );
                for( int layer : layers )
                {
                    viewport->add_visible_native_layers( layer );
                    if( layer == LAYER_DRAWINGSHEET || layer == LAYER_SCHEMATIC_PAGE_LIMITS )
                    {
                        drawPage( *gal, *painter.GetSettings(), *layout, *m_frame, path,
                                  layer == LAYER_SCHEMATIC_PAGE_LIMITS );
                        continue;
                    }
                    for( const auto& item : items )
                    {
                        const auto itemLayers = item->ViewGetLayers();
                        if( std::find( itemLayers.begin(), itemLayers.end(), layer ) != itemLayers.end() )
                            painter.Draw( item.get(), layer );
                    }
                }
                gal->EndDrawing();
            } // Destroying the private Cairo context transfers the final pixels.
            wxMemoryOutputStream stream;
            if( !image.SaveFile( stream, wxBITMAP_TYPE_PNG ) ) return reject( "Offscreen PNG encoding failed" );
            preview->mutable_png()->resize( stream.GetSize() );
            stream.CopyTo( preview->mutable_png()->data(), preview->png().size() );
        }
    }
    catch( const std::exception& error ) { return reject( std::string( "Offscreen rendering failed: " ) + error.what() ); }
    auto after = snapshot();
    if( !after ) return tl::unexpected( after.error() );
    if( !google::protobuf::util::MessageDifferencer::Equals( *before, *after )
            || *context()->GetCurrentSheet() != humanPath
            || humanView->GetCenter() != humanCenter || humanView->GetScale() != humanScale )
    {
        ApiResponseStatus error; error.set_status( ApiStatusCode::AS_NOT_READY );
        error.set_error_message( "The design or human viewport changed during offscreen rendering; retry" );
        return tl::unexpected( error );
    }
    result.mutable_snapshot()->Swap( &*after );
    projectSnapshotSchema( *result.mutable_snapshot()->mutable_data()->mutable_metadata(),
                           aCtx.Request.schema_version() );
    return result;
}
