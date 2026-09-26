/* Owned native DRC input bundle. GPL-3.0-or-later. */
#include "pcb_drc_run_inputs.h"
#include "pcb_drc_document_snapshot.h"
#include "pcb_drc_schematic_input.h"
#include <api/api_pcb_utils.h>
#include <api/api_utils.h>
#include <board.h>
#include <board_connected_item.h>
#include <api/native_state_digest.h>
#include <board_design_settings.h>
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
#include <title_block.h>
#include <pcb_marker.h>
#include <zone.h>
#include <fmt/format.h>
#include <algorithm>
#include <map>
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

// The functions known to read outside the design: the day, the time (now()), chance
// (random()) and the project's version-control repository (vcs...()). Written inside an
// @{...} expression, any function that is not pure counts as such too (ScanTexts): a
// function this list does not know may be one whose value the check cannot capture.
bool ReadsOutsideTheDesign( std::string_view aName )
{
    return aName == TODAY || aName == "now" || aName == "random" || aName.starts_with( "vcs" );
}

bool IdentifierStart( wchar_t aChar )
{
    // As the expression lexer: any character beyond ASCII counts as a letter.
    return aChar == L'_' || ( aChar >= L'a' && aChar <= L'z' ) || ( aChar >= L'A' && aChar <= L'Z' )
           || ( aChar >= 0x80 && aChar != 0xFFFD );
}

bool IdentifierPart( wchar_t aChar ) { return IdentifierStart( aChar ) || ( aChar >= L'0' && aChar <= L'9' ); }

// The position after the brace that closes the one at aOpen, or the end of an unclosed text.
size_t AfterClosingBrace( const std::wstring& aText, size_t aOpen )
{
    int depth = 0;
    for( size_t i = aOpen; i < aText.size(); ++i )
    {
        if( aText[i] == L'{' ) ++depth;
        else if( aText[i] == L'}' && --depth == 0 ) return i + 1;
    }
    return aText.size();
}

// The names of the functions called in aText[aBegin, aEnd): an identifier followed by '('.
// Inside an expression, a variable reference ${...} is replaced before evaluation, so its
// own name is no call (aSkipReferences). The search is lexical: a quoted string counts
// too, so it can find more than KiCad evaluates, never less.
std::vector<std::string> CallNames( const std::wstring& aText, size_t aBegin, size_t aEnd, bool aSkipReferences )
{
    std::vector<std::string> names;
    size_t i = aBegin;
    while( i < aEnd )
    {
        if( aSkipReferences && aText[i] == L'$' && i + 1 < aEnd && aText[i + 1] == L'{' )
        {
            i = std::min( aEnd, AfterClosingBrace( aText, i + 1 ) );
            continue;
        }
        if( IdentifierStart( aText[i] ) && ( i == aBegin || !IdentifierPart( aText[i - 1] ) ) )
        {
            size_t end = i;
            while( end < aEnd && IdentifierPart( aText[end] ) ) ++end;
            size_t next = end;
            while( next < aEnd && ( aText[next] == L' ' || aText[next] == L'\t' ) ) ++next;
            if( next < aEnd && aText[next] == L'(' )
                names.push_back( wxString( aText.substr( i, end - i ) ).utf8_string() );
            i = end;
            continue;
        }
        ++i;
    }
    return names;
}

// The @{...} expressions of one text: the contents of each, and whether a value substituted
// into the text can become part of an expression: an expression that refers to a variable
// or field (${...}), text "@${...}" whose substituted value starts an expression, or a value
// ending in '@' that starts one in the text it is substituted into. An escaped \@{ is text.
struct EXPRESSION_SPANS
{
    std::vector<std::pair<size_t, size_t>> spans;
    bool substitutes = false;
};

EXPRESSION_SPANS ExpressionSpans( const std::wstring& aText )
{
    EXPRESSION_SPANS result;
    for( size_t i = 0; i + 1 < aText.size(); ++i )
    {
        if( aText[i] != L'@' || ( i > 0 && aText[i - 1] == L'\\' ) ) continue;
        if( aText[i + 1] == L'{' )
        {
            const size_t after = AfterClosingBrace( aText, i + 1 );
            const size_t end = after > i + 2 && aText[after - 1] == L'}' ? after - 1 : after;
            result.spans.emplace_back( i + 2, end );
            if( std::wstring_view( aText ).substr( i + 2, end - ( i + 2 ) ).find( L"${" ) != std::wstring_view::npos )
                result.substitutes = true;
            i = after - 1;
        }
        else if( aText[i + 1] == L'$' && i + 2 < aText.size() && aText[i + 2] == L'{' )
        {
            result.substitutes = true;
        }
    }
    if( !aText.empty() && aText.back() == L'@' ) result.substitutes = true;
    return result;
}

// What the texts a check reads take from outside the design.
struct TEXT_SCAN
{
    std::set<std::string> variables;           // Live text variables a text can show.
    std::set<std::string> expressionVariables; // Live text variables an expression can read.
    std::set<std::string> calls;               // Functions an expression calls, other than the pure ones.
    bool expressions = false;                  // A text evaluates an expression.
};

// The names a text refers to with ${NAME}. A reference whose name is built from another
// reference or an expression (${${NAME}}, ${REF:UNIT(${ROW})}) can name any definition
// (aAnyName). An escaped \${ is text.
void ReferencedNames( const std::wstring& aText, std::vector<wxString>& aNames, bool& aAnyName )
{
    for( size_t i = 0; i + 1 < aText.size(); ++i )
    {
        if( aText[i] != L'$' || aText[i + 1] != L'{' || ( i > 0 && aText[i - 1] == L'\\' ) ) continue;
        const size_t after = AfterClosingBrace( aText, i + 1 );
        const size_t end = after > i + 2 && aText[after - 1] == L'}' ? after - 1 : after;
        const std::wstring name = aText.substr( i + 2, end - ( i + 2 ) );
        if( name.find( L"${" ) != std::wstring::npos || name.find( L"@{" ) != std::wstring::npos ) aAnyName = true;
        else aNames.emplace_back( name );
        i = after - 1;
    }
}

// Scans the texts a check lays out or resolves (aSources) and the definitions their variable
// references reach, by name and transitively: project text variables, board properties,
// title-block fields and variant descriptions (object fields are sources themselves). A
// reference whose name is built at resolution time, or an expression that builds text (it
// quotes a string, which can form a reference), can reach every definition. A value
// substituted into an expression becomes part of it, so then every reachable text counts for
// the functions that read outside the design. The search is by name and lexical: it can find
// more than the texts show, never less.
TEXT_SCAN ScanTexts( const std::vector<wxString>& aSources,
                     const std::multimap<wxString, wxString>& aDefinitions )
{
    TEXT_SCAN scan;
    std::vector<std::wstring> reachable;
    for( const wxString& text : aSources ) reachable.push_back( text.ToStdWstring() );
    std::set<wxString> followed;
    bool everyDefinition = false;
    for( size_t index = 0; index < reachable.size() && !everyDefinition; ++index )
    {
        std::vector<wxString> names;
        ReferencedNames( reachable[index], names, everyDefinition );
        for( const auto& [begin, end] : ExpressionSpans( reachable[index] ).spans )
            if( std::wstring_view( reachable[index] ).substr( begin, end - begin ).find_first_of( L"\"'" )
                != std::wstring_view::npos )
                everyDefinition = true;
        for( const wxString& name : names )
        {
            if( !followed.insert( name ).second ) continue;
            const auto [first, last] = aDefinitions.equal_range( name );
            for( auto definition = first; definition != last; ++definition )
                reachable.push_back( definition->second.ToStdWstring() );
        }
    }
    if( everyDefinition )
    {
        reachable.resize( aSources.size() );
        for( const auto& [name, value] : aDefinitions ) reachable.push_back( value.ToStdWstring() );
    }
    auto liveNames = [&]( std::wstring_view aText, std::set<std::string>& aFound )
    {
        auto find = [&]( const char* aName )
        {
            if( aText.find( wxString( aName ).ToStdWstring() ) != std::wstring_view::npos ) aFound.insert( aName );
        };
        for( const char* name : CAPTURED_LIVE_TEXT ) find( name );
        for( const char* name : TIME_OF_DAY_TEXT ) find( name );
    };
    bool substitutes = false;
    for( const std::wstring& text : reachable )
    {
        liveNames( text, scan.variables );
        const EXPRESSION_SPANS found = ExpressionSpans( text );
        substitutes |= found.substitutes;
        for( const auto& [begin, end] : found.spans )
        {
            scan.expressions = true;
            liveNames( std::wstring_view( text ).substr( begin, end - begin ), scan.expressionVariables );
            for( std::string& name : CallNames( text, begin, end, true ) )
                if( !PURE_EXPRESSION_FUNCTIONS.contains( name ) ) scan.calls.insert( std::move( name ) );
        }
    }
    if( substitutes )
    {
        scan.expressions = true;
        for( const std::wstring& text : reachable )
        {
            liveNames( text, scan.expressionVariables );
            for( std::string& name : CallNames( text, 0, text.size(), false ) )
                if( ReadsOutsideTheDesign( name ) ) scan.calls.insert( std::move( name ) );
        }
    }
    return scan;
}

// Every text of the board a check lays out: the texts, fields, dimensions, table cells and
// barcodes of the board and its footprints, and the field values of every footprint variant.
std::vector<wxString> BoardTexts( const BOARD& aBoard )
{
    std::vector<wxString> texts;
    auto add = [&]( const BOARD_ITEM* aItem )
    {
        if( const auto* text = dynamic_cast<const EDA_TEXT*>( aItem ) ) texts.push_back( text->GetText() );
        else if( aItem->Type() == PCB_BARCODE_T ) texts.push_back( static_cast<const PCB_BARCODE*>( aItem )->GetText() );
        if( aItem->Type() == PCB_FOOTPRINT_T )
            for( const auto& [name, variant] : static_cast<const FOOTPRINT*>( aItem )->GetVariants() )
                for( const auto& [field, value] : variant.GetFields() ) texts.push_back( value );
    };
    for( BOARD_ITEM* item : aBoard.GetItemSet() )
    {
        add( item );
        item->RunOnChildren( add, RECURSE_MODE::RECURSE );
    }
    return texts;
}

// The definitions a text reaches through a variable reference, by the name it refers to.
std::multimap<wxString, wxString> TextDefinitions( const BOARD& aBoard )
{
    std::multimap<wxString, wxString> definitions;
    if( const PROJECT* project = aBoard.GetProject() )
        for( const auto& [name, value] : project->GetTextVars() ) definitions.emplace( name, value );
    for( const auto& [name, value] : aBoard.GetProperties() ) definitions.emplace( name, value );
    const TITLE_BLOCK& titles = aBoard.GetTitleBlock();
    definitions.emplace( wxS( "TITLE" ), titles.GetTitle() );
    definitions.emplace( wxS( "ISSUE_DATE" ), titles.GetDate() );
    definitions.emplace( wxS( "REVISION" ), titles.GetRevision() );
    definitions.emplace( wxS( "COMPANY" ), titles.GetCompany() );
    for( int comment = 0; comment < 9; ++comment )
        definitions.emplace( wxString::Format( wxS( "COMMENT%d" ), comment + 1 ), titles.GetComment( comment ) );
    // Every variant's description, not only the current variant's.
    for( const wxString& variant : aBoard.GetVariantNames() )
        definitions.emplace( wxS( "VARIANT_DESC" ), aBoard.GetVariantDescription( variant ) );
    return definitions;
}

TEXT_SCAN ScanBoardTexts( const BOARD& aBoard )
{
    return ScanTexts( BoardTexts( aBoard ), TextDefinitions( aBoard ) );
}

// The drawing sheet's texts. The check resolves them to report variables they leave
// unresolved; nothing else of the sheet reaches a finding.
std::vector<wxString> DrawingSheetTexts( const DS_DATA_MODEL& aDrawing )
{
    std::vector<wxString> texts;
    for( unsigned index = 0; index < aDrawing.GetCount(); ++index )
    {
        const DS_DATA_ITEM* item = aDrawing.GetItem( index );
        if( item && item->GetType() == DS_DATA_ITEM::DS_TEXT )
            texts.push_back( static_cast<const DS_DATA_ITEM_TEXT*>( item )->m_TextBase );
    }
    return texts;
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
    // The date or revision the board's texts show, only when they can show one, so that
    // a board without them does not go stale at midnight: a date or revision variable, or
    // the day an expression reads through today(). Every read compares them again. The time
    // of day and expressions that read the clock, chance or the repository are no captured
    // value (PCB_DRC_RUN_INPUTS::Gaps).
    const TEXT_SCAN texts = ScanBoardTexts( aBoard );
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
    const TEXT_SCAN texts = ScanBoardTexts( aBoard );
    for( const char* name : TIME_OF_DAY_TEXT )
        if( texts.variables.contains( name ) ) result->m_timeOfDayText.emplace_back( name );
    for( const std::string& name : texts.calls )
        if( name != TODAY ) result->m_volatileExpressions.push_back( name + "()" );
    // The drawing sheet's expressions: the check reports the variables a sheet text leaves
    // unresolved, and an expression that reads the day, the time, chance or the repository
    // can change that while the check's copy of the sheet stays the same. Plain variables
    // always resolve there, whatever their value.
    const TEXT_SCAN sheet = ScanTexts( DrawingSheetTexts( aContext.drawing ), TextDefinitions( aBoard ) );
    if( sheet.expressions )
    {
        for( const std::string& name : sheet.calls ) result->m_drawingSheetExpressions.push_back( name + "()" );
        for( const std::string& name : sheet.expressionVariables )
            result->m_drawingSheetExpressions.push_back( "${" + name + "}" );
    }
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
