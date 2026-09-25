/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
// Exact pin identities of alternate symbol variants, and the pin facts connected
// realization measures (contract CN-1 §6.2 and §11): why a symbol's pin geometry is
// incomplete, each pin's effective electrical type, the implicit power connection the
// connection graph gives it, and the bounds a placement measurement reports for a symbol.
#include <boost/test/unit_test.hpp>
#include <api/api_sch_utils.h>
#include <api/common/commands/automation_commands.pb.h>
#include <lib_symbol.h>
#include <math/util.h>
#include <sch_field.h>
#include <sch_pin.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>
#include <sch_symbol.h>

#include <map>
#include <memory>
#include <string>

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


BOOST_AUTO_TEST_SUITE_END()
