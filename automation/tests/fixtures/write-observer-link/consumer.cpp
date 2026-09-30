// ABI/lifetime test of the actual observer module. The payloads are opaque to
// that module; real wxString/file-write behavior is covered by native QA.
#include <file_write_observer.h>
#include <atomic>
#include <barrier>
#include <stdexcept>
#include <thread>

class wxString {};
class FILE_CONTENT_BASELINE {};

static void Require( bool condition )
{
    if( !condition ) throw std::runtime_error( "Observer DLL contract failed" );
}

int main()
{
    wxString path;
    FILE_CONTENT_BASELINE baseline;
    std::atomic<int> outer = 0, worker = 0;
    int inner = 0;
    std::atomic<bool> correctPayloads = true;
    FILE_WRITE_OBSERVER::BeforeWrite( path );
    FILE_WRITE_OBSERVER::AfterWrite( baseline );
    {
        FILE_WRITE_OBSERVER scope(
                [&]( const wxString& value ) { if( &value != &path ) correctPayloads = false; ++outer; },
                [&]( const FILE_CONTENT_BASELINE& value ) { if( &value != &baseline ) correctPayloads = false; ++outer; } );
        FILE_WRITE_OBSERVER::BeforeWrite( path );
        FILE_WRITE_OBSERVER::AfterWrite( baseline );
        {
            FILE_WRITE_OBSERVER nested( [&]( const wxString& ) { ++inner; },
                                        [&]( const FILE_CONTENT_BASELINE& ) { ++inner; } );
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        }
        Require( inner == 2 && outer == 2 );
        std::barrier gate( 2 );
        std::thread thread( [&]
        {
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
            FILE_WRITE_OBSERVER scoped( [&]( const wxString& ) { ++worker; },
                                        [&]( const FILE_CONTENT_BASELINE& ) { ++worker; } );
            gate.arrive_and_wait();
            FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
            gate.arrive_and_wait();
        } );
        gate.arrive_and_wait();
        FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        gate.arrive_and_wait(); thread.join();
        Require( outer == 4 && worker == 2 && correctPayloads );
        bool rejected = false;
        try
        {
            FILE_WRITE_OBSERVER failing( []( const wxString& ) { throw std::runtime_error( "reject" ); }, {} );
            FILE_WRITE_OBSERVER::BeforeWrite( path );
        }
        catch( const std::runtime_error& ) { rejected = true; }
        Require( rejected );
        FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
        Require( outer == 6 );
    }
    FILE_WRITE_OBSERVER::BeforeWrite( path ); FILE_WRITE_OBSERVER::AfterWrite( baseline );
    Require( outer == 6 && worker == 2 && inner == 2 );
}
