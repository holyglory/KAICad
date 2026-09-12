/* Declared netclass API fidelity. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <netclass.h>
#include <stroke_params.h>
#include <base_units.h>
#include <api/common/types/project_settings.pb.h>

BOOST_AUTO_TEST_SUITE( NetclassProto )

BOOST_AUTO_TEST_CASE( ExplicitClassesPreserveEveryPersistedMember )
{
    for( const wxString& name : { wxString( "Default" ), wxString::FromUTF8( "電源" ) } )
    {
        NETCLASS original( name, true );
        original.SetPriority( 123 );
        original.SetuViaDiameter( 345000 ); original.SetuViaDrill( 123000 );
        original.SetTuningProfile( "USB matching" );
        original.SetPcbColor( KIGFX::COLOR4D( 0.25, 0.5, 0.75, 0.8 ) );
        original.SetSchematicColor( KIGFX::COLOR4D( 0.5, 0.25, 0.75, 1.0 ) );
        for( int style = -1; style <= 4; ++style )
        {
            original.SetLineStyle( style );
            kiapi::common::project::NetClass message;
            original.Serialize( message );
            BOOST_CHECK_EQUAL( message.type(), kiapi::common::project::NCT_EXPLICIT );
            BOOST_CHECK_EQUAL( message.constituents_size(), 0 );
            BOOST_REQUIRE( message.board().has_microvia_stack() );
            BOOST_REQUIRE( message.schematic().has_line_style() );
            NETCLASS restored( "placeholder", false );
            BOOST_REQUIRE( restored.Deserialize( message ) );
            BOOST_CHECK( restored.EqualsByPersistedFields( original ) );
            BOOST_CHECK_EQUAL( restored.IsDefault(), original.IsDefault() );
            BOOST_REQUIRE_EQUAL( restored.GetConstituentNetclasses().size(), 1 );
            BOOST_CHECK( restored.GetConstituentNetclasses().front() == &restored );
            kiapi::common::project::NetClass repeated;
            restored.Serialize( repeated );
            BOOST_CHECK_EQUAL( message.SerializeAsString(), repeated.SerializeAsString() );
            original.Serialize( message );
            BOOST_CHECK_EQUAL( message.SerializeAsString(), repeated.SerializeAsString() );
        }
    }
}

BOOST_AUTO_TEST_CASE( UnsetValuesStayUnsetAndLegacyPartialUpdatesRemainPartial )
{
    NETCLASS unset( "Inherited", false );
    kiapi::common::project::NetClass message;
    unset.Serialize( message );
    BOOST_CHECK( !message.board().has_clearance() );
    BOOST_CHECK( !message.board().has_via_stack() );
    BOOST_CHECK( !message.board().has_microvia_stack() );
    BOOST_CHECK( !message.schematic().has_wire_width() );
    BOOST_CHECK( !message.schematic().has_line_style() );
    NETCLASS fresh( "fresh", false );
    BOOST_REQUIRE( fresh.Deserialize( message ) );
    BOOST_CHECK( fresh.EqualsByPersistedFields( unset ) );
    NETCLASS existing( "Inherited", true );
    const int wire = existing.GetWireWidth();
    const int microvia = existing.GetuViaDiameter();
    BOOST_REQUIRE( existing.Deserialize( message ) );
    BOOST_CHECK_EQUAL( existing.GetWireWidth(), wire );
    BOOST_CHECK_EQUAL( existing.GetuViaDiameter(), microvia );
    unset.SetuViaDrill( 0 ); unset.SetLineStyle( -1 );
    unset.Serialize( message );
    BOOST_CHECK( message.board().has_microvia_stack() );
    BOOST_CHECK( message.board().microvia_stack().has_drill() );
    BOOST_CHECK( message.schematic().has_line_style() );
}

BOOST_AUTO_TEST_CASE( UnsupportedClassAndStyleDoNotPartiallyMutateExistingValues )
{
    NETCLASS first( "A", true ), second( "B", true ), composite( "", false );
    composite.SetConstituentNetclasses( { &first, &second } );
    kiapi::common::project::NetClass message, before, after;
    composite.Serialize( message );
    BOOST_CHECK_EQUAL( message.type(), kiapi::common::project::NCT_IMPLICIT );
    BOOST_CHECK_EQUAL( message.constituents_size(), 2 );
    first.Serialize( before );
    BOOST_CHECK( !first.Deserialize( message ) );
    first.Serialize( after );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );
    message = before; message.set_name( "should-not-apply" );
    message.mutable_schematic()->set_line_style( static_cast<kiapi::common::types::StrokeLineStyle>( 99 ) );
    BOOST_CHECK( !first.Deserialize( message ) );
    first.Serialize( after );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );
}

BOOST_AUTO_TEST_SUITE_END()
