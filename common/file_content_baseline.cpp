/* Loaded/written native file versions. GPL-3.0-or-later. */
#include <file_content_baseline.h>
#include <digesting_file_line_reader.h>
#include <api/native_state_digest.h>
#include <wx/filename.h>
#include <wx/filefn.h>
#include <filesystem>
#include <fstream>

namespace
{
wxString NormalPath( const wxString& aPath )
{
    if( aPath.empty() ) return {};
    wxFileName path( aPath );
    if( !path.Normalize( wxPATH_NORM_DOTS | wxPATH_NORM_ABSOLUTE ) ) return {};
    return path.GetFullPath();
}

std::filesystem::path NativePath( const wxString& aPath )
{
    return std::filesystem::u8path( aPath.utf8_string() );
}

FILE* OpenRegularFile( const wxString& aPath )
{
    std::error_code error;
    const auto status = std::filesystem::status( NativePath( aPath ), error );
    FILE* file = nullptr;
    if( !error && std::filesystem::is_regular_file( status ) )
        file = wxFopen( aPath, "rb" );
    if( !file ) THROW_IO_ERROR( "Cannot read native file: " + aPath );
    return file;
}
}

bool FILE_CONTENT_BASELINE::SamePath( const wxString& aLeft, const wxString& aRight )
{
    const wxString left = NormalPath( aLeft );
    return !left.empty() && left == NormalPath( aRight );
}

FILE_CONTENT_BASELINE FILE_CONTENT_BASELINE::FromDigest( const wxString& aPath,
        const std::string& aSha256, uint64_t aBytes )
{
    FILE_CONTENT_BASELINE result;
    result.m_path = NormalPath( aPath );
    if( result.m_path.empty() ) return result;
    result.m_known = result.m_exists = true;
    result.m_sha256 = aSha256;
    result.m_bytes = aBytes;
    return result;
}

FILE_CONTENT_BASELINE FILE_CONTENT_BASELINE::FromBytes( const wxString& aPath,
        std::string_view aBytes, bool aTextMode )
{
    NATIVE_STATE_DIGEST hash;
#ifdef _WIN32
    // Match CRT text-mode fwrite, including an existing CR before an LF.
    if( aTextMode )
    {
        size_t start = 0;
        for( size_t index = 0; index < aBytes.size(); ++index )
        {
            if( aBytes[index] == '\n' )
            {
                hash.Append( aBytes.substr( start, index - start ) );
                hash.Append( "\r\n" );
                start = index + 1;
            }
        }
        hash.Append( aBytes.substr( start ) );
    }
    else
#else
    (void) aTextMode;
#endif
        hash.Append( aBytes );
    return FromDigest( aPath, hash.Hex(), hash.Bytes() );
}

FILE_CONTENT_BASELINE FILE_CONTENT_BASELINE::Read( const wxString& aPath, std::string* aContents )
{
    FILE_CONTENT_BASELINE result;
    if( aContents ) aContents->clear();
    result.m_path = NormalPath( aPath );
    if( result.m_path.empty() ) return result;
    std::error_code error;
    const auto path = NativePath( result.m_path );
    const auto status = std::filesystem::status( path, error );
    if( status.type() == std::filesystem::file_type::not_found
            && ( !error || error == std::errc::no_such_file_or_directory ) )
    {
        result.m_known = true;
        return result;
    }
    if( error || !std::filesystem::is_regular_file( status ) ) return result;
    std::ifstream input( path, std::ios::binary );
    if( !input ) return result;
    NATIVE_STATE_DIGEST hash;
    std::array<char, 65536> buffer;
    while( input )
    {
        input.read( buffer.data(), buffer.size() );
        std::string_view bytes( buffer.data(), static_cast<size_t>( input.gcount() ) );
        hash.Append( bytes );
        if( aContents ) aContents->append( bytes );
    }
    if( input.bad() || !input.eof() )
    {
        if( aContents ) aContents->clear();
        return result;
    }
    return FromDigest( result.m_path, hash.Hex(), hash.Bytes() );
}

FILE_BASELINE_CHECK FILE_CONTENT_BASELINE::Check( const wxString& aPath ) const
{
    if( !m_known ) return FILE_BASELINE_CHECK::UNKNOWN;
    if( !SamePath( m_path, aPath ) ) return FILE_BASELINE_CHECK::WRONG_PATH;
    const auto current = Read( aPath );
    if( !current.Known() ) return FILE_BASELINE_CHECK::UNREADABLE;
    return m_exists == current.m_exists && m_bytes == current.m_bytes && m_sha256 == current.m_sha256
            ? FILE_BASELINE_CHECK::UNCHANGED : FILE_BASELINE_CHECK::CHANGED;
}

struct DIGESTING_FILE_LINE_READER::STATE
{
    NATIVE_STATE_DIGEST hash;
};

DIGESTING_FILE_LINE_READER::DIGESTING_FILE_LINE_READER( const wxString& aPath ) :
        FILE_LINE_READER( OpenRegularFile( aPath ), aPath ), m_state( std::make_unique<STATE>() )
{}

DIGESTING_FILE_LINE_READER::~DIGESTING_FILE_LINE_READER() = default;

char* DIGESTING_FILE_LINE_READER::ReadLine()
{
    char* line = FILE_LINE_READER::ReadLine();
    if( ferror( m_fp ) ) THROW_IO_ERROR( "Error reading native file: " + m_source );
    if( line ) m_state->hash.Append( std::string_view( line, m_length ) );
    return line;
}

void DIGESTING_FILE_LINE_READER::Rewind()
{
    if( fseek( m_fp, 0, SEEK_SET ) != 0 ) THROW_IO_ERROR( "Cannot rewind native file: " + m_source );
    clearerr( m_fp );
    m_lineNum = 0;
    m_state = std::make_unique<STATE>();
}

FILE_CONTENT_BASELINE DIGESTING_FILE_LINE_READER::FinishBaseline()
{
    // Read raw blocks: ignored trailing content must not inherit the parser's line-size limit.
    std::array<char, 65536> buffer;
    size_t count;
    while( ( count = fread( buffer.data(), 1, buffer.size(), m_fp ) ) != 0 )
        m_state->hash.Append( std::string_view( buffer.data(), count ) );
    if( ferror( m_fp ) ) THROW_IO_ERROR( "Error finishing native file read: " + m_source );
    return FILE_CONTENT_BASELINE::FromDigest( m_source, m_state->hash.Hex(), m_state->hash.Bytes() );
}
