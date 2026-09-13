/* Loaded/written native file versions. GPL-3.0-or-later. */
#ifndef KICAD_FILE_CONTENT_BASELINE_H
#define KICAD_FILE_CONTENT_BASELINE_H

#include <kicommon.h>
#include <wx/string.h>
#include <cstdint>
#include <string>
#include <string_view>
#include <functional>

enum class FILE_BASELINE_CHECK { UNKNOWN, UNCHANGED, CHANGED, WRONG_PATH, UNREADABLE };

/** A content baseline, not a lock or permission to overwrite a file. */
class KICOMMON_API FILE_CONTENT_BASELINE
{
public:
    // An unknown default must never be interpreted as an absent file.
    static FILE_CONTENT_BASELINE Read( const wxString& aPath, std::string* aContents = nullptr );
    static FILE_CONTENT_BASELINE FromBytes( const wxString& aPath, std::string_view aBytes,
                                            bool aTextMode = false );
    static bool SamePath( const wxString& aLeft, const wxString& aRight );

    FILE_BASELINE_CHECK Check( const wxString& aPath ) const;
    bool Known() const { return m_known; }
    bool Exists() const { return m_exists; }
    const wxString& Path() const { return m_path; }
    const std::string& Sha256() const { return m_sha256; }
    uint64_t Bytes() const { return m_bytes; }

private:
    friend class DIGESTING_FILE_LINE_READER;
    static FILE_CONTENT_BASELINE FromDigest( const wxString& aPath, const std::string& aSha256,
                                             uint64_t aBytes );
    wxString m_path;
    std::string m_sha256;
    uint64_t m_bytes = 0;
    bool m_known = false;
    bool m_exists = false;
};

/** UI-thread scope around an explicitly checked native save. Normal saves have no observer. */
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
    static thread_local FILE_WRITE_OBSERVER* s_current;
};

#endif
