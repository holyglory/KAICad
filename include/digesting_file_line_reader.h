/* Fingerprint the bytes supplied to the native parser. GPL-3.0-or-later. */
#ifndef KICAD_DIGESTING_FILE_LINE_READER_H
#define KICAD_DIGESTING_FILE_LINE_READER_H

#include <richio.h>
#include <file_content_baseline.h>
#include <memory>

class KICOMMON_API DIGESTING_FILE_LINE_READER : public FILE_LINE_READER
{
public:
    explicit DIGESTING_FILE_LINE_READER( const wxString& aPath );
    ~DIGESTING_FILE_LINE_READER() override;
    char* ReadLine() override;
    void Rewind();
    // Includes unread trailing bytes from the SAME open file, never reopens the path.
    FILE_CONTENT_BASELINE FinishBaseline();

private:
    struct STATE;
    std::unique_ptr<STATE> m_state;
};

#endif
