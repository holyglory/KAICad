/* Exact native state digest contract. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <api/native_state_digest.h>

BOOST_AUTO_TEST_SUITE( NativeStateDigest )

BOOST_AUTO_TEST_CASE( KnownValuesChunkingAndObservationDoNotChangeTheDigest )
{
    NATIVE_STATE_DIGEST empty;
    BOOST_CHECK_EQUAL( empty.Hex(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" );
    NATIVE_STATE_DIGEST one, chunks;
    one.Append( "abc" );
    chunks.Print( "%s", "a" );
    const auto first = chunks.Hex();
    BOOST_CHECK_EQUAL( first, chunks.Hex() );
    chunks.Append( "b" ); chunks.Print( "%c", 'c' );
    BOOST_CHECK_EQUAL( one.Hex(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" );
    BOOST_CHECK_EQUAL( one.Hex(), chunks.Hex() );
    BOOST_CHECK_EQUAL( one.Bytes(), 3 );
    one.Append( std::string_view( "\0", 1 ) );
    BOOST_CHECK( one.Hex() != chunks.Hex() );
    BOOST_CHECK_EQUAL( one.Bytes(), 4 );
}

BOOST_AUTO_TEST_CASE( LargeInputsAreChunkIndependent )
{
    const std::string data( 200000, 'x' );
    NATIVE_STATE_DIGEST whole, parts;
    whole.Append( data );
    for( size_t index = 0; index < data.size(); index += 137 )
        parts.Append( std::string_view( data ).substr( index, std::min<size_t>( 137, data.size() - index ) ) );
    BOOST_CHECK_EQUAL( whole.Hex(), parts.Hex() );
    BOOST_CHECK_EQUAL( whole.Bytes(), parts.Bytes() );
}

BOOST_AUTO_TEST_CASE( NamedPartsHaveStableOrderAndUnambiguousBoundaries )
{
    NATIVE_STATE_DIGEST a, b, joined;
    a.Append( "a" ); b.Append( "bc" ); joined.Append( "abc" );
    NATIVE_DOCUMENT_DIGEST first, reverse, renamed, flattened;
    first.Add( "board", a ); first.Add( "project", b );
    reverse.Add( "project", b ); reverse.Add( "board", a );
    renamed.Add( "different", a ); renamed.Add( "project", b );
    flattened.Add( "board", joined );
    BOOST_CHECK_EQUAL( first.Hex(), reverse.Hex() );
    BOOST_CHECK( first.Hex() != renamed.Hex() );
    BOOST_CHECK( first.Hex() != flattened.Hex() );
    const auto before = first.Hex();
    BOOST_CHECK_THROW( first.Add( "board", b ), std::invalid_argument );
    BOOST_CHECK_THROW( first.Add( "", b ), std::invalid_argument );
    BOOST_CHECK_THROW( first.Add( std::string( "bad\0name", 8 ), b ), std::invalid_argument );
    BOOST_CHECK_EQUAL( before, first.Hex() );
}

BOOST_AUTO_TEST_SUITE_END()
