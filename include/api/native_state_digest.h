/* Streaming native state fingerprints. GPL-3.0-or-later. */
#ifndef KICAD_NATIVE_STATE_DIGEST_H
#define KICAD_NATIVE_STATE_DIGEST_H

#include <richio.h>
#include <picosha2.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>

class NATIVE_STATE_DIGEST : public OUTPUTFORMATTER
{
public:
    void Append( std::string_view aBytes )
    {
        if( aBytes.size() > std::numeric_limits<uint64_t>::max() - m_bytes )
            throw std::overflow_error( "Native state byte count overflow" );
        // Bound the temporary buffer used inside the existing digest library.
        for( size_t offset = 0; offset < aBytes.size(); )
        {
            size_t count = std::min<size_t>( 4096, aBytes.size() - offset );
            m_hash.process( aBytes.begin() + offset, aBytes.begin() + offset + count );
            offset += count;
        }
        m_bytes += aBytes.size();
    }

    std::string Hex() const
    {
        auto copy = m_hash;
        copy.finish();
        std::array<unsigned char, picosha2::k_digest_size> bytes;
        copy.get_hash_bytes( bytes.begin(), bytes.end() );
        return picosha2::bytes_to_hex_string( bytes.begin(), bytes.end() );
    }
    uint64_t Bytes() const { return m_bytes; }

protected:
    void write( const char* aBuffer, int aCount ) override
    {
        if( aCount < 0 || ( aCount && !aBuffer ) )
            throw std::invalid_argument( "Invalid native state formatter buffer" );
        if( aCount ) Append( std::string_view( aBuffer, static_cast<size_t>( aCount ) ) );
    }

private:
    picosha2::hash256_one_by_one m_hash;
    uint64_t m_bytes = 0;
};

class NATIVE_DOCUMENT_DIGEST
{
public:
    void Add( const std::string& aName, const NATIVE_STATE_DIGEST& aState )
    {
        if( aName.empty() || aName.find( '\0' ) != std::string::npos )
            throw std::invalid_argument( "Native state parts require nonempty names" );
        if( !m_parts.emplace( aName, PART{ aState.Bytes(), aState.Hex() } ).second )
            throw std::invalid_argument( "Duplicate native state part" );
    }

    std::string Hex() const
    {
        NATIVE_STATE_DIGEST result;
        result.Append( "kicad-native-state-v1" );
        Number( result, m_parts.size() );
        for( const auto& [name, part] : m_parts )
        {
            Number( result, name.size() ); result.Append( name );
            Number( result, part.bytes ); result.Append( part.hash );
        }
        return result.Hex();
    }

private:
    struct PART { uint64_t bytes; std::string hash; };
    static void Number( NATIVE_STATE_DIGEST& aResult, uint64_t aValue )
    {
        std::array<char, 8> bytes;
        for( size_t index = 0; index < bytes.size(); ++index )
            bytes[index] = static_cast<char>( ( aValue >> ( index * 8 ) ) & 0xff );
        aResult.Append( std::string_view( bytes.data(), bytes.size() ) );
    }
    std::map<std::string, PART> m_parts;
};

#endif
