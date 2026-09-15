/* Scoped native write callbacks. GPL-3.0-or-later. */
#include <file_write_observer.h>
#include <utility>

namespace
{
// Storage belongs to kicommon, not its exported class interface. Every caller
// reaches this one per-thread stack through the exported observer functions.
thread_local FILE_WRITE_OBSERVER* currentWriteObserver = nullptr;
}

FILE_WRITE_OBSERVER::FILE_WRITE_OBSERVER( BEFORE aBefore, AFTER aAfter ) :
        m_before( std::move( aBefore ) ), m_after( std::move( aAfter ) ),
        m_previous( currentWriteObserver )
{
    currentWriteObserver = this;
}

FILE_WRITE_OBSERVER::~FILE_WRITE_OBSERVER() { currentWriteObserver = m_previous; }

void FILE_WRITE_OBSERVER::BeforeWrite( const wxString& aPath )
{
    if( currentWriteObserver && currentWriteObserver->m_before )
        currentWriteObserver->m_before( aPath );
}

void FILE_WRITE_OBSERVER::AfterWrite( const FILE_CONTENT_BASELINE& aWritten )
{
    if( currentWriteObserver && currentWriteObserver->m_after )
        currentWriteObserver->m_after( aWritten );
}
