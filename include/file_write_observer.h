/* Scoped native write callbacks. GPL-3.0-or-later. */
#ifndef KICAD_FILE_WRITE_OBSERVER_H
#define KICAD_FILE_WRITE_OBSERVER_H

#include <kicommon.h>
#include <functional>

class wxString;
class FILE_CONTENT_BASELINE;

/** Per-thread scope around a checked native save. Normal saves have no observer. */
class KICOMMON_API FILE_WRITE_OBSERVER
{
public:
    using BEFORE = std::function<void( const wxString& )>;
    using AFTER = std::function<void( const FILE_CONTENT_BASELINE& )>;
    FILE_WRITE_OBSERVER( BEFORE aBefore, AFTER aAfter );
    ~FILE_WRITE_OBSERVER();
    FILE_WRITE_OBSERVER( const FILE_WRITE_OBSERVER& ) = delete;
    FILE_WRITE_OBSERVER& operator=( const FILE_WRITE_OBSERVER& ) = delete;
    static void BeforeWrite( const wxString& aPath );
    static void AfterWrite( const FILE_CONTENT_BASELINE& aWritten );

private:
    BEFORE m_before;
    AFTER m_after;
    FILE_WRITE_OBSERVER* m_previous;
};

#endif
