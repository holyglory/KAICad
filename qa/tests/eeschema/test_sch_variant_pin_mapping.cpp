/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
// Exact pin identities of alternate symbol variants, and the pin facts connected
// realization measures (contract CN-1 §6.2 and §11): why a symbol's pin geometry is
// incomplete, each pin's effective electrical type, the implicit power connection the
// connection graph gives it, and the bounds a placement measurement reports for a symbol.
// Ledger p20323fd749ff825e: a De Morgan alternate body, a unit swap and each sheet instance
// report their own placed and library pins exactly, before and after save and reload; a
// replacement symbol variant, an override whose symbol is missing and pins that share a
// number are reported incomplete with their reason, never with a guessed pairing.
#include <boost/test/unit_test.hpp>
#include <api/api_sch_utils.h>
#include <api/common/commands/automation_commands.pb.h>
#include <lib_symbol.h>
#include <math/util.h>
#include <sch_field.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_pin.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <sch_symbol.h>
#include <schematic.h>
#include <settings/settings_manager.h>

#include <wx/filename.h>

#include <initializer_list>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <utility>
#include <vector>

namespace
{
using namespace kiapi::automation::v1;

void AddPin( LIB_SYMBOL& aLibrary, const wxString& aNumber, const wxString& aName, ELECTRICAL_PINTYPE aType,
             bool aVisible )
{
    auto* pin = new SCH_PIN( &aLibrary );
    pin->SetUnit( 0 );
    pin->SetBodyStyle( 0 );
    pin->SetNumber( aNumber );
    pin->SetName( aName );
    pin->SetType( aType );
    pin->SetVisible( aVisible );
    aLibrary.AddDrawItem( pin );
}


struct PLACED
{
    LIB_SYMBOL     library;
    SCH_SHEET      sheet;
    SCH_SHEET_PATH path;
    std::unique_ptr<SCH_SYMBOL> symbol;

    explicit PLACED( const wxString& aName ) : library( aName )
    {
        library.SetLibId( LIB_ID( wxS( "Automation" ), aName ) );
        path.push_back( &sheet );
    }

    void Place()
    {
        symbol = std::make_unique<SCH_SYMBOL>( library, library.GetLibId(), &path, 1, 1 );
    }

    std::map<std::string, SchematicPinAnchor> Measure( SchematicSymbolPinGeometry& aOutput )
    {
        PackSchematicPinGeometry( *symbol, path, wxEmptyString, aOutput );
        BOOST_REQUIRE( aOutput.complete() );
        BOOST_CHECK_EQUAL( aOutput.incomplete_reason(), SPGIR_UNSPECIFIED );
        std::map<std::string, SchematicPinAnchor> result;
        for( const SchematicPinAnchor& pin : aOutput.pins() )
            result[pin.number()] = pin;
        return result;
    }
};


VECTOR2I Mm( double aX, double aY )
{
    return VECTOR2I( schIUScale.mmToIU( aX ), schIUScale.mmToIU( aY ) );
}


/// A library pin of @a aUnit and body style @a aStyle (0 is common) at a local position, pointing from its
/// connection point towards the body.
SCH_PIN* AddBodyPin( LIB_SYMBOL& aLibrary, int aUnit, int aStyle, const wxString& aNumber, const wxString& aName,
                     const VECTOR2I& aPosition, PIN_ORIENTATION aOrientation,
                     ELECTRICAL_PINTYPE aType = ELECTRICAL_PINTYPE::PT_PASSIVE )
{
    auto* pin = new SCH_PIN( &aLibrary );
    pin->SetUnit( aUnit );
    pin->SetBodyStyle( aStyle );
    pin->SetNumber( aNumber );
    pin->SetName( aName );
    pin->SetType( aType );
    pin->SetVisible( true );
    pin->SetPosition( aPosition );
    pin->SetOrientation( aOrientation );
    pin->SetLength( schIUScale.mmToIU( 2.54 ) );
    aLibrary.AddDrawItem( pin );
    return pin;
}


/// A De Morgan part. Its Standard body draws pins 1 (A) and 2 (B) above and below the origin, pointing into an upright
/// body; its Alternate body draws the same two pins left and right of the origin, pointing into a crosswise body. Pin 3
/// (C) is common to both bodies. Pins 1 and 2 of the two bodies look alike (the same number, name and type) but are
/// distinct library pins with their own identities and geometry.
struct DE_MORGAN_PART
{
    LIB_SYMBOL                               library;
    std::map<std::pair<int, wxString>, KIID> pins; ///< (body style, number) -> library pin identity

    DE_MORGAN_PART() : library( wxS( "DeMorganProbe" ) )
    {
        library.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "DeMorganProbe" ) ) );
        library.SetHasDeMorganBodyStyles( true );
        auto add = [&]( int aUnit, int aStyle, const wxString& aNumber, const wxString& aName, const VECTOR2I& aAt,
                        PIN_ORIENTATION aOrientation )
        {
            pins[{ aStyle, aNumber }] = AddBodyPin( library, aUnit, aStyle, aNumber, aName, aAt, aOrientation )->m_Uuid;
        };
        add( 1, 1, wxS( "1" ), wxS( "A" ), Mm( 0, -5.08 ), PIN_ORIENTATION::PIN_DOWN );
        add( 1, 1, wxS( "2" ), wxS( "B" ), Mm( 0, 5.08 ), PIN_ORIENTATION::PIN_UP );
        add( 1, 2, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
        add( 1, 2, wxS( "2" ), wxS( "B" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
        add( 0, 0, wxS( "3" ), wxS( "C" ), Mm( 7.62, 7.62 ), PIN_ORIENTATION::PIN_LEFT );
    }
};


/// The complete pin observation of @a aSymbol at @a aPath in @a aVariant, by pin number.
std::map<std::string, SchematicPinAnchor> CompletePins( const SCH_SYMBOL& aSymbol, const SCH_SHEET_PATH& aPath,
                                                       const wxString& aVariant = wxEmptyString )
{
    SchematicSymbolPinGeometry output;
    PackSchematicPinGeometry( aSymbol, aPath, aVariant, output );
    BOOST_REQUIRE_MESSAGE( output.complete(), output.ShortDebugString() );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), SPGIR_UNSPECIFIED );
    BOOST_CHECK_EQUAL( output.limitations_size(), 0 );
    std::map<std::string, SchematicPinAnchor> result;
    for( const SchematicPinAnchor& pin : output.pins() )
        BOOST_CHECK_MESSAGE( result.emplace( pin.number(), pin ).second, "pin " + pin.number() + " is reported once" );
    return result;
}


/// @a aSymbol at @a aPath in @a aVariant reports no pins, @a aReason and a limitation containing @a aLimitation.
void RequireIncomplete( const SCH_SYMBOL& aSymbol, const SCH_SHEET_PATH& aPath, const wxString& aVariant,
                        SchematicPinGeometryIncompleteReason aReason, const std::string& aLimitation )
{
    SchematicSymbolPinGeometry output;
    // A complete earlier observation must not leak into this one.
    output.set_complete( true );
    output.add_pins()->set_number( "stale" );
    PackSchematicPinGeometry( aSymbol, aPath, aVariant, output );
    BOOST_CHECK( !output.complete() );
    BOOST_CHECK_EQUAL( output.pins_size(), 0 );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), aReason );
    bool named = false;
    for( const std::string& limitation : output.limitations() )
        named |= limitation.find( aLimitation ) != std::string::npos;
    BOOST_CHECK_MESSAGE( named, "a limitation names \"" + aLimitation + "\": " + output.ShortDebugString() );
}


/// Where KiCad draws library pin @a aLibraryPin of @a aSymbol's own definition on the sheet: the symbol's transform
/// applied to the library position, at the symbol's position.
VECTOR2I Drawn( const SCH_SYMBOL& aSymbol, const KIID& aLibraryPin )
{
    for( const SCH_PIN* pin : aSymbol.GetLibSymbolRef()->GetPins() )
    {
        if( pin->m_Uuid == aLibraryPin )
            return aSymbol.GetTransform().TransformCoordinate( pin->GetLocalPosition() ) + aSymbol.GetPosition();
    }
    BOOST_FAIL( "library pin " + aLibraryPin.AsStdString() + " belongs to the placed definition" );
    return VECTOR2I();
}


VECTOR2I Position( const SchematicPinAnchor& aPin )
{
    return VECTOR2I( schIUScale.NmToIU( aPin.position().x_nm() ), schIUScale.NmToIU( aPin.position().y_nm() ) );
}
}


BOOST_AUTO_TEST_SUITE( SchematicPinGeometryFacts )


BOOST_AUTO_TEST_CASE( IncompletePinGeometryNamesItsReason )
{
    PLACED placed( wxS( "Reasons" ) );
    AddPin( placed.library, wxS( "1" ), wxS( "A" ), ELECTRICAL_PINTYPE::PT_PASSIVE, true );
    AddPin( placed.library, wxS( "2" ), wxS( "B" ), ELECTRICAL_PINTYPE::PT_PASSIVE, true );
    placed.Place();
    SchematicSymbolPinGeometry output;
    placed.Measure( output );

    // An unresolved definition.
    SCH_SYMBOL unresolved;
    PackSchematicPinGeometry( unresolved, placed.path, wxEmptyString, output );
    BOOST_CHECK( !output.complete() );
    BOOST_CHECK_EQUAL( output.pins_size(), 0 );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), SPGIR_DEFINITION_UNRESOLVED );

    // A complete observation clears the previous reason.
    placed.Measure( output );

    // A design variant that swaps in another library symbol: its pins would only be
    // matched by similarity, so the mapping is reported as unresolved.
    placed.symbol->SetVariantSymbolOverride( placed.path, wxS( "replacement" ), LIB_ID( wxS( "Other" ), wxS( "Part" ) ) );
    PackSchematicPinGeometry( *placed.symbol, placed.path, wxS( "replacement" ), output );
    BOOST_CHECK( !output.complete() );
    BOOST_CHECK_EQUAL( output.pins_size(), 0 );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED );

    // False-positive guard: the same symbol outside that variant is complete.
    placed.Measure( output );

    // Two active pins sharing one placed identity.
    auto active = placed.symbol->GetPins( &placed.path );
    BOOST_REQUIRE_EQUAL( active.size(), 2 );
    const KIID original = active[0]->m_Uuid;
    const_cast<KIID&>( active[0]->m_Uuid ) = active[1]->m_Uuid;
    PackSchematicPinGeometry( *placed.symbol, placed.path, wxEmptyString, output );
    BOOST_CHECK( !output.complete() );
    BOOST_CHECK_EQUAL( output.pins_size(), 0 );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), SPGIR_PLACED_IDENTITY_MISSING );
    const_cast<KIID&>( active[0]->m_Uuid ) = original;
    placed.Measure( output );
}


BOOST_AUTO_TEST_CASE( PinsReportTheirImplicitPowerConnection )
{
    SchematicSymbolPinGeometry output;

    // An ordinary symbol: a hidden power input is a legacy global power connection named
    // by the pin; a visible power input and a passive pin carry no implicit connection.
    PLACED normal( wxS( "Regulator" ) );
    normal.library.SetNormal();
    AddPin( normal.library, wxS( "1" ), wxS( "VCC_HIDDEN" ), ELECTRICAL_PINTYPE::PT_POWER_IN, false );
    AddPin( normal.library, wxS( "2" ), wxS( "VIN" ), ELECTRICAL_PINTYPE::PT_POWER_IN, true );
    AddPin( normal.library, wxS( "3" ), wxS( "OUT" ), ELECTRICAL_PINTYPE::PT_PASSIVE, true );
    normal.Place();
    auto pins = normal.Measure( output );
    BOOST_REQUIRE_EQUAL( pins.size(), 3 );
    BOOST_CHECK_EQUAL( pins["1"].power_scope(), SPPS_GLOBAL );
    BOOST_CHECK_EQUAL( pins["1"].power_net(), "VCC_HIDDEN" );
    BOOST_CHECK_EQUAL( pins["1"].electrical_type(), kiapi::common::types::EPT_POWER_INPUT );
    BOOST_CHECK( !pins["1"].visible() );
    BOOST_CHECK_EQUAL( pins["2"].power_scope(), SPPS_NONE );
    BOOST_CHECK( pins["2"].power_net().empty() );
    BOOST_CHECK_EQUAL( pins["2"].electrical_type(), kiapi::common::types::EPT_POWER_INPUT );
    BOOST_CHECK_EQUAL( pins["3"].power_scope(), SPPS_NONE );
    BOOST_CHECK( pins["3"].power_net().empty() );
    BOOST_CHECK_EQUAL( pins["3"].electrical_type(), kiapi::common::types::EPT_PASSIVE );

    // A global power symbol is named by its value, not by its pin name.
    PLACED global( wxS( "GlobalSupply" ) );
    global.library.SetGlobalPower();
    AddPin( global.library, wxS( "1" ), wxS( "PIN_NAME" ), ELECTRICAL_PINTYPE::PT_POWER_IN, false );
    global.library.GetValueField().SetText( wxS( "RAIL_3V3" ) );
    global.Place();
    pins = global.Measure( output );
    BOOST_REQUIRE_EQUAL( pins.size(), 1 );
    BOOST_CHECK_EQUAL( pins["1"].power_scope(), SPPS_GLOBAL );
    BOOST_CHECK_EQUAL( pins["1"].power_net(), "RAIL_3V3" );

    // A local power symbol joins same-named connections on its own sheet only.
    PLACED local( wxS( "LocalSupply" ) );
    local.library.SetLocalPower();
    AddPin( local.library, wxS( "1" ), wxS( "PIN_NAME" ), ELECTRICAL_PINTYPE::PT_POWER_IN, false );
    local.library.GetValueField().SetText( wxS( "LOCAL_RAIL" ) );
    local.Place();
    pins = local.Measure( output );
    BOOST_REQUIRE_EQUAL( pins.size(), 1 );
    BOOST_CHECK_EQUAL( pins["1"].power_scope(), SPPS_LOCAL );
    BOOST_CHECK_EQUAL( pins["1"].power_net(), "LOCAL_RAIL" );

    // False-positive guard: a power symbol pin that is not a power input is no implicit connection.
    PLACED output_only( wxS( "Flag" ) );
    output_only.library.SetGlobalPower();
    AddPin( output_only.library, wxS( "1" ), wxS( "FLAG" ), ELECTRICAL_PINTYPE::PT_POWER_OUT, true );
    output_only.library.GetValueField().SetText( wxS( "PWR_FLAG" ) );
    output_only.Place();
    pins = output_only.Measure( output );
    BOOST_CHECK_EQUAL( pins["1"].power_scope(), SPPS_NONE );
    BOOST_CHECK( pins["1"].power_net().empty() );
    BOOST_CHECK_EQUAL( pins["1"].electrical_type(), kiapi::common::types::EPT_POWER_OUTPUT );
}


BOOST_AUTO_TEST_CASE( UnresolvedSymbolsAreMeasuredByTheirOwnBounds )
{
    // KiCad draws a symbol whose library definition it cannot resolve as its placeholder
    // body, without pins. A placement measurement reports exactly those bounds, as KiCad's
    // own bounding box does, and its pins as incomplete, instead of refusing the sheet.
    PLACED placed( wxS( "Resolved" ) );
    AddPin( placed.library, wxS( "1" ), wxS( "A" ), ELECTRICAL_PINTYPE::PT_PASSIVE, true );
    placed.Place();
    SCH_SYMBOL unresolved;
    unresolved.SetPosition( VECTOR2I( schIUScale.mmToIU( 25.4 ), schIUScale.mmToIU( 12.7 ) ) );
    unresolved.SetOrientation( SYM_ORIENT_90 );
    for( SCH_FIELD& field : unresolved.GetFields() )
        field.SetVisible( false );
    const BOX2I bounds = MeasureSchematicSymbolBounds( unresolved, placed.path, wxEmptyString );
    BOOST_CHECK( bounds == unresolved.GetBodyAndPinsBoundingBox() );
    BOOST_CHECK_GT( bounds.GetWidth(), 0 );
    BOOST_CHECK_GT( bounds.GetHeight(), 0 );
    BOOST_CHECK( bounds.Contains( unresolved.GetPosition() ) );
    SchematicSymbolPinGeometry output;
    PackSchematicPinGeometry( unresolved, placed.path, wxEmptyString, output );
    BOOST_CHECK( !output.complete() );
    BOOST_CHECK_EQUAL( output.pins_size(), 0 );
    BOOST_CHECK_EQUAL( output.incomplete_reason(), SPGIR_DEFINITION_UNRESOLVED );

    // A visible field of the unresolved symbol is part of its bounds, as KiCad draws it.
    SCH_FIELD* reference = unresolved.GetField( FIELD_T::REFERENCE );
    BOOST_REQUIRE( reference );
    reference->SetText( wxS( "X1" ) );
    reference->SetPosition( unresolved.GetPosition() + VECTOR2I( schIUScale.mmToIU( 20 ), 0 ) );
    reference->SetVisible( true );
    const BOX2I withField = MeasureSchematicSymbolBounds( unresolved, placed.path, wxEmptyString );
    BOOST_CHECK( withField.Contains( bounds ) );
    BOOST_CHECK( withField.Contains( reference->GetBoundingBox( &placed.path, wxEmptyString ) ) );

    // False-positive guard: the resolved symbol is measured by its own definition, with its pin.
    const BOX2I resolved = MeasureSchematicSymbolBounds( *placed.symbol, placed.path, wxEmptyString );
    BOOST_CHECK( resolved.Contains( placed.symbol->GetPins( &placed.path ).front()->GetPosition() ) );
    placed.Measure( output );
}


BOOST_AUTO_TEST_CASE( VisiblePinBoundsReachThePinTarget )
{
    // A symbol's measured bounds reach past the end of every visible pin by the target KiCad
    // draws on an unconnected pin end (TARGET_PIN_RADIUS, 15 mil) plus the pin box's one-unit
    // inflation: 381,100 nm. Library pins, which the measurement reads, are never connected.
    // The reach does not come from fields: with every field hidden and empty it remains. The
    // realizer lets a label on a pin cover its own symbol only that far in front of the pin.
    auto reach = []( ELECTRICAL_PINTYPE aType )
    {
        PLACED placed( wxS( "Reach" ) );
        AddPin( placed.library, wxS( "1" ), wxS( "A" ), aType, true );
        placed.Place();
        for( SCH_FIELD& field : placed.symbol->GetFields() )
        {
            field.SetText( wxEmptyString );
            field.SetVisible( false );
        }
        const BOX2I bounds = MeasureSchematicSymbolBounds( *placed.symbol, placed.path, wxEmptyString );
        const SCH_PIN* pin = placed.symbol->GetPins( &placed.path ).front();
        // The default pin runs right from its connection point into the body, so it faces left.
        BOOST_REQUIRE( pin->PinDrawOrient( placed.symbol->GetTransform() ) == PIN_ORIENTATION::PIN_RIGHT );
        return pin->GetPosition().x - bounds.GetLeft();
    };
    BOOST_CHECK_EQUAL( reach( ELECTRICAL_PINTYPE::PT_PASSIVE ), TARGET_PIN_RADIUS + 1 );
    BOOST_CHECK_EQUAL( KiROUND( schIUScale.IUTomm( TARGET_PIN_RADIUS + 1 ) * 1e6 ), 381100 );
    // A no-connect pin is never drawn dangling, so its bounds end one unit past it.
    BOOST_CHECK_EQUAL( reach( ELECTRICAL_PINTYPE::PT_NC ), 1 );
}


BOOST_AUTO_TEST_CASE( AlternateBodyReportsItsOwnLibraryPins )
{
    // A De Morgan symbol drawn with its Alternate body reports that body's own library pins where that body draws them,
    // turned and mirrored with the symbol, never the Standard body's pins that share their numbers, names and types; the
    // common pin belongs to both bodies.
    DE_MORGAN_PART       part;
    SCH_SHEET      sheet;
    SCH_SHEET_PATH path;
    path.push_back( &sheet );
    SCH_SYMBOL symbol( part.library, part.library.GetLibId(), &path, 1, 2, Mm( 50.8, 25.4 ) );
    BOOST_REQUIRE_EQUAL( symbol.GetBodyStyle(), 2 );

    for( int orientation : std::initializer_list<int>{ SYM_ORIENT_0, SYM_ORIENT_90, SYM_ORIENT_180 | SYM_MIRROR_Y,
                                                       SYM_ORIENT_270 | SYM_MIRROR_X } )
    {
        symbol.SetOrientation( orientation );
        auto pins = CompletePins( symbol, path );
        BOOST_REQUIRE_EQUAL( pins.size(), 3 );
        for( const wxString& number : { wxString( wxS( "1" ) ), wxString( wxS( "2" ) ) } )
        {
            const SchematicPinAnchor& pin = pins.at( number.ToStdString() );
            BOOST_CHECK_EQUAL( pin.library_pin_id().value(), part.pins.at( { 2, number } ).AsStdString() );
            BOOST_CHECK_NE( pin.library_pin_id().value(), part.pins.at( { 1, number } ).AsStdString() );
            BOOST_CHECK_EQUAL( pin.body_style(), 2 );
            BOOST_CHECK_EQUAL( pin.unit(), 1 );
            // The check discriminates: the two bodies draw this pin at different points.
            BOOST_CHECK( Drawn( symbol, part.pins.at( { 2, number } ) ) != Drawn( symbol, part.pins.at( { 1, number } ) ) );
            BOOST_CHECK( Position( pin ) == Drawn( symbol, part.pins.at( { 2, number } ) ) );
        }
        const SchematicPinAnchor& common = pins.at( "3" );
        BOOST_CHECK_EQUAL( common.library_pin_id().value(), part.pins.at( { 0, wxS( "3" ) } ).AsStdString() );
        BOOST_CHECK_EQUAL( common.body_style(), 0 );
        BOOST_CHECK_EQUAL( common.unit(), 0 );
        BOOST_CHECK( Position( common ) == Drawn( symbol, part.pins.at( { 0, wxS( "3" ) } ) ) );
    }

    // Unturned, the Alternate body's pins face into its crosswise body and the Standard body's would face into its
    // upright one: the body direction is the selected body's too.
    symbol.SetOrientation( SYM_ORIENT_0 );
    auto alternate = CompletePins( symbol, path );
    BOOST_CHECK_EQUAL( alternate.at( "1" ).body_direction_x(), 1 );
    BOOST_CHECK_EQUAL( alternate.at( "1" ).body_direction_y(), 0 );
    BOOST_CHECK_EQUAL( alternate.at( "2" ).body_direction_x(), -1 );
    BOOST_CHECK_EQUAL( alternate.at( "2" ).body_direction_y(), 0 );

    // A changed body is a changed observation: the Standard body reports its own library pins, positions and directions,
    // and KiCad keeps each placed pin, one physical pin whichever body draws it. Switching back reports exactly the
    // first observation again.
    SchematicSymbolPinGeometry first;
    PackSchematicPinGeometry( symbol, path, wxEmptyString, first );
    symbol.SetBodyStyle( 1 );
    auto standard = CompletePins( symbol, path );
    BOOST_REQUIRE_EQUAL( standard.size(), 3 );
    for( const wxString& number : { wxString( wxS( "1" ) ), wxString( wxS( "2" ) ) } )
    {
        const SchematicPinAnchor& pin = standard.at( number.ToStdString() );
        BOOST_CHECK_EQUAL( pin.id().value(), alternate.at( number.ToStdString() ).id().value() );
        BOOST_CHECK_EQUAL( pin.library_pin_id().value(), part.pins.at( { 1, number } ).AsStdString() );
        BOOST_CHECK_EQUAL( pin.body_style(), 1 );
        BOOST_CHECK( Position( pin ) == Drawn( symbol, part.pins.at( { 1, number } ) ) );
        BOOST_CHECK_EQUAL( pin.body_direction_x(), 0 );
    }
    BOOST_CHECK_EQUAL( standard.at( "1" ).body_direction_y(), 1 );
    BOOST_CHECK_EQUAL( standard.at( "2" ).body_direction_y(), -1 );
    BOOST_CHECK_EQUAL( standard.at( "3" ).SerializeAsString(), alternate.at( "3" ).SerializeAsString() );
    symbol.SetBodyStyle( 2 );
    SchematicSymbolPinGeometry again;
    PackSchematicPinGeometry( symbol, path, wxEmptyString, again );
    BOOST_CHECK_MESSAGE( again.SerializeAsString() == first.SerializeAsString(), again.ShortDebugString() );
}


BOOST_AUTO_TEST_CASE( LookalikePinsKeepTheirOwnIdentities )
{
    // Pins that look alike (the same name, type, length and direction) but carry their own numbers are distinct pins:
    // each placed pin reports its own library pin and position. Two definitions drawn identically are distinct
    // definitions: a symbol of each reports its own library pins, and the two placements have their own placed pins.
    auto build = []( LIB_SYMBOL& aLibrary )
    {
        std::map<wxString, KIID> ids;
        for( const auto& [number, x] : { std::pair<wxString, double>( wxS( "4" ), -2.54 ),
                                         std::pair<wxString, double>( wxS( "5" ), 2.54 ) } )
        {
            ids[number] = AddBodyPin( aLibrary, 1, 0, number, wxS( "GND" ), Mm( x, 5.08 ), PIN_ORIENTATION::PIN_UP,
                                      ELECTRICAL_PINTYPE::PT_POWER_IN )->m_Uuid;
        }
        return ids;
    };
    LIB_SYMBOL first( wxS( "LookalikeA" ) ), second( wxS( "LookalikeB" ) );
    first.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "LookalikeA" ) ) );
    second.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "LookalikeB" ) ) );
    const auto firstIds = build( first ), secondIds = build( second );
    SCH_SHEET      sheet;
    SCH_SHEET_PATH path;
    path.push_back( &sheet );
    SCH_SYMBOL a( first, first.GetLibId(), &path, 1, 1, Mm( 25.4, 25.4 ) );
    SCH_SYMBOL b( second, second.GetLibId(), &path, 1, 1, Mm( 25.4, 25.4 ) );
    const auto pinsA = CompletePins( a, path ), pinsB = CompletePins( b, path );
    BOOST_REQUIRE_EQUAL( pinsA.size(), 2 );
    BOOST_REQUIRE_EQUAL( pinsB.size(), 2 );
    std::set<std::string> placed;
    for( const wxString& number : { wxString( wxS( "4" ) ), wxString( wxS( "5" ) ) } )
    {
        const std::string key = number.ToStdString();
        BOOST_CHECK_EQUAL( pinsA.at( key ).library_pin_id().value(), firstIds.at( number ).AsStdString() );
        BOOST_CHECK_EQUAL( pinsB.at( key ).library_pin_id().value(), secondIds.at( number ).AsStdString() );
        BOOST_CHECK( Position( pinsA.at( key ) ) == Drawn( a, firstIds.at( number ) ) );
        BOOST_CHECK( Position( pinsB.at( key ) ) == Position( pinsA.at( key ) ) );
        placed.insert( pinsA.at( key ).id().value() );
        placed.insert( pinsB.at( key ).id().value() );
    }
    BOOST_CHECK( Position( pinsA.at( "4" ) ) != Position( pinsA.at( "5" ) ) );
    BOOST_CHECK_EQUAL( placed.size(), 4 );
}


BOOST_AUTO_TEST_CASE( PinsThatShareANumberAreNeverGuessed )
{
    // KiCad saves a placed pin under its number alone and pairs it with a library pin of that number when it loads, so
    // when two pins of the selected body share a number, which placed pin is which library pin is not saved. Such a
    // symbol is reported incomplete, naming the number, never with a guessed pairing.
    SCH_SHEET      sheetA, sheetB;
    SCH_SHEET_PATH pathA, pathB;
    pathA.push_back( &sheetA );
    pathB.push_back( &sheetB );
    const std::string shared = "Pin numbers '3' are shared";

    // Two pins numbered 3 in one unit.
    LIB_SYMBOL twice( wxS( "Twice" ) );
    twice.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "Twice" ) ) );
    AddBodyPin( twice, 1, 0, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( twice, 1, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( twice, 1, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 2.54 ), PIN_ORIENTATION::PIN_LEFT );
    SCH_SYMBOL twiceSymbol( twice, twice.GetLibId(), &pathA, 1, 1 );
    RequireIncomplete( twiceSymbol, pathA, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, shared );

    // Pin 3 drawn in each of two units, as a dual diode's shared cathode often is: the unit on this sheet shares it.
    LIB_SYMBOL dual( wxS( "Dual" ) );
    dual.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "Dual" ) ) );
    dual.SetUnitCount( 2, false );
    AddBodyPin( dual, 1, 0, wxS( "1" ), wxS( "A1" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( dual, 1, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( dual, 2, 0, wxS( "2" ), wxS( "A2" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( dual, 2, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    SCH_SYMBOL dualSymbol( dual, dual.GetLibId(), &pathA, 1, 1 );
    RequireIncomplete( dualSymbol, pathA, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, shared );

    // False-positive guards. Pins that share a number only in a body style that is not selected are never paired, so
    // the selected body is exact; selecting the other body makes the symbol incomplete.
    LIB_SYMBOL styled( wxS( "Styled" ) );
    styled.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "Styled" ) ) );
    styled.SetHasDeMorganBodyStyles( true );
    AddBodyPin( styled, 1, 1, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( styled, 1, 1, wxS( "3" ), wxS( "K" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( styled, 1, 1, wxS( "3" ), wxS( "K" ), Mm( 5.08, 2.54 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( styled, 1, 2, wxS( "1" ), wxS( "A" ), Mm( 0, -5.08 ), PIN_ORIENTATION::PIN_DOWN );
    AddBodyPin( styled, 1, 2, wxS( "3" ), wxS( "K" ), Mm( 0, 5.08 ), PIN_ORIENTATION::PIN_UP );
    SCH_SYMBOL styledSymbol( styled, styled.GetLibId(), &pathA, 1, 2 );
    BOOST_CHECK_EQUAL( CompletePins( styledSymbol, pathA ).size(), 2 );
    styledSymbol.SetBodyStyle( 1 );
    RequireIncomplete( styledSymbol, pathA, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, shared );

    // Pins that share a number only in units that this sheet instance does not draw leave it exact; the instance that
    // draws one of those units, or a unit swapped to one, is incomplete.
    LIB_SYMBOL triple( wxS( "Triple" ) );
    triple.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "Triple" ) ) );
    triple.SetUnitCount( 3, false );
    AddBodyPin( triple, 1, 0, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( triple, 1, 0, wxS( "2" ), wxS( "B" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( triple, 2, 0, wxS( "3" ), wxS( "C" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( triple, 2, 0, wxS( "9" ), wxS( "V" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( triple, 3, 0, wxS( "4" ), wxS( "D" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( triple, 3, 0, wxS( "9" ), wxS( "V" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    SCH_SYMBOL tripleSymbol( triple, triple.GetLibId(), &pathA, 1, 1 );
    tripleSymbol.AddHierarchicalReference( pathB.Path(), wxS( "U9" ), 2 );
    BOOST_CHECK_EQUAL( CompletePins( tripleSymbol, pathA ).size(), 2 );
    RequireIncomplete( tripleSymbol, pathB, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, "Pin numbers '9' are shared" );
    tripleSymbol.SetUnitSelection( &pathA, 3 );
    RequireIncomplete( tripleSymbol, pathA, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, "Pin numbers '9' are shared" );
}


BOOST_AUTO_TEST_CASE( EachSheetInstanceReportsItsOwnUnitAndVariant )
{
    // One symbol drawn on two instances of a repeated sheet: each instance reports the pins of the unit it draws, the
    // common pin as the same placed pin on both, and a unit swap reports exactly the pins the other instance reports.
    LIB_SYMBOL library( wxS( "TwoUnits" ) );
    library.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "TwoUnits" ) ) );
    library.SetUnitCount( 2, false );
    AddBodyPin( library, 1, 0, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( library, 1, 0, wxS( "2" ), wxS( "B" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( library, 2, 0, wxS( "3" ), wxS( "C" ), Mm( -5.08, 2.54 ), PIN_ORIENTATION::PIN_RIGHT );
    AddBodyPin( library, 2, 0, wxS( "4" ), wxS( "D" ), Mm( 5.08, 2.54 ), PIN_ORIENTATION::PIN_LEFT );
    AddBodyPin( library, 0, 0, wxS( "5" ), wxS( "V" ), Mm( 0, -5.08 ), PIN_ORIENTATION::PIN_DOWN );
    SCH_SHEET      sheetA, sheetB;
    SCH_SHEET_PATH pathA, pathB;
    pathA.push_back( &sheetA );
    pathB.push_back( &sheetB );
    SCH_SYMBOL symbol( library, library.GetLibId(), &pathA, 1, 1, Mm( 76.2, 50.8 ) );
    symbol.AddHierarchicalReference( pathB.Path(), wxS( "U1" ), 2 );
    const auto onA = CompletePins( symbol, pathA ), onB = CompletePins( symbol, pathB );
    auto numbers = []( const std::map<std::string, SchematicPinAnchor>& aPins )
    {
        std::set<std::string> result;
        for( const auto& [number, pin] : aPins )
            result.insert( number );
        return result;
    };
    BOOST_CHECK( numbers( onA ) == ( std::set<std::string>{ "1", "2", "5" } ) );
    BOOST_CHECK( numbers( onB ) == ( std::set<std::string>{ "3", "4", "5" } ) );
    BOOST_CHECK_EQUAL( onA.at( "5" ).SerializeAsString(), onB.at( "5" ).SerializeAsString() );
    symbol.SetUnitSelection( &pathA, 2 );
    const auto swapped = CompletePins( symbol, pathA );
    BOOST_REQUIRE( numbers( swapped ) == numbers( onB ) );
    for( const auto& [number, pin] : swapped )
        BOOST_CHECK_EQUAL( pin.SerializeAsString(), onB.at( number ).SerializeAsString() );
    symbol.SetUnitSelection( &pathA, 1 );

    // A design variant that swaps in another library symbol on instance A only: in that variant A is reported
    // unresolved (its pins have no persistent link to the replacement's pins), while B and every other variant of A
    // stay exact. The override's symbol is missing here (nothing can load Other:Part), and the observation still never
    // falls back to the base symbol's pins.
    const LIB_ID replacement( wxS( "Other" ), wxS( "Part" ) );
    symbol.SetVariantSymbolOverride( pathA, wxS( "Alt" ), replacement );
    symbol.SetDNP( true, &pathA, wxS( "Unfitted" ) );
    BOOST_REQUIRE( symbol.GetVariant( pathA, wxS( "Unfitted" ) ).has_value() );
    BOOST_REQUIRE( !symbol.GetVariant( pathA, wxS( "Unfitted" ) )->m_SymbolOverride.has_value() );
    RequireIncomplete( symbol, pathA, wxS( "Alt" ), SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED, "exact persistent pin mapping" );
    for( const auto& [pins, reference] : { std::make_pair( CompletePins( symbol, pathB, wxS( "Alt" ) ), &onB ),
                                           std::make_pair( CompletePins( symbol, pathA ), &onA ),
                                           std::make_pair( CompletePins( symbol, pathA, wxS( "Unfitted" ) ), &onA ) } )
    {
        BOOST_REQUIRE( numbers( pins ) == numbers( *reference ) );
        for( const auto& [number, pin] : pins )
            BOOST_CHECK_EQUAL( pin.SerializeAsString(), reference->at( number ).SerializeAsString() );
    }

    // A changed variant: the override removed, or set to the symbol's own library symbol, leaves A exact with exactly
    // the pins it reported before the override.
    BOOST_REQUIRE( symbol.ClearVariantSymbolOverride( pathA, wxS( "Alt" ) ) );
    for( const auto& [number, pin] : CompletePins( symbol, pathA, wxS( "Alt" ) ) )
        BOOST_CHECK_EQUAL( pin.SerializeAsString(), onA.at( number ).SerializeAsString() );
    symbol.SetVariantSymbolOverride( pathA, wxS( "Alt" ), symbol.GetLibId() );
    BOOST_CHECK_EQUAL( CompletePins( symbol, pathA, wxS( "Alt" ) ).size(), 3 );
    symbol.SetVariantSymbolOverride( pathA, wxS( "Alt" ), replacement );
    RequireIncomplete( symbol, pathA, wxS( "Alt" ), SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED, "exact persistent pin mapping" );
}


BOOST_AUTO_TEST_CASE( AlternateBodyIdentitiesSurviveSaveAndReload )
{
    // A De Morgan symbol drawn with its Alternate body, saved to a schematic file and loaded back, reports exactly the
    // same placed pins, Alternate-body library pins, positions and directions; its replacement variant is still
    // unresolved, and a symbol with pins that share a number is still incomplete rather than paired by a guess.
    wxString project = wxFileName::CreateTempFileName( wxS( "lane2a_p20323_project" ) );
    wxString file = wxFileName::CreateTempFileName( wxS( "lane2a_p20323_schematic" ) );
    wxRemoveFile( project );
    wxRemoveFile( file );
    project += wxS( ".kicad_pro" );
    file += wxS( ".kicad_sch" );
    {
        SETTINGS_MANAGER settings;
        settings.LoadProject( project.ToStdString() );
        auto schematic = std::make_unique<SCHEMATIC>( nullptr );
        schematic->SetProject( &settings.Prj() );
        schematic->CreateDefaultScreens();
        SCH_SHEET*  sheet = schematic->GetTopLevelSheets()[0];
        SCH_SCREEN* screen = sheet->GetScreen();
        screen->SetFileName( file );
        SCH_SHEET_PATH path;
        path.push_back( sheet );

        DE_MORGAN_PART part;
        screen->AddLibSymbol( new LIB_SYMBOL( part.library ) );
        auto* symbol = new SCH_SYMBOL( part.library, part.library.GetLibId(), &path, 1, 2, Mm( 50.8, 25.4 ) );
        symbol->SetOrientation( SYM_ORIENT_90 );
        screen->Append( symbol );
        symbol->SetVariantSymbolOverride( path, wxS( "Alt" ), LIB_ID( wxS( "Other" ), wxS( "Part" ) ) );

        LIB_SYMBOL twice( wxS( "Twice" ) );
        twice.SetLibId( LIB_ID( wxS( "Lane2A" ), wxS( "Twice" ) ) );
        AddBodyPin( twice, 1, 0, wxS( "1" ), wxS( "A" ), Mm( -5.08, 0 ), PIN_ORIENTATION::PIN_RIGHT );
        AddBodyPin( twice, 1, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 0 ), PIN_ORIENTATION::PIN_LEFT );
        AddBodyPin( twice, 1, 0, wxS( "3" ), wxS( "K" ), Mm( 5.08, 2.54 ), PIN_ORIENTATION::PIN_LEFT );
        screen->AddLibSymbol( new LIB_SYMBOL( twice ) );
        auto* shared = new SCH_SYMBOL( twice, twice.GetLibId(), &path, 1, 1, Mm( 101.6, 25.4 ) );
        screen->Append( shared );

        const KIID symbolId = symbol->m_Uuid;
        const KIID sharedId = shared->m_Uuid;
        SchematicSymbolPinGeometry before;
        PackSchematicPinGeometry( *symbol, path, wxEmptyString, before );
        BOOST_REQUIRE( before.complete() );
        BOOST_REQUIRE_EQUAL( before.pins_size(), 3 );
        for( const SchematicPinAnchor& pin : before.pins() )
        {
            const int style = pin.number() == "3" ? 0 : 2;
            BOOST_CHECK_EQUAL( pin.library_pin_id().value(), part.pins.at( { style, wxString( pin.number() ) } ).AsStdString() );
        }
        RequireIncomplete( *shared, path, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING, "Pin numbers '3' are shared" );

        SCH_IO_KICAD_SEXPR().SaveSchematicFile( file, sheet, schematic.get() );
        schematic->Reset();
        SCH_SHEET* defaultSheet = schematic->GetTopLevelSheet( 0 );
        SCH_SHEET* loaded = SCH_IO_KICAD_SEXPR().LoadSchematicFile( file, schematic.get() );
        BOOST_REQUIRE( loaded );
        schematic->AddTopLevelSheet( loaded );
        schematic->RemoveTopLevelSheet( defaultSheet );
        delete defaultSheet;
        SCH_SHEET_PATH reloadedPath;
        reloadedPath.push_back( loaded );
        SCH_SYMBOL* reloaded = nullptr;
        SCH_SYMBOL* reloadedShared = nullptr;
        for( SCH_ITEM* item : loaded->GetScreen()->Items().OfType( SCH_SYMBOL_T ) )
        {
            if( item->m_Uuid == symbolId )
                reloaded = static_cast<SCH_SYMBOL*>( item );
            else if( item->m_Uuid == sharedId )
                reloadedShared = static_cast<SCH_SYMBOL*>( item );
        }
        BOOST_REQUIRE( reloaded && reloadedShared );
        SCH_SYMBOL_INSTANCE instance;
        BOOST_REQUIRE( reloaded->GetInstance( instance, reloadedPath.Path() ) );
        BOOST_CHECK_EQUAL( reloaded->GetBodyStyle(), 2 );
        SchematicSymbolPinGeometry after;
        PackSchematicPinGeometry( *reloaded, reloadedPath, wxEmptyString, after );
        BOOST_CHECK_MESSAGE( after.SerializeAsString() == before.SerializeAsString(),
                             "before: " + before.ShortDebugString() + " after: " + after.ShortDebugString() );
        RequireIncomplete( *reloaded, reloadedPath, wxS( "Alt" ), SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED,
                           "exact persistent pin mapping" );
        RequireIncomplete( *reloadedShared, reloadedPath, wxEmptyString, SPGIR_PLACED_IDENTITY_MISSING,
                           "Pin numbers '3' are shared" );
    }
    wxRemoveFile( file );
    wxRemoveFile( project );
}


BOOST_AUTO_TEST_SUITE_END()
