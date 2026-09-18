/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include <api/api_handler_sch.h>
#include <api/api_sch_utils.h>
#include <api/api_sch_field_text_modes.h>
#include <api/api_utils.h>
#include <api/sch_text_presentation.h>
#include <lib_symbol.h>
#include <sch_draw_panel.h>
#include <sch_edit_frame.h>
#include <sch_field.h>
#include <sch_label.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_pin.h>
#include <sch_symbol.h>
#include <sch_table.h>
#include <sch_tablecell.h>
#include <sch_textbox.h>
#include <schematic.h>
#include <view/view.h>
#include <trigo.h>
#include <algorithm>
#include <memory>
#include <limits>
#include <set>
#include <stdexcept>

namespace
{
BOX2I measure( SCH_ITEM& item, const SCH_SHEET_PATH& path, const wxString& variant,
               const SCH_RENDER_SETTINGS& settings )
{
    // The caller owns private copies. Explicit field/text contexts must not
    // borrow the human editor's CurrentSheet() for a repeated instance.
    if( auto* symbol = dynamic_cast<SCH_SYMBOL*>( &item ) )
    {
        const LIB_SYMBOL* definition = symbol->GetEffectiveLibSymbol( &path );
        if( !definition ) throw std::runtime_error( "A symbol has no resolved native definition" );
        BOX2I bounds = definition->GetBodyBoundingBox( symbol->GetUnitSelection( &path ),
                                                      symbol->GetBodyStyle(), true, false );
        bounds = symbol->GetTransform().TransformCoordinate( bounds );
        bounds.Normalize(); bounds.Offset( symbol->GetPosition() );
        for( const SCH_FIELD& field : symbol->GetFields() )
            if( field.IsVisible() ) bounds.Merge( field.GetBoundingBox( &path, variant ) );
        return bounds;
    }
    if( auto* sheet = dynamic_cast<SCH_SHEET*>( &item ) )
    {
        BOX2I bounds = sheet->GetBodyBoundingBox();
        for( const SCH_FIELD& field : sheet->GetFields() )
            if( field.IsVisible() ) bounds.Merge( field.GetBoundingBox( &path, variant ) );
        for( SCH_SHEET_PIN* pin : sheet->GetPins() ) bounds.Merge( measure( *pin, path, variant, settings ) );
        return bounds;
    }
    if( auto* table = dynamic_cast<SCH_TABLE*>( &item ) )
    {
        BOX2I bounds = table->GetBoundingBox();
        for( SCH_TABLECELL* cell : table->GetCells() )
            if( cell->GetColSpan() > 0 && cell->GetRowSpan() > 0 ) bounds.Merge( measure( *cell, path, variant, settings ) );
        return bounds;
    }
    if( auto* box = dynamic_cast<SCH_TEXTBOX*>( &item ) )
    {
        box->SetText( box->GetShownText( nullptr, &path, true ) );
        BOX2I bounds = box->GetBoundingBox();
        const BOX2I text = box->GetTextBox( nullptr );
        VECTOR2I corners[] = { text.GetOrigin(), VECTOR2I( text.GetRight(), text.GetTop() ),
                              text.GetEnd(), VECTOR2I( text.GetLeft(), text.GetBottom() ) };
        for( auto point : corners )
        {
            RotatePoint( point, box->GetDrawPos(), box->GetDrawRotation() );
            bounds.Merge( point );
        }
        return bounds;
    }
    if( auto* text = dynamic_cast<SCH_TEXT*>( &item ) )
    {
        text->SetText( text->GetShownText( &path, true ) );
        if( auto* label = dynamic_cast<SCH_LABEL_BASE*>( text ) )
        {
            BOX2I bounds = label->GetBodyBoundingBox( &settings );
            for( const SCH_FIELD& field : label->GetFields() )
            {
                if( !field.IsVisible() ) continue;
                BOX2I fieldBounds = field.GetBoundingBox( &path, variant );
                if( item.Type() == SCH_LABEL_T || item.Type() == SCH_GLOBAL_LABEL_T )
                    fieldBounds.Offset( label->GetSchematicTextOffset( &settings ) );
                bounds.Merge( fieldBounds );
            }
            return bounds;
        }
        if( auto painted = SchTextPresentationBounds( *text, settings ) ) return *painted;
    }
    return item.GetBoundingBox();
}
}

HANDLER_RESULT<kiapi::automation::v1::SchematicPlacementGeometry> API_HANDLER_SCH::handleMeasurePlacement(
        const HANDLER_CONTEXT<kiapi::automation::v1::MeasureSchematicPlacement>& aCtx )
{
    using namespace kiapi::automation::v1;
    auto reject = []( const std::string& message ) -> HANDLER_RESULT<SchematicPlacementGeometry>
    {
        ApiResponseStatus error; error.set_status( ApiStatusCode::AS_BAD_REQUEST ); error.set_error_message( message );
        return tl::unexpected( error );
    };
    auto known = aCtx.Request;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aCtx.Request.ByteSizeLong() )
        return reject( "Placement measurement contains unsupported fields" );
    if( auto error = validateSnapshotSchema( aCtx.Request.schema_version() ) ) return tl::unexpected( *error );
    if( auto busy = checkForStableObservation() ) return tl::unexpected( *busy );
    if( auto valid = validateDocument( aCtx.Request.document() ); !valid ) return tl::unexpected( valid.error() );
    const auto path = resolveBatchSheet( UnpackSheetPath( aCtx.Request.document().sheet_path() ) );
    if( !path || !path->LastScreen() || !m_frame || !m_frame->GetCanvas() )
        return reject( "An explicitly loaded graphical schematic sheet is required" );
    const auto& journal = schematic()->ChangeJournal();
    if( !aCtx.Request.has_expected_revision() || aCtx.Request.expected_revision().epoch() != journal.Epoch()
            || aCtx.Request.expected_revision().sequence() != journal.Sequence() )
        return reject( "Placement geometry requires the exact observed document revision" );
    if( aCtx.Request.candidates_size() > 256 ) return reject( "Measure at most 256 symbol candidates per request" );
    auto* screen = path->LastScreen();
    auto* settings = static_cast<const SCH_RENDER_SETTINGS*>( m_frame->GetCanvas()->GetView()->GetPainter()->GetSettings() );
    const wxString variant = schematic()->GetCurrentVariant();
    SchematicPlacementGeometry result;
    result.mutable_document()->CopyFrom( aCtx.Request.document() );
    result.mutable_revision()->CopyFrom( aCtx.Request.expected_revision() );
    result.mutable_screen_id()->set_value( screen->GetUuid().AsStdString() );
    const auto& page = screen->GetPageSettings();
    PackBox2( *result.mutable_page_bounds(), BOX2I( VECTOR2I( 0, 0 ),
            VECTOR2I( page.GetWidthIU( schIUScale.IU_PER_MILS ), page.GetHeightIU( schIUScale.IU_PER_MILS ) ) ), schIUScale );
    auto append = [&]( SCH_ITEM& item, SchematicPlacementBounds* output )
    {
        output->mutable_id()->set_value( item.m_Uuid.AsStdString() );
        PackVector2( *output->mutable_anchor(), item.GetPosition(), schIUScale );
        BOX2I bounds = measure( item, *path, variant, *settings ); bounds.Normalize();
        PackBox2( *output->mutable_bounds(), bounds, schIUScale );
    };
    try
    {
        std::set<std::string> existing;
        std::vector<SCH_ITEM*> items;
        for( SCH_ITEM* item : screen->Items() )
        {
            existing.insert( item->m_Uuid.AsStdString() );
            if( item->Type() != SCH_GROUP_T && item->Type() != SCH_MARKER_T ) items.push_back( item );
        }
        std::sort( items.begin(), items.end(), []( auto* a, auto* b ) { return a->m_Uuid < b->m_Uuid; } );
        for( const auto* item : items )
        {
            auto copy = std::unique_ptr<SCH_ITEM>( static_cast<SCH_ITEM*>( item->Clone() ) );
            append( *copy, result.add_obstacles() );
        }
        std::set<std::string> ids;
        for( const auto& proposed : aCtx.Request.candidates() )
        {
            if( !KIID::SniffTest( wxString::FromUTF8( proposed.id().value() ) ) ) return reject( "Invalid candidate identity" );
            const KIID id( proposed.id().value() );
            if( id == niluuid || id.AsStdString() != proposed.id().value() || existing.contains( proposed.id().value() )
                    || !ids.insert( proposed.id().value() ).second || !proposed.has_definition()
                    || proposed.path().SerializeAsString() != aCtx.Request.document().sheet_path().SerializeAsString()
                    || !SchematicFieldTextModesArePersistable( proposed ) )
                return reject( "Candidates need distinct new identities, exact target paths and complete native definitions" );
            auto coordinate = []( int64_t value )
            {
                const int64_t quantum = schIUScale.IUToNm( 1 );
                return value % quantum == 0 && value / quantum >= std::numeric_limits<int>::min()
                        && value / quantum <= std::numeric_limits<int>::max();
            };
            if( !proposed.has_position() || !coordinate( proposed.position().x_nm() )
                    || !coordinate( proposed.position().y_nm() ) || !proposed.has_unit()
                    || proposed.unit().unit() < 1 )
                return reject( "Candidate anchors must fit the native coordinate range and 100 nm quantum, with an explicit unit" );
            SCH_SYMBOL symbol;
            if( !UnpackSymbol( &symbol, proposed ) || proposed.unit().unit() > symbol.GetUnitCount() )
                return reject( "Candidate symbol or unit cannot be decoded" );
            // Resolve project variables through the target, but never append,
            // register variants, create a commit or alter the displayed sheet.
            symbol.SetParent( screen );
            append( symbol, result.add_candidates() );
        }
    }
    catch( const std::exception& error ) { return reject( error.what() ); }
    if( journal.Epoch() != result.revision().epoch() || journal.Sequence() != result.revision().sequence() )
        return reject( "The schematic changed during placement measurement" );
    result.add_limitations( "Conservative native bounding envelopes; not an occlusion or readability certificate" );
    result.add_limitations( "Drawing-sheet border and title-block reservation must be supplied as layout constraints" );
    return result;
}
