/* Owned native DRC input bundle. GPL-3.0-or-later. */
#include "pcb_drc_run_inputs.h"
#include "pcb_drc_document_snapshot.h"
#include "pcb_drc_schematic_input.h"
#include <api/api_pcb_utils.h>
#include <api/api_utils.h>
#include <board.h>
#include <board_connected_item.h>
#include <api/native_state_digest.h>
#include <advanced_config.h>
#include <board_design_settings.h>
#include <build_version.h>
#include <common.h>
#include <drc/drc_engine.h>
#include <drc/drc_library_inputs.h>
#include <drawing_sheet/ds_data_item.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_proxy_view_item.h>
#include <embedded_files.h>
#include <font/outline_font.h>
#include <footprint.h>
#include <text_eval/text_eval_wrapper.h>
#include <progress_reporter.h>
#include <project.h>
#include <project/project_file.h>
#include <project/net_settings.h>
#include <pcb_project_editor_state.h>
#include <json_common.h>
#include <ki_exception.h>
#include <router/pns_routing_settings.h>
#include <eda_group.h>
#include <eda_text.h>
#include <pcb_barcode.h>
#include <pcb_field.h>
#include <pcb_tablecell.h>
#include <pgm_base.h>
#include <string_utils.h>
#include <title_block.h>
#include <wx/filename.h>
#include <pcb_marker.h>
#include <zone.h>
#include <fmt/format.h>
#include <algorithm>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <stdexcept>
#include <string_view>

std::string PcbDrcExceptionMessage( const std::exception& aError )
{
    if( const auto* io = dynamic_cast<const IO_ERROR*>( &aError ) )
        return io->Problem().ToStdString( wxConvUTF8 );
    return aError.what();
}

namespace
{
std::optional<std::string> CandidateNetName( KICAD_T aType,
        const google::protobuf::Any& aItem )
{
    using namespace kiapi::board::types;
    if( aType == PCB_TRACE_T )
    {
        Track value;
        if( !aItem.UnpackTo( &value ) ) return std::nullopt;
        return value.net().name();
    }
    if( aType == PCB_ARC_T )
    {
        Arc value;
        if( !aItem.UnpackTo( &value ) ) return std::nullopt;
        return value.net().name();
    }
    if( aType == PCB_VIA_T )
    {
        Via value;
        if( !aItem.UnpackTo( &value ) ) return std::nullopt;
        return value.net().name();
    }
    return std::nullopt;
}
}

PCB_DRC_RUN_INPUTS::~PCB_DRC_RUN_INPUTS() = default;
BOARD& PCB_DRC_RUN_INPUTS::GetBoard() const { return m_document->GetBoard(); }

namespace
{
// Text variables whose value comes from outside the design, which KiCad resolves again
// each time it lays out a text: the date and the project's version-control revision,
// which a check can capture and compare, and the time of day, which changes while the
// check runs.
constexpr const char* CAPTURED_LIVE_TEXT[] = { "CURRENT_DATE", "VCSHASH", "VCSSHORTHASH" };
constexpr const char* TIME_OF_DAY_TEXT[] = { "CURRENT_TIME_HH_MM_SS", "CURRENT_TIME_LOCALE" };

// Text also evaluates @{...} expressions (ResolveTextVars, DS_DRAW_ITEM_LIST::BuildFullText).
// These functions of KiCad's expression evaluator (common/text_eval/text_eval_parser.cpp)
// give a value that depends only on their arguments.
const std::set<std::string, std::less<>> PURE_EXPRESSION_FUNCTIONS = {
    "abs", "afterfirst", "afterlast", "avg", "beforefirst", "beforelast", "ceil", "concat", "currency",
    "dateformat", "datestring", "db", "dbv", "edown", "enearest", "eup", "fixed", "floor", "format", "fromdb",
    "fromdbv", "if", "lower", "max", "min", "pow", "replace", "round", "shunt", "sqrt", "sum", "timeformat",
    "upper", "weekdayname"
};

// today() gives the current day, which a check captures and compares like ${CURRENT_DATE}.
constexpr std::string_view TODAY = "today";

// The functions of KiCad's evaluator that read outside the design: the day, the time and
// chance, and every function whose name starts with "vcs", which reads the project's
// version-control repository.
constexpr std::wstring_view OUTSIDE_FUNCTIONS[] = { L"today", L"now", L"random" };
constexpr std::wstring_view VCS_FUNCTIONS = L"vcs";

// What ExpandTextVars leaves for an escaped reference or expression (\${...}, \@{...}).
constexpr std::wstring_view ESCAPE_MARKERS[] = { L"<<<ESC_DOLLAR:", L"<<<ESC_AT:" };

using RESOLVER = std::function<bool( wxString* )>;

// What KiCad reads from outside the design when it resolves a text.
struct TEXT_READS
{
    std::set<std::string> variables; // Live text variables it resolves.
    std::set<std::string> calls;     // Functions its expressions call, other than the pure ones.
    bool evaluates = false;          // It evaluates an expression.
    std::set<wxString> titleFields;  // Title-block fields already followed (Reading).

    // A function whose value the check cannot capture: all but the pure ones and today().
    bool Volatile() const
    {
        return std::any_of( calls.begin(), calls.end(), []( const std::string& aName ) { return aName != TODAY; } );
    }

    void Add( const TEXT_READS& aText )
    {
        variables.insert( aText.variables.begin(), aText.variables.end() );
        calls.insert( aText.calls.begin(), aText.calls.end() );
        evaluates |= aText.evaluates;
    }
};

// The whitespace of KiCad's expression reader (CHARACTER_CLASSIFIER::is_whitespace), which it
// skips between a function's name and its '('.
bool ExpressionWhitespace( wchar_t aChar )
{
    switch( aChar )
    {
    case L' ': case L'\t': case L'\r': case L'\n': case L'\f': case L'\v':
    case 0x00A0: case 0x2028: case 0x2029: case 0x202F: case 0x205F: case 0x3000:
        return true;
    default:
        return aChar >= 0x2000 && aChar <= 0x200A;
    }
}

// A character of a name, as KiCad's expression reader reads one: a letter, a digit or '_',
// where any character beyond ASCII counts as a letter (a no-break space too, once a name has
// started).
bool IdentifierPart( wchar_t aChar )
{
    return aChar == L'_' || ( aChar >= L'a' && aChar <= L'z' ) || ( aChar >= L'A' && aChar <= L'Z' )
           || ( aChar >= L'0' && aChar <= L'9' ) || ( aChar >= 0x80 && aChar != 0xFFFD );
}

// The first character of a name: the reader skips whitespace before a name and reads a digit
// as the start of a number.
bool IdentifierStart( wchar_t aChar )
{
    return IdentifierPart( aChar ) && !ExpressionWhitespace( aChar ) && !( aChar >= L'0' && aChar <= L'9' );
}

// Whether the name that ends at aFrom is called: a '(' follows, after any whitespace.
bool CallFollows( const std::wstring& aText, size_t aFrom )
{
    while( aFrom < aText.size() && ExpressionWhitespace( aText[aFrom] ) )
        ++aFrom;
    return aFrom < aText.size() && aText[aFrom] == L'(';
}

std::string Utf8( const std::wstring& aText, size_t aBegin, size_t aEnd )
{
    return wxString( aText.substr( aBegin, aEnd - aBegin ) ).utf8_string();
}

// The position of the brace that closes the one at aOpen, counting every brace; npos when
// none does.
size_t ClosingBrace( const std::wstring& aText, size_t aOpen )
{
    int depth = 0;
    for( size_t i = aOpen; i < aText.size(); ++i )
    {
        if( aText[i] == L'{' ) ++depth;
        else if( aText[i] == L'}' && --depth == 0 ) return i;
    }
    return std::wstring::npos;
}

// The position after the brace that closes the one at aOpen, or the end of an unclosed text.
size_t AfterClosingBrace( const std::wstring& aText, size_t aOpen )
{
    const size_t close = ClosingBrace( aText, aOpen );
    return close == std::wstring::npos ? aText.size() : close + 1;
}

// The position after the quoted string that starts at aQuote, where a backslash escapes the
// character after it, as KiCad's expression reader reads it; the end of an unclosed string.
size_t AfterString( const std::wstring& aText, size_t aQuote )
{
    size_t i = aQuote + 1;
    while( i < aText.size() && aText[i] != aText[aQuote] )
        i += aText[i] == L'\\' && i + 1 < aText.size() ? 2 : 1;
    return std::min( aText.size(), i + 1 );
}

// Where the expression that opens at aOpen ("@{") ends as far as KiCad's evaluator reads it:
// at the brace that closes it counting every brace (it evaluates each expression on its own),
// or where its tokens close it, skipping quoted strings (it reads the whole text),
// whichever is further.
size_t ExpressionEnd( const std::wstring& aText, size_t aOpen )
{
    int level = 1;
    size_t i = aOpen + 2;
    while( i < aText.size() && level > 0 )
    {
        if( ( aText[i] == L'@' || aText[i] == L'$' ) && i + 1 < aText.size() && aText[i + 1] == L'{' )
        {
            ++level;
            i += 2;
        }
        else if( aText[i] == L'}' )
        {
            --level;
            ++i;
        }
        else if( aText[i] == L'"' || aText[i] == L'\'' )
        {
            i = AfterString( aText, i );
        }
        else
        {
            ++i;
        }
    }
    return std::max( i, AfterClosingBrace( aText, aOpen + 1 ) );
}

// The functions KiCad's evaluator can call when it evaluates aText, other than the pure ones,
// added to aCalls. A function that reads outside the design counts wherever a '(' follows its
// name in a text that evaluates an expression, also in a quoted string or beside the
// expression: after a number KiCad's reader takes letters and quote marks as a unit, so
// neither the extent of a quoted string nor that of an expression can hide one. Any other
// name that a '(' follows in an expression counts outside quoted strings and variable
// references KiCad left unresolved: a function KiCad does not know as one of the design alone
// may read outside it too. This can find more calls than KiCad makes, never fewer.
void AddCalls( const wxString& aText, std::set<std::string>& aCalls )
{
    const std::wstring text = aText.ToStdWstring();
    if( text.find( L"@{" ) == std::wstring::npos )
        return;
    for( size_t i = 0; i < text.size(); ++i )
    {
        size_t end = i;
        if( text.compare( i, VCS_FUNCTIONS.size(), VCS_FUNCTIONS ) == 0 )
        {
            end = i + VCS_FUNCTIONS.size();
            while( end < text.size() && IdentifierPart( text[end] ) ) ++end;
        }
        else
        {
            for( std::wstring_view name : OUTSIDE_FUNCTIONS )
                if( text.compare( i, name.size(), name ) == 0
                    && ( i + name.size() == text.size() || !IdentifierPart( text[i + name.size()] ) ) )
                    end = i + name.size();
        }
        if( end > i && CallFollows( text, end ) )
            aCalls.insert( Utf8( text, i, end ) );
    }
    // Each expression as KiCad's evaluator takes them in turn: after one whose braces close, the
    // next that opens after its closing brace.
    for( size_t open = text.find( L"@{" ); open != std::wstring::npos; )
    {
        const size_t end = ExpressionEnd( text, open );
        const size_t close = ClosingBrace( text, open + 1 );
        const size_t next = close == std::wstring::npos ? open + 2 : close + 1;
        for( size_t i = open + 2; i < end; )
        {
            if( text[i] == L'"' || text[i] == L'\'' )
            {
                i = AfterString( text, i );
            }
            else if( text[i] == L'$' && i + 1 < text.size() && text[i + 1] == L'{' )
            {
                i = AfterClosingBrace( text, i + 1 );
            }
            else if( IdentifierStart( text[i] ) && !IdentifierPart( text[i - 1] ) )
            {
                size_t after = i;
                while( after < text.size() && IdentifierPart( text[after] ) ) ++after;
                std::string name = Utf8( text, i, after );
                if( CallFollows( text, after ) && !PURE_EXPRESSION_FUNCTIONS.contains( name ) )
                    aCalls.insert( std::move( name ) );
                i = after;
            }
            else
            {
                ++i;
            }
        }
        open = text.find( L"@{", next );
    }
}

// The end of the name of the variable reference whose name starts at aBegin (after "${"), as
// ExpandTextVars reads it: at the brace that closes it, where the marker of an escaped
// reference or expression keeps its own closing brace.
size_t ReferenceEnd( const std::wstring& aText, size_t aBegin )
{
    int depth = 1;
    size_t i = aBegin;
    while( i < aText.size() )
    {
        bool escaped = false;
        for( std::wstring_view prefix : ESCAPE_MARKERS )
        {
            if( aText.compare( i, prefix.size(), prefix ) != 0 ) continue;
            i += prefix.size();
            for( int inner = 1; i < aText.size() && inner > 0; ++i )
            {
                if( aText[i] == L'{' ) ++inner;
                else if( aText[i] == L'}' ) --inner;
            }
            escaped = true;
            break;
        }
        if( escaped ) continue;
        if( aText[i] == L'{' ) ++depth;
        else if( aText[i] == L'}' && --depth == 0 ) return i;
        ++i;
    }
    return aText.size();
}

// A variable whose name holds a variable or an expression (${REF:UNIT(@{${ROW}-1})}) is looked
// up after KiCad expands the name's variables with the same resolver and evaluates the name's
// expressions (ExpandTextVars), so those expressions are evaluated too. Every reference counts,
// also one nested in another or escaped, and its name to the further of the ends KiCad's
// readers give it: this can find more than KiCad evaluates, never less.
void ReadNameExpressions( const wxString& aText, const RESOLVER& aResolver, int aFlags, TEXT_READS& aReads )
{
    const std::wstring text = aText.ToStdWstring();
    for( size_t open = text.find( L"${" ); open != std::wstring::npos; open = text.find( L"${", open + 2 ) )
    {
        const size_t after = AfterClosingBrace( text, open + 1 );
        const size_t end = std::max( after > open + 2 && text[after - 1] == L'}' ? after - 1 : after,
                                     ReferenceEnd( text, open + 2 ) );
        const wxString name( text.substr( open + 2, end - ( open + 2 ) ) );
        if( !name.Contains( wxS( "${" ) ) && !name.Contains( wxS( "@{" ) ) )
            continue;
        const wxString expanded = ExpandTextVars( name, &aResolver, aFlags );
        if( expanded.Contains( wxS( "@{" ) ) )
        {
            aReads.evaluates = true;
            AddCalls( expanded, aReads.calls );
        }
    }
}

void NoteLiveVariable( const wxString& aToken, TEXT_READS& aReads )
{
    for( const char* name : CAPTURED_LIVE_TEXT )
        if( aToken == name ) aReads.variables.insert( name );
    for( const char* name : TIME_OF_DAY_TEXT )
        if( aToken == name ) aReads.variables.insert( name );
}

// The title-block field a text variable names, as TITLE_BLOCK::TextVarResolver reads it.
std::optional<wxString> TitleBlockField( const TITLE_BLOCK& aTitles, const wxString& aToken )
{
    if( aToken.IsSameAs( wxS( "ISSUE_DATE" ) ) ) return aTitles.GetDate();
    if( aToken.IsSameAs( wxS( "REVISION" ) ) ) return aTitles.GetRevision();
    if( aToken.IsSameAs( wxS( "TITLE" ) ) ) return aTitles.GetTitle();
    if( aToken.IsSameAs( wxS( "COMPANY" ) ) ) return aTitles.GetCompany();
    if( aToken.Len() == 8 && aToken.StartsWith( wxS( "COMMENT" ) ) )
    {
        const wxChar last = aToken.Last();
        if( last >= '1' && last <= '9' )
            return aTitles.GetComment( last - '1' );
    }
    return std::nullopt;
}

// aResolver, recording the live variables a resolution reads, and following a title-block
// field as TITLE_BLOCK::TextVarResolver gives it: with the field's own variables expanded by
// the project's resolver, which evaluates the expressions in those variables' names.
RESOLVER Reading( const BOARD& aBoard, RESOLVER aResolver, int aFlags, TEXT_READS& aReads )
{
    return [&aBoard, resolver = std::move( aResolver ), aFlags, &aReads]( wxString* aToken ) -> bool
    {
        NoteLiveVariable( *aToken, aReads );
        const PROJECT* project = aBoard.GetProject();
        const std::optional<wxString> field = TitleBlockField( aBoard.GetTitleBlock(), *aToken );
        if( field && project && aReads.titleFields.insert( *aToken ).second )
        {
            const RESOLVER projectResolver = [project, &aReads]( wxString* aName ) -> bool
            {
                NoteLiveVariable( *aName, aReads );
                return project->TextVarResolver( aName );
            };
            ReadNameExpressions( *field, projectResolver, aFlags, aReads );
            ExpandTextVars( *field, &projectResolver, aFlags );
        }
        return resolver( aToken );
    };
}

// Resolves aText as KiCad resolves a board text (ResolveTextVars): expand its variables, then
// evaluate its expressions, again while the result holds either, and records what that reads
// from outside the design. It evaluates only when every call is to a pure function or today(),
// whose day the check captures, so each further round reads the text KiCad reads: an
// expression that a variable or another expression builds is found as KiCad evaluates it.
void ReadResolution( wxString aText, const RESOLVER& aResolver, TEXT_READS& aReads )
{
    const int maxDepth = ADVANCED_CFG::GetCfg().m_ResolveTextRecursionDepth;
    EXPRESSION_EVALUATOR evaluator;
    for( int depth = 1; depth <= maxDepth && ( aText.Contains( wxS( "${" ) ) || aText.Contains( wxS( "@{" ) ) );
         ++depth )
    {
        ReadNameExpressions( aText, aResolver, 0, aReads );
        if( aReads.Volatile() )
            return;
        aText = ExpandTextVars( aText, &aResolver );
        if( !aText.Contains( wxS( "@{" ) ) )
            continue;
        aReads.evaluates = true;
        AddCalls( aText, aReads.calls );
        if( aReads.Volatile() )
            return;
        aText = evaluator.Evaluate( aText );
    }
}

// The resolver KiCad gives a board text's variables (PCB_TEXT, PCB_FIELD, PCB_TEXTBOX and
// PCB_TABLECELL::GetShownText; a barcode's text is a PCB_TEXT on the barcode's layer): a table
// cell's position, the item's layer, then its footprint's variables, then the board's.
RESOLVER ItemResolver( const BOARD_ITEM& aItem )
{
    const FOOTPRINT* footprint = aItem.GetParentFootprint();
    const BOARD* board = aItem.GetBoard();
    const PCB_TABLECELL* cell =
            aItem.Type() == PCB_TABLECELL_T ? static_cast<const PCB_TABLECELL*>( &aItem ) : nullptr;
    return [&aItem, footprint, board, cell]( wxString* aToken ) -> bool
    {
        if( cell && aToken->IsSameAs( wxT( "ROW" ) ) )
        {
            *aToken = wxString::Format( wxT( "%d" ), cell->GetRow() + 1 );
            return true;
        }
        if( cell && aToken->IsSameAs( wxT( "COL" ) ) )
        {
            *aToken = wxString::Format( wxT( "%d" ), cell->GetColumn() + 1 );
            return true;
        }
        if( cell && aToken->IsSameAs( wxT( "ADDR" ) ) )
        {
            *aToken = cell->GetAddr();
            return true;
        }
        if( aToken->IsSameAs( wxT( "LAYER" ) ) )
        {
            *aToken = aItem.GetLayerName();
            return true;
        }
        if( footprint && footprint->ResolveTextVar( aToken, 1 ) )
            return true;
        return board && board->ResolveTextVar( aToken, 1 );
    };
}

// Every text of the board a check lays out, with the item whose resolver KiCad gives it: the
// texts, fields, dimensions, table cells and barcodes of the board and its footprints, and a
// field's value in every footprint variant.
void ForEachBoardText( const BOARD& aBoard, const std::function<void( const BOARD_ITEM&, const wxString& )>& aVisit )
{
    auto visit = [&]( const BOARD_ITEM* aItem )
    {
        if( const auto* text = dynamic_cast<const EDA_TEXT*>( aItem ) )
            aVisit( *aItem, text->EDA_TEXT::GetShownText( true, 0 ) );
        else if( aItem->Type() == PCB_BARCODE_T )
            aVisit( *aItem, UnescapeString( static_cast<const PCB_BARCODE*>( aItem )->GetText() ) );
        if( aItem->Type() != PCB_FOOTPRINT_T )
            return;
        const auto* footprint = static_cast<const FOOTPRINT*>( aItem );
        for( const auto& [name, variant] : footprint->GetVariants() )
        {
            for( const auto& [field, value] : variant.GetFields() )
            {
                const PCB_FIELD* owner = footprint->GetField( field );
                aVisit( owner ? static_cast<const BOARD_ITEM&>( *owner ) : *aItem, UnescapeString( value ) );
            }
        }
    };
    for( BOARD_ITEM* item : aBoard.GetItemSet() )
    {
        visit( item );
        item->RunOnChildren( visit, RECURSE_MODE::RECURSE );
    }
}

// What the board's texts read from outside the design as KiCad resolves them.
TEXT_READS ReadBoardTexts( const BOARD& aBoard )
{
    TEXT_READS reads;
    ForEachBoardText( aBoard, [&]( const BOARD_ITEM& aItem, const wxString& aText )
    {
        if( !aText.Contains( wxS( "${" ) ) && !aText.Contains( wxS( "@{" ) ) )
            return;
        TEXT_READS text;
        ReadResolution( aText, Reading( aBoard, ItemResolver( aItem ), 0, text ), text );
        reads.Add( text );
    } );
    return reads;
}

// The resolver the check's drawing-sheet test gives the sheet's texts: that of
// DS_DRAW_ITEM_LIST::BuildFullText with what DRC_TEST_PROVIDER_MISC sets (page 1 of 1 of
// "dummyFilename", sheet "dummySheet" on layer "dummyLayer", no sheet path, variant or board
// properties), the board's paper, title block and project. aTitleBlock is false while it
// expands a title-block field's value again, as KiCad does.
RESOLVER SheetResolver( const BOARD& aBoard, bool aTitleBlock, TEXT_READS& aReads )
{
    return [&aBoard, aTitleBlock, &aReads]( wxString* aToken ) -> bool
    {
        const PROJECT* project = aBoard.GetProject();
        bool updated = true;
        if( aToken->IsSameAs( wxT( "KICAD_VERSION" ) ) && PgmOrNull() )
            *aToken = wxString::Format( wxT( "%s %s" ), wxT( "KiCad E.D.A." ), GetBaseVersion() );
        else if( aToken->IsSameAs( wxT( "#" ) ) || aToken->IsSameAs( wxT( "##" ) ) )
            *aToken = wxT( "1" );
        else if( aToken->IsSameAs( wxT( "SHEETNAME" ) ) )
            *aToken = wxT( "dummySheet" );
        else if( aToken->IsSameAs( wxT( "SHEETPATH" ) ) || aToken->IsSameAs( wxT( "VARIANT" ) )
                 || aToken->IsSameAs( wxT( "VARIANT_DESC" ) ) )
            *aToken = wxEmptyString;
        else if( aToken->IsSameAs( wxT( "FILENAME" ) ) )
            *aToken = wxFileName( wxT( "dummyFilename" ) ).GetFullName();
        else if( aToken->IsSameAs( wxT( "FILEPATH" ) ) )
        {
            *aToken = wxFileName( wxT( "dummyFilename" ) ).GetFullPath();
            return true;
        }
        else if( aToken->IsSameAs( wxT( "PAPER" ) ) )
            *aToken = aBoard.GetPageSettings().GetTypeAsString();
        else if( aToken->IsSameAs( wxT( "LAYER" ) ) )
            *aToken = wxT( "dummyLayer" );
        else
            updated = false;

        if( updated )
        {
            if( project )
            {
                const RESOLVER projectResolver = Reading( aBoard,
                        [project]( wxString* aName ) { return project->TextVarResolver( aName ); }, FOR_ERC_DRC,
                        aReads );
                *aToken = ExpandTextVars( *aToken, &projectResolver, FOR_ERC_DRC );
            }
            return true;
        }
        if( aTitleBlock && aBoard.GetTitleBlock().TextVarResolver( aToken, project, FOR_ERC_DRC ) )
        {
            const RESOLVER again = Reading( aBoard, SheetResolver( aBoard, false, aReads ), FOR_ERC_DRC, aReads );
            ReadNameExpressions( *aToken, again, FOR_ERC_DRC, aReads );
            *aToken = ExpandTextVars( *aToken, &again, FOR_ERC_DRC );
            return true;
        }
        return project && project->TextVarResolver( aToken );
    };
}

// The drawing sheet's texts, which the check lays out to report the variables they leave
// unresolved; nothing else of the sheet reaches a finding. An empty sheet that may not be
// empty is laid out as KiCad's default sheet (DS_DRAW_ITEM_LIST::BuildDrawItemsList).
std::vector<wxString> DrawingSheetTexts( DS_DATA_MODEL& aDrawing )
{
    if( aDrawing.GetCount() == 0 && !aDrawing.VoidListAllowed() )
    {
        DS_DATA_MODEL defaults;
        defaults.LoadDrawingSheet( wxEmptyString, nullptr );
        return defaults.GetCount() == 0 ? std::vector<wxString>() : DrawingSheetTexts( defaults );
    }
    std::vector<wxString> texts;
    for( unsigned index = 0; index < aDrawing.GetCount(); ++index )
    {
        const DS_DATA_ITEM* item = aDrawing.GetItem( index );
        if( item && item->GetType() == DS_DATA_ITEM::DS_TEXT )
            texts.push_back( static_cast<const DS_DATA_ITEM_TEXT*>( item )->m_TextBase );
    }
    return texts;
}

// What the drawing sheet's texts read from outside the design, as the check resolves them:
// BuildFullText expands a text's variables and then evaluates its expressions once. Plain
// variables always resolve there, whatever their value; the live variables a text reads count
// only when the text evaluates an expression, whose result they can decide.
TEXT_READS ReadDrawingSheetTexts( DS_DATA_MODEL& aDrawing, const BOARD& aBoard )
{
    TEXT_READS reads;
    for( const wxString& base : DrawingSheetTexts( aDrawing ) )
    {
        if( !base.Contains( wxS( "${" ) ) && !base.Contains( wxS( "@{" ) ) )
            continue;
        TEXT_READS text;
        const RESOLVER resolver = Reading( aBoard, SheetResolver( aBoard, true, text ), FOR_ERC_DRC, text );
        ReadNameExpressions( base, resolver, FOR_ERC_DRC, text );
        const wxString expanded = ExpandTextVars( base, &resolver, FOR_ERC_DRC );
        if( expanded.Contains( wxS( "@{" ) ) )
        {
            text.evaluates = true;
            AddCalls( expanded, text.calls );
        }
        if( !text.evaluates )
            text.variables.clear();
        reads.Add( text );
    }
    return reads;
}

// "a(), b() and c()" style list of names.
std::string NameList( const std::vector<std::string>& aNames )
{
    std::string list;
    for( size_t index = 0; index < aNames.size(); ++index )
        list += ( index == 0 ? "" : index + 1 == aNames.size() ? " and " : ", " ) + aNames[index];
    return list;
}

// The value of a captured live text variable now, as the board's texts resolve it.
std::string LiveTextValue( const BOARD& aBoard, const std::string& aName )
{
    if( aName == "CURRENT_DATE" ) return TITLE_BLOCK::GetCurrentDate().utf8_string();
    wxString token = wxString::FromUTF8( aName );
    if( const PROJECT* project = aBoard.GetProject(); project && project->TextVarResolver( &token ) )
        return token.utf8_string();
    return "${" + aName + "}"; // Unresolved: shown as written.
}

// The day today() gives now, as the expression evaluator itself gives it.
std::string TodayValue()
{
    EXPRESSION_EVALUATOR evaluator;
    return evaluator.Evaluate( wxS( "@{today()}" ) ).utf8_string();
}

// The font files of the outline fonts the texts of aBoard are laid out with, other than fonts
// embedded in the board, whose content is part of the board. Font files the check could not
// read are listed in aUnreadable.
std::map<wxString, FILE_CONTENT_BASELINE> FontFiles( const BOARD& aBoard, std::vector<wxString>& aUnreadable )
{
    std::set<wxString> embedded;
    if( const EMBEDDED_FILES* files = aBoard.GetEmbeddedFiles() )
        if( const std::vector<wxString>* fonts = files->GetFontFiles() )
            embedded.insert( fonts->begin(), fonts->end() );
    std::map<wxString, FILE_CONTENT_BASELINE> result;
    auto add = [&]( const BOARD_ITEM* aItem )
    {
        const auto* text = dynamic_cast<const EDA_TEXT*>( aItem );
        const KIFONT::FONT* font = text ? text->GetFont() : nullptr;
        if( !font || !font->IsOutline() ) return;
        const wxString& path = static_cast<const KIFONT::OUTLINE_FONT*>( font )->GetFileName();
        if( path.empty() || embedded.contains( path ) || result.contains( path )
            || std::find( aUnreadable.begin(), aUnreadable.end(), path ) != aUnreadable.end() )
            return;
        FILE_CONTENT_BASELINE content = FILE_CONTENT_BASELINE::Read( path );
        if( content.Known() && content.Exists() ) result.emplace( path, std::move( content ) );
        else aUnreadable.push_back( path );
    };
    for( BOARD_ITEM* item : aBoard.GetItemSet() )
    {
        add( item );
        item->RunOnChildren( add, RECURSE_MODE::RECURSE );
    }
    return result;
}

bool SameContent( const FILE_CONTENT_BASELINE& aCaptured, const FILE_CONTENT_BASELINE& aNow )
{
    return aCaptured.Known() && aNow.Known() && aNow.Exists() == aCaptured.Exists()
           && aNow.Bytes() == aCaptured.Bytes() && aNow.Sha256() == aCaptured.Sha256();
}

nlohmann::json ProjectInputs( const BOARD& aBoard )
{
    const PROJECT* project = aBoard.GetProject();
    const auto& settings = aBoard.GetDesignSettings();
    nlohmann::json result = {
        { "project_path", project ? project->GetProjectFullName().ToStdString() : "" },
        // The editor's current variant resolves variant field values and ${VARIANT}; it
        // lives only in memory and switching it is no board edit.
        { "current_variant", aBoard.GetCurrentVariant().utf8_string() },
        { "project", project ? project->GetProjectFile().CaptureCurrentState() : nlohmann::json() },
        { "board_settings", settings.CaptureCurrentState() },
        { "net_settings", settings.m_NetSettings
                ? settings.m_NetSettings->CaptureCurrentState() : nlohmann::json() }
    };
    // Exclusion comments can change on live markers before project settings save.
    result["effective_exclusions"] = nlohmann::json::array();
    for( const auto& exclusion : PCB_PROJECT_EDITOR_STATE::Exclusions( aBoard ) )
        result["effective_exclusions"].push_back( exclusion );
    // The date or revision the board's texts show, only when KiCad reads one resolving
    // them, so that a board without them does not go stale at midnight: a date or revision
    // variable, or the day an expression reads through today(). Every read compares them
    // again. The time of day and expressions that read the clock, chance or the repository
    // are no captured value (PCB_DRC_RUN_INPUTS::Gaps).
    const TEXT_READS texts = ReadBoardTexts( aBoard );
    for( const char* name : CAPTURED_LIVE_TEXT )
        if( texts.variables.contains( name ) ) result["live_text"][name] = LiveTextValue( aBoard, name );
    if( texts.calls.contains( std::string( TODAY ) ) ) result["live_text"]["today()"] = TodayValue();
    return result;
}
}

const FILE_CONTENT_BASELINE& PCB_DRC_PROJECT_OBSERVATION::Font( const wxString& aPath ) const
{
    auto found = m_fonts.find( aPath );
    if( found == m_fonts.end() ) found = m_fonts.emplace( aPath, FILE_CONTENT_BASELINE::Read( aPath ) ).first;
    return found->second;
}

PCB_DRC_PROJECT_OBSERVATION PCB_DRC_PROJECT_BASELINE::Observe( const BOARD& aBoard )
{
    PCB_DRC_PROJECT_OBSERVATION result;
    result.settings = ProjectInputs( aBoard );
    result.rulesPath = aBoard.GetDesignRulesPath();
    if( !result.rulesPath.empty() ) result.rules = FILE_CONTENT_BASELINE::Read( result.rulesPath );
    return result;
}

bool PCB_DRC_PROJECT_BASELINE::Unchanged( const BOARD& aBoard ) const
{
    try
    {
        return Unchanged( Observe( aBoard ) );
    }
    catch( const std::exception& )
    {
        // Unreadable or unrepresentable inputs are not evidence of freshness.
        return false;
    }
}

bool PCB_DRC_PROJECT_BASELINE::Unchanged( const PCB_DRC_PROJECT_OBSERVATION& aObserved ) const
{
    try
    {
        // The same comparison as FILE_CONTENT_BASELINE::Check, against the rules file
        // content the observation already read: an unreadable file, another path or
        // other bytes are never unchanged.
        const FILE_CONTENT_BASELINE& now = aObserved.rules;
        const bool rulesUnchanged = m_rules.Path().empty() ? aObserved.rulesPath.empty()
                : FILE_CONTENT_BASELINE::SamePath( m_rules.Path(), aObserved.rulesPath )
                  && SameContent( m_rules, now );
        if( !rulesUnchanged || m_settings != aObserved.settings ) return false;
        // A font file rewritten in place (even with the same size and modification time)
        // gives the texts other glyphs.
        for( const auto& [path, captured] : m_fonts )
            if( !SameContent( captured, aObserved.Font( path ) ) ) return false;
        return true;
    }
    catch( const std::exception& )
    {
        // Unreadable or unrepresentable inputs are not evidence of freshness.
        return false;
    }
}

PCB_DRC_AUXILIARY_BASELINE PCB_DRC_AUXILIARY_BASELINE::Capture(
        const PCB_DRC_CAPTURE_CONTEXT& aContext )
{
    wxString drawing;
    aContext.drawing.SaveInString( &drawing );
    PCB_DRC_AUXILIARY_BASELINE result;
    result.m_state = {
        { "drawing_identity", aContext.drawingIdentity.AsStdString() },
        { "drawing", drawing.utf8_string() },
        { "allow_empty_drawing", aContext.drawing.VoidListAllowed() },
        { "routing", aContext.routingSettings
                ? aContext.routingSettings->CaptureCurrentState() : nlohmann::json() }
    };
    return result;
}

bool PCB_DRC_AUXILIARY_BASELINE::Unchanged( const PCB_DRC_CAPTURE_CONTEXT& aContext ) const
{
    try { return m_state == Capture( aContext ).m_state; }
    catch( const std::exception& ) { return false; }
}

std::string PCB_DRC_AUXILIARY_BASELINE::Fingerprint() const
{
    NATIVE_STATE_DIGEST digest;
    digest.Append( m_state.dump() );
    return digest.Hex();
}

std::unique_ptr<PCB_DRC_RUN_INPUTS> PCB_DRC_RUN_INPUTS::Capture(
        BOARD& aBoard, const PCB_DRC_CAPTURE_CONTEXT& aContext, PROGRESS_REPORTER* aReporter )
{
    const int revision = aBoard.GetTimeStamp();
    const KIID identity = aBoard.m_Uuid;
    auto result = std::unique_ptr<PCB_DRC_RUN_INPUTS>( new PCB_DRC_RUN_INPUTS );
    result->m_projectBaseline.m_settings = ProjectInputs( aBoard );
    const TEXT_READS texts = ReadBoardTexts( aBoard );
    for( const char* name : TIME_OF_DAY_TEXT )
        if( texts.variables.contains( name ) ) result->m_timeOfDayText.emplace_back( name );
    for( const std::string& name : texts.calls )
        if( name != TODAY ) result->m_volatileExpressions.push_back( name + "()" );
    // The drawing sheet's expressions: the check reports the variables a sheet text leaves
    // unresolved, and an expression that reads the day, the time, chance or the repository
    // can change that while the check's copy of the sheet stays the same.
    const TEXT_READS sheet = ReadDrawingSheetTexts( aContext.drawing, aBoard );
    for( const std::string& name : sheet.calls ) result->m_drawingSheetExpressions.push_back( name + "()" );
    for( const std::string& name : sheet.variables ) result->m_drawingSheetExpressions.push_back( "${" + name + "}" );
    result->m_auxiliaryBaseline = PCB_DRC_AUXILIARY_BASELINE::Capture( aContext );
    if( aContext.routingSettings )
    {
        result->m_routingSettings = std::make_unique<PNS::ROUTING_SETTINGS>(
                nullptr, aContext.routingSettings->GetPath() );
        aContext.routingSettings->CopyCurrentStateTo( *result->m_routingSettings );
    }
    result->m_sourceDrawingIdentity = aContext.drawingIdentity;
    const wxString rulesPath = aBoard.GetDesignRulesPath();
    if( !rulesPath.empty() )
    {
        result->m_rulesBaseline = FILE_CONTENT_BASELINE::Read( rulesPath, &result->m_rulesText );
        if( !result->m_rulesBaseline.Known() )
            throw std::runtime_error( "Custom design rules could not be captured" );
    }
    result->m_projectBaseline.m_rules = result->m_rulesBaseline;
    result->m_document = PCB_DRC_DOCUMENT_SNAPSHOT::Capture( aBoard );
    // The fonts of the check's own copy: the texts it lays out.
    result->m_projectBaseline.m_fonts = FontFiles( result->GetBoard(), result->m_unreadableFonts );
    result->m_libraries = DRC_LIBRARY_INPUTS::Capture( aBoard, aContext.libraries, aReporter );
    if( !result->m_libraries ) return nullptr;
    result->m_drawing = aContext.drawing.CloneForRendering();
    BOARD& copy = result->GetBoard();
    result->m_proxy = std::make_unique<DS_PROXY_VIEW_ITEM>( pcbIUScale, &copy.GetPageSettings(),
            copy.GetProject(), &copy.GetTitleBlock(), &copy.GetProperties() );
    if( aReporter && aReporter->IsCancelled() ) return nullptr;
    if( aBoard.GetTimeStamp() != revision || aBoard.m_Uuid != identity
            || !result->m_auxiliaryBaseline.Unchanged( aContext )
            || !result->m_projectBaseline.Unchanged( aBoard ) )
        throw std::runtime_error( "Native DRC inputs changed during capture" );
    return result;
}

void PCB_DRC_RUN_INPUTS::InitializeEngine( DRC_ENGINE& aEngine ) const
{
    aEngine.InitEngineFromText( m_rulesText, m_rulesBaseline.Known()
            ? m_rulesBaseline.Path() : wxString( "captured implicit design rules" ) );
}

void PCB_DRC_RUN_INPUTS::BindInvocation( DRC_ENGINE& aEngine )
{
    if( !m_drawing ) throw std::logic_error( "Captured DRC input bundle already consumed" );
    aEngine.SetLibraryInputs( m_libraries );
    aEngine.SetDrawingSheet( m_proxy.get() );
    aEngine.SetDrawingSheetModel( std::move( m_drawing ) );
    if( m_schematic ) aEngine.SetSchematicNetlist( &m_schematic->Netlist() );
}

void PCB_DRC_RUN_INPUTS::SetSchematicInput( std::unique_ptr<PCB_DRC_SCHEMATIC_INPUT> aInput )
{
    if( !m_drawing || m_schematic || !aInput )
        throw std::logic_error( "Schematic input must be attached once before starting DRC" );
    m_schematic = std::move( aInput );
}

const KIID& PCB_DRC_RUN_INPUTS::CapturedDrawingIdentity() const { return m_proxy->m_Uuid; }

std::string PCB_DRC_RUN_INPUTS::LibraryFingerprint() const
{
    return m_libraries->ContentFingerprint();
}

std::map<wxString, std::string> PCB_DRC_RUN_INPUTS::LibraryFingerprints() const
{
    return m_libraries->LibraryFingerprints();
}

tl::expected<std::vector<KIID>, std::string> PCB_DRC_RUN_INPUTS::AddCandidateItems(
        const google::protobuf::RepeatedPtrField<google::protobuf::Any>& aItems )
{
    if( aItems.empty() ) return std::vector<KIID>();
    BOARD& source = GetBoard();
    std::vector<KIID> identities;
    identities.reserve( aItems.size() );
    for( const google::protobuf::Any& encoded : aItems )
    {
        const std::optional<KICAD_T> type = kiapi::common::TypeNameFromAny( encoded );
        if( !type || ( *type != PCB_TRACE_T && *type != PCB_ARC_T && *type != PCB_VIA_T ) )
            return tl::unexpected( "Candidate DRC accepts only Track, Arc and Via items" );
        const std::optional<std::string> netName = CandidateNetName( *type, encoded );
        if( !netName || netName->empty() )
            return tl::unexpected( "Candidate DRC requires an explicit net name" );
        if( !source.FindNet( wxString::FromUTF8( *netName ) ) )
            return tl::unexpected( "Candidate DRC requires an existing board net" );
        std::unique_ptr<BOARD_ITEM> item = CreateItemForType( *type, &source );
        if( !item || !item->Deserialize( encoded ) )
            return tl::unexpected( "Candidate DRC could not deserialize a native route item" );
        if( item->m_Uuid == niluuid || source.ResolveItem( item->m_Uuid, true )
            || std::find( identities.begin(), identities.end(), item->m_Uuid ) != identities.end() )
            return tl::unexpected( "Candidate DRC requires distinct nonempty item identities" );
        auto* connected = dynamic_cast<BOARD_CONNECTED_ITEM*>( item.get() );
        if( !connected || connected->GetNetCode() <= 0 )
            return tl::unexpected( "Candidate DRC requires a connected existing net" );
        identities.push_back( item->m_Uuid );
        source.Add( item.release() );
    }
    m_candidates.insert( identities.begin(), identities.end() );
    return identities;
}

std::vector<PCB_DRC_INPUT_GAP> PCB_DRC_RUN_INPUTS::Gaps() const
{
    std::vector<PCB_DRC_INPUT_GAP> gaps;
    const auto& gap = m_document->IdentityGap();
    if( !gap.Empty() )
    {
        gaps.push_back( { "item_identity",
                fmt::format( "The check's copy of the board does not keep the identity of every object of the open "
                             "board ({} missing from the copy, {} in the copy but not in the open board, {} identities "
                             "held by more than one object), so a finding could name an object that does not exist "
                             "in the open board.",
                             gap.missing, gap.unexpected, gap.shared ) } );
    }
    if( !m_timeOfDayText.empty() )
    {
        std::string names;
        for( const std::string& name : m_timeOfDayText ) names += ( names.empty() ? "${" : ", ${" ) + name + "}";
        gaps.push_back( { "current_time_text",
                fmt::format( "The board's text can show the time of day ({}), which changes while the check runs, "
                             "so the check cannot read one captured value of it.", names ) } );
    }
    if( !m_volatileExpressions.empty() )
    {
        gaps.push_back( { "volatile_text_expression",
                fmt::format( "The board's text evaluates @{{...}} expressions that call {}, which can give another "
                             "value each time KiCad evaluates them (the time, a random number, the state of the "
                             "project's version-control repository) or are not functions KiCad evaluates from the "
                             "design alone, so the check cannot read one captured value of them.",
                             NameList( m_volatileExpressions ) ) } );
    }
    if( !m_drawingSheetExpressions.empty() )
    {
        gaps.push_back( { "volatile_text_expression",
                fmt::format( "The drawing sheet's text evaluates @{{...}} expressions that read {}, which can change "
                             "while the check runs and with it which variables the sheet leaves unresolved, so the "
                             "check cannot read one captured value of them.",
                             NameList( m_drawingSheetExpressions ) ) } );
    }
    if( !m_unreadableFonts.empty() )
    {
        std::vector<std::string> paths;
        for( const wxString& path : m_unreadableFonts ) paths.push_back( path.utf8_string() );
        gaps.push_back( { "font_file",
                fmt::format( "The board's text is laid out with the font file {}, which the check could not read, so it "
                             "cannot tell whether the file still holds the glyphs the check laid out.",
                             NameList( paths ) ) } );
    }
    return gaps;
}

void PCB_DRC_RUN_INPUTS::MapGeneratedItems()
{
    const std::set<KIID>& source = m_document->SourceIdentities();
    m_generated.clear();
    for( BOARD_ITEM* item : GetBoard().GetItemSet() )
    {
        if( item->Type() == PCB_MARKER_T || source.contains( item->m_Uuid ) || m_candidates.contains( item->m_Uuid ) )
            continue;
        // A tuning pattern regenerates its tracks with new identities; the pattern is the
        // object of the open board a person selects and edits.
        if( EDA_GROUP* group = item->GetParentGroup() )
        {
            const EDA_ITEM* owner = group->AsEdaItem();
            if( owner->Type() == PCB_GENERATOR_T && source.contains( owner->m_Uuid ) )
            {
                m_generated.emplace( item->m_Uuid, owner->m_Uuid );
                continue;
            }
        }
        // A rebuilt teardrop is the open board's teardrop only when it is the same one.
        if( item->Type() == PCB_ZONE_T )
            if( auto teardrop = m_document->SourceTeardrop( *static_cast<const ZONE*>( item ) ) )
                m_generated.emplace( item->m_Uuid, *teardrop );
    }
}

bool PCB_DRC_RUN_INPUTS::ResolveFindingItem( KIID& aIdentity ) const
{
    if( aIdentity == niluuid ) return true; // Names no object.
    if( m_proxy && aIdentity == m_proxy->m_Uuid )
    {
        aIdentity = m_sourceDrawingIdentity;
        return true;
    }
    if( m_candidates.contains( aIdentity ) ) return true;
    if( const auto generated = m_generated.find( aIdentity ); generated != m_generated.end() )
    {
        aIdentity = generated->second;
        return true;
    }
    return m_document->SourceIdentities().contains( aIdentity );
}

bool PCB_DRC_RUN_INPUTS::HasLibraryDependencies() const
{
    return m_libraries->Size() != 0;
}

bool PCB_DRC_RUN_INPUTS::RulesUnchanged() const
{
    // An absent project/rule path is explicitly implicit-only, not a failed read.
    return m_rulesBaseline.Path().empty()
            || m_rulesBaseline.Check( m_rulesBaseline.Path() ) == FILE_BASELINE_CHECK::UNCHANGED;
}
