/* Actual MSVC descriptor/stream translation; GPL-3.0-or-later. */
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include "file_stream_mode.h"

static std::string Write( const std::filesystem::path& path, const wchar_t* mode,
                          const std::string& bytes, bool legacy )
{
    HANDLE handle = CreateFileW( path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                FILE_ATTRIBUTE_NORMAL, nullptr );
    if( handle == INVALID_HANDLE_VALUE ) throw std::runtime_error( "CreateFile failed" );
    int flags = legacy ? _O_WRONLY | _O_BINARY
                       : KIPLATFORM::IO::DETAIL::WritableFileDescriptorFlags( mode );
    int descriptor = _open_osfhandle( reinterpret_cast<intptr_t>( handle ), flags );
    if( descriptor < 0 ) { CloseHandle( handle ); throw std::runtime_error( "descriptor failed" ); }
    FILE* stream = _wfdopen( descriptor, mode );
    if( !stream ) { _close( descriptor ); throw std::runtime_error( "stream failed" ); }
    const size_t count = fwrite( bytes.data(), 1, bytes.size(), stream );
    const int closed = fclose( stream );
    if( count != bytes.size() || closed != 0 ) throw std::runtime_error( "write failed" );
    std::ifstream saved( path, std::ios::binary );
    if( !saved ) throw std::runtime_error( "read failed" );
    return { std::istreambuf_iterator<char>( saved ), std::istreambuf_iterator<char>() };
}

int wmain( int argc, wchar_t** argv )
{
    try
    {
        if( argc != 2 ) throw std::runtime_error( "Expected isolated fixture root" );
        const std::filesystem::path root( argv[1] );
        const char raw[] = "one\ntwo\r\nthree\0tail\n";
        const char translated[] = "one\r\ntwo\r\r\nthree\0tail\r\n";
        const std::string input( raw, sizeof( raw ) - 1 );
        const std::string text( translated, sizeof( translated ) - 1 );
        if( Write( root / "legacy.txt", L"wt", input, true ) != input )
            throw std::runtime_error( "The original binary-descriptor defect was not reproduced" );
        if( Write( root / "text.txt", L"wt", input, false ) != text )
            throw std::runtime_error( "Text translation differs from recorded baseline bytes" );
        if( Write( root / "default.txt", L"w", input, false ) != text )
            throw std::runtime_error( "Default writable mode differs from text baseline" );
        if( Write( root / "binary.txt", L"wb", input, false ) != input )
            throw std::runtime_error( "Binary bytes were translated" );
        if( Write( root / "empty.txt", L"wt", {}, false ) != "" )
            throw std::runtime_error( "Empty output changed" );
        std::cout << "{\"legacyMismatchReproduced\":true,\"textBytesMatched\":true,"
                     "\"binaryBytesMatched\":true,\"embeddedNulPreserved\":true,\"cases\":5}\n";
        return 0;
    }
    catch( const std::exception& error ) { std::cerr << error.what() << '\n'; return 1; }
}
