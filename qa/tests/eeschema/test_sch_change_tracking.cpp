/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

// Coverage oracle for native schematic change tracking.
//
// An automation client may only trust a schematic revision when every native path that
// changes persisted state advances it.  Each direct OnModify() call in the schematic editor
// and in the shared code it is built from (eeschema/, common/, include/) is such a mutation
// owner.  This oracle reads those sources and fails when:
//
//  - an owner is not reviewed below, or a reviewed owner is gone or calls OnModify more often;
//  - a routed owner marks the document modified without a commit push, a recorded change or a
//    tracked change before the call, in the call's own brace scope or an enclosing one (a
//    route in a sibling branch, only inside a nested block or lambda, or after the call does
//    not count; a braces-less if at the same brace level is not distinguished);
//  - a call to a helper that marks the document modified is not itself routed, or stages into
//    a commit that is not pushed after it;
//  - a SCH_TRACKED_CHANGE is declared without review, is staged or restricted to persisted
//    parts or screens without a reason (or without the code its reason depends on), or compares
//    a commit that is not declared before it (a whole-state tracker compares the same state as
//    the lifecycle digest; a staged tracker compares only the items its commit staged and what
//    its owner reports; a part-restricted tracker compares named persisted parts and the project
//    settings; a screen-restricted tracker compares named screens and the project settings);
//  - a schematic writer gains or loses a pinned persisted field, or a pinned writer group has
//    no owner (the commit machinery or a reviewed direct owner);
//  - a proven owner loses its rendered journey steps (a real OneChange and Unchanged call that
//    the journey method always runs: not a comment or text, and not inside a branch, loop,
//    catch, local function, lambda or conditional expression, or after an early return), or
//    complete tracking is claimed anywhere in the scanned sources while owners are pending or
//    routed but not yet proven end to end.
//
// Owners that change no persisted schematic state are an explicit, reviewed allow-list.
//
// The source checks read only the source tree and link nothing from eeschema, so the focused
// journal check compiles them too (qa/tests/common/test_document_change_journal.cpp).  The
// eeschema-linked checks at the end of this file (the tracked change itself and the cost of
// what it compares) build only where EESCHEMA is defined: qa_symbol_graphic_identity, which
// native-foundation builds and runs, and qa_eeschema.

#include <boost/test/unit_test.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#if defined( EESCHEMA )
#include <api/api_handler_sch.h>
#include <api/api_sch_state_groups.h>
#include <api/checked_schematic_controller.h>
#include <api/sch_api_save.h>
#include <google/protobuf/util/message_differencer.h>
#include <bus_alias.h>
#include <connection_graph.h>
#include <embedded_files.h>
#include <lib_symbol.h>
#include <nlohmann/json.hpp>
#include <project.h>
#include <project/project_file.h>
#include <qa_utils/wx_utils/unit_test_utils.h>
#include <refdes_tracker.h>
#include <sch_commit.h>
#include <sch_label.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_symbol.h>
#include <sch_text.h>
#include <schematic.h>
#include <schematic_settings.h>
#include <schematic_utils/schematic_file_util.h>
#include <settings/settings_manager.h>
#include <tool/tool_manager.h>
#include <kiid.h>

#include <api/schematic/schematic_types.pb.h>
#include <google/protobuf/any.pb.h>

#include <chrono>
#include <iomanip>
#include <iostream>
#include <memory>
#endif

namespace SCH_CHANGE_TRACKING_ORACLE
{
namespace fs = std::filesystem;

// ---------------------------------------------------------------------------------------------
// Source scanning
// ---------------------------------------------------------------------------------------------

inline bool isIdentChar( char c )
{
    return std::isalnum( static_cast<unsigned char>( c ) ) || c == '_';
}


inline bool isIdentStart( char c )
{
    return std::isalpha( static_cast<unsigned char>( c ) ) || c == '_';
}


inline bool isLineSpace( char c )
{
    return c == ' ' || c == '\t' || c == '\n';
}


inline bool startsWith( const std::string& aText, const std::string& aPrefix )
{
    return aText.compare( 0, aPrefix.size(), aPrefix ) == 0;
}


struct BLANKED_SOURCE
{
    std::string                            code;       ///< Comments and literal text blanked.
    std::vector<std::pair<size_t, size_t>> literals;   ///< [begin, end) of string literal text.
};


/// Blank comments, preprocessor directives and the contents of string and character literals,
/// keeping every offset and line break, so that code in comments, macros or text never counts
/// as a call, a route or a definition.
inline BLANKED_SOURCE blankCommentsAndLiterals( const std::string& aSource )
{
    BLANKED_SOURCE result{ aSource, {} };
    std::string&   out = result.code;
    const size_t   n = aSource.size();
    size_t         i = 0;
    bool           lineStart = true;

    while( i < n )
    {
        char c = aSource[i];

        if( c == '\n' )
        {
            lineStart = true;
            ++i;
            continue;
        }

        if( lineStart && ( c == ' ' || c == '\t' || c == '\r' ) )
        {
            ++i;
            continue;
        }

        if( lineStart && c == '#' )
        {
            // A directive runs to the end of the line, across backslash continuations.
            size_t j = i;

            while( j < n && !( aSource[j] == '\n' && ( j == 0 || aSource[j - 1] != '\\' ) ) )
            {
                if( aSource[j] != '\n' )
                    out[j] = ' ';

                ++j;
            }

            i = j;
            continue;
        }

        lineStart = false;

        if( c == '/' && i + 1 < n && aSource[i + 1] == '/' )
        {
            size_t j = aSource.find( '\n', i );
            j = j == std::string::npos ? n : j;

            for( size_t k = i; k < j; ++k )
                out[k] = ' ';

            i = j;
            continue;
        }

        if( c == '/' && i + 1 < n && aSource[i + 1] == '*' )
        {
            size_t j = aSource.find( "*/", i + 2 );
            j = j == std::string::npos ? n : j + 2;

            for( size_t k = i; k < j; ++k )
            {
                if( aSource[k] != '\n' )
                    out[k] = ' ';
            }

            i = j;
            continue;
        }

        if( c == 'R' && i + 1 < n && aSource[i + 1] == '"' && ( i == 0 || !isIdentChar( aSource[i - 1] ) ) )
        {
            size_t open = aSource.find( '(', i + 2 );

            if( open != std::string::npos && open - ( i + 2 ) <= 16 )
            {
                std::string terminator = ")" + aSource.substr( i + 2, open - ( i + 2 ) ) + "\"";
                size_t      j = aSource.find( terminator, open + 1 );
                j = j == std::string::npos ? n : j + terminator.size();

                if( j >= terminator.size() && j - terminator.size() >= open + 1 )
                    result.literals.emplace_back( open + 1, j - terminator.size() );

                for( size_t k = i + 2; k + 1 < j; ++k )
                {
                    if( aSource[k] != '\n' )
                        out[k] = ' ';
                }

                i = j;
                continue;
            }
        }

        if( c == '"' || ( c == '\'' && !( i > 0 && isIdentChar( aSource[i - 1] ) ) ) )
        {
            size_t j = i + 1;

            while( j < n && aSource[j] != c && aSource[j] != '\n' )
            {
                if( aSource[j] == '\\' )
                    ++j;

                ++j;
            }

            j = std::min( j, n );

            if( c == '"' )
                result.literals.emplace_back( i + 1, j );

            for( size_t k = i + 1; k < j; ++k )
                out[k] = ' ';

            i = std::min( n, j + 1 );
            continue;
        }

        ++i;
    }

    return result;
}


struct CALL_SITE
{
    std::string      identifier;
    std::string      function;   ///< Qualified name of the enclosing function definition.
    std::string      receiver;   ///< "m_frame->", "parent.", "EDA_BASE_FRAME::" or empty.
    std::string      arguments;  ///< The argument list without its parentheses, spaces removed.
    int              line = 0;
    size_t           position = 0;
    std::vector<int> scope;      ///< Brace scopes from the function body inwards.
};


enum class ROUTE_KIND
{
    PUSH,       ///< A SCH_COMMIT (or commit-named object) is pushed.
    RECORD,     ///< SCHEMATIC::RecordCommittedChange is called.
    TRACKED,    ///< A SCH_TRACKED_CHANGE is declared, or received from the caller.
};


struct ROUTE
{
    std::string      function;
    std::vector<int> scope;
    size_t           position = 0;
    ROUTE_KIND       kind = ROUTE_KIND::PUSH;
    std::string      object;     ///< The pushed commit or the declared tracker variable.
};


/// A SCH_TRACKED_CHANGE variable declaration and the arguments it is constructed with.
struct TRACKER
{
    std::string function;
    std::string variable;
    int         arguments = 0;   ///< Top-level constructor arguments.
    int         line = 0;
    bool        parts = false;   ///< The third argument names SCH_PERSISTED_PARTS: the parts form.
    std::string commit;          ///< Staged form: the third argument, the compared commit.
    bool        commitDeclared = false;  ///< That commit is a SCH_COMMIT declared before it.
};


struct FUNCTION_RANGE
{
    std::string name;
    size_t      begin = 0;   ///< Start of the definition header.
    size_t      end = 0;     ///< One past the closing brace.
};


struct LITERAL
{
    std::string function;
    std::string text;
    size_t      begin = 0;
};


/// True when @a aRoute is the scope of @a aCall or encloses it: a route taken in a sibling
/// branch, or only inside a nested block or lambda, does not always run before the call.
inline bool scopeEncloses( const std::vector<int>& aRoute, const std::vector<int>& aCall )
{
    return aRoute.size() <= aCall.size() && std::equal( aRoute.begin(), aRoute.end(), aCall.begin() );
}


inline std::string withoutSpaces( const std::string& aText )
{
    std::string result;

    for( char c : aText )
    {
        if( !std::isspace( static_cast<unsigned char>( c ) ) )
            result += c;
    }

    return result;
}


struct SOURCE_SCAN
{
    std::string                 raw;
    std::string                 code;
    std::vector<FUNCTION_RANGE> functions;
    std::vector<CALL_SITE>      calls;
    std::vector<ROUTE>          routes;
    std::vector<TRACKER>        trackers;
    std::vector<LITERAL>        literals;

    /// A call is routed when a commit push, a recorded change or a tracked change comes
    /// before it in its own scope or an enclosing one, so it always runs first.
    bool Routed( const CALL_SITE& aCall ) const
    {
        return std::any_of( routes.begin(), routes.end(),
                            [&]( const ROUTE& route )
                            {
                                return route.function == aCall.function && route.position < aCall.position
                                       && scopeEncloses( route.scope, aCall.scope );
                            } );
    }

    /// A call that stages into a caller's commit is routed when that commit, named first in
    /// its arguments, is pushed after the call in the same function.  The push may sit in a
    /// branch of its own: the other branch reverts the commit, and the staged edit with it.
    bool PushedAfter( const CALL_SITE& aCall ) const
    {
        return std::any_of( routes.begin(), routes.end(),
                            [&]( const ROUTE& route )
                            {
                                if( route.kind != ROUTE_KIND::PUSH || route.object.empty()
                                        || route.function != aCall.function || route.position < aCall.position )
                                {
                                    return false;
                                }

                                for( const std::string& form : { "&" + route.object, "*" + route.object,
                                                                  route.object } )
                                {
                                    const std::string& args = aCall.arguments;

                                    if( args.compare( 0, form.size() + 1, form + "," ) == 0 || args == form )
                                        return true;
                                }

                                return false;
                            } );
    }

    bool FunctionRoutes( const std::string& aFunction ) const
    {
        return std::any_of( routes.begin(), routes.end(),
                            [&]( const ROUTE& route ) { return route.function == aFunction; } );
    }

    bool HasFunction( const std::string& aFunction ) const
    {
        return std::any_of( functions.begin(), functions.end(),
                            [&]( const FUNCTION_RANGE& f ) { return f.name == aFunction; } );
    }

    bool FunctionContains( const std::string& aFunction, const std::string& aNeedle ) const
    {
        const std::string needle = withoutSpaces( aNeedle );

        for( const FUNCTION_RANGE& function : functions )
        {
            if( function.name != aFunction )
                continue;

            if( withoutSpaces( code.substr( function.begin, function.end - function.begin ) )
                        .find( needle ) != std::string::npos )
            {
                return true;
            }
        }

        return false;
    }
};


inline std::string collapseWhitespace( const std::string& aText )
{
    std::string result;
    bool        pendingSpace = false;

    for( char c : aText )
    {
        if( std::isspace( static_cast<unsigned char>( c ) ) )
        {
            pendingSpace = !result.empty();
            continue;
        }

        if( pendingSpace )
            result += ' ';

        pendingSpace = false;
        result += c;
    }

    return result;
}


/// The class name of a "class NAME : BASE" header, or nothing when the header is not a class.
inline bool classHeaderName( const std::string& aHeader, std::string& aName )
{
    if( aHeader.find( '(' ) != std::string::npos )
        return false;

    std::vector<std::string> candidates;

    if( startsWith( aHeader, "template" ) )
    {
        // Try every closing angle bracket, the last one first, as a template header end.
        for( size_t pos = aHeader.size(); pos-- > 0; )
        {
            if( aHeader[pos] == '>' )
                candidates.push_back( collapseWhitespace( aHeader.substr( pos + 1 ) ) );
        }
    }
    else
    {
        candidates.push_back( aHeader );
    }

    for( const std::string& rest : candidates )
    {
        std::string keyword;

        for( const char* word : { "class", "struct", "union" } )
        {
            if( startsWith( rest, word )
                    && ( rest.size() == std::string( word ).size() || !isIdentChar( rest[std::string( word ).size()] ) ) )
            {
                keyword = word;
            }
        }

        if( keyword.empty() )
            continue;

        std::string body = rest.substr( keyword.size() );
        size_t      colon = body.find( ':' );
        std::string names = body.substr( 0, colon );
        std::string last;
        bool        valid = !names.empty() && std::isspace( static_cast<unsigned char>( names[0] ) );

        std::istringstream words( names );
        std::string        word;

        while( words >> word )
        {
            if( !std::all_of( word.begin(), word.end(), isIdentChar ) )
                valid = false;

            last = word;
        }

        aName = valid ? last : std::string();
        return true;
    }

    return false;
}


/// Qualified name before the first parenthesis of a definition header.
inline std::string definitionName( const std::string& aHeader )
{
    std::string pre = aHeader.substr( 0, aHeader.find( '(' ) );

    while( !pre.empty() && std::isspace( static_cast<unsigned char>( pre.back() ) ) )
        pre.pop_back();

    auto readIdentifier = [&]( size_t aEnd, size_t& aBegin ) -> bool
    {
        size_t b = aEnd;

        while( b > 0 && isIdentChar( pre[b - 1] ) )
            --b;

        if( b == aEnd || !isIdentStart( pre[b] ) )
            return false;

        aBegin = b;
        return true;
    };

    size_t begin = 0;

    if( !readIdentifier( pre.size(), begin ) )
        return "<anonymous>";

    if( begin > 0 && pre[begin - 1] == '~' )
        --begin;

    size_t chainBegin = begin;

    while( true )
    {
        size_t b = chainBegin;

        while( b > 0 && std::isspace( static_cast<unsigned char>( pre[b - 1] ) ) )
            --b;

        if( b < 2 || pre[b - 1] != ':' || pre[b - 2] != ':' )
            break;

        b -= 2;

        while( b > 0 && std::isspace( static_cast<unsigned char>( pre[b - 1] ) ) )
            --b;

        size_t qualifierBegin = 0;

        if( !readIdentifier( b, qualifierBegin ) )
            break;

        chainBegin = qualifierBegin;
    }

    return withoutSpaces( pre.substr( chainBegin ) );
}


inline std::string receiverBefore( const std::string& aCode, size_t aPos )
{
    long b = static_cast<long>( aPos ) - 1;

    while( b >= 0 && isLineSpace( aCode[b] ) )
        --b;

    if( b >= 1 && ( aCode.compare( b - 1, 2, "->" ) == 0 || aCode.compare( b - 1, 2, "::" ) == 0 ) )
    {
        std::string op = aCode.substr( b - 1, 2 );
        b -= 2;

        while( b >= 0 && isLineSpace( aCode[b] ) )
            --b;

        long end = b + 1;

        if( b >= 0 && aCode[b] == ')' )
        {
            while( b >= 0 && aCode[b] != '(' )
                --b;

            --b;
        }

        while( b >= 0 && isIdentChar( aCode[b] ) )
            --b;

        std::string receiver = end > b + 1 ? aCode.substr( b + 1, end - ( b + 1 ) ) : std::string();
        receiver.erase( std::remove( receiver.begin(), receiver.end(), ' ' ), receiver.end() );
        return receiver + op;
    }

    if( b >= 0 && aCode[b] == '.' )
    {
        --b;

        while( b >= 0 && isLineSpace( aCode[b] ) )
            --b;

        long end = b + 1;

        while( b >= 0 && isIdentChar( aCode[b] ) )
            --b;

        return ( end > b + 1 ? aCode.substr( b + 1, end - ( b + 1 ) ) : std::string() ) + ".";
    }

    return {};
}


/// Name of the object a member function is called on ("commit" in "commit.Push("), if any.
inline std::string objectBefore( const std::string& aCode, size_t aPos )
{
    long b = static_cast<long>( aPos ) - 1;

    while( b >= 0 && isLineSpace( aCode[b] ) )
        --b;

    if( b >= 1 && aCode.compare( b - 1, 2, "->" ) == 0 )
        b -= 2;
    else if( b >= 0 && aCode[b] == '.' )
        b -= 1;
    else
        return {};

    while( b >= 0 && isLineSpace( aCode[b] ) )
        --b;

    long end = b + 1;

    while( b >= 0 && isIdentChar( aCode[b] ) )
        --b;

    return end > b + 1 ? aCode.substr( b + 1, end - ( b + 1 ) ) : std::string();
}


inline std::string lowered( std::string aText )
{
    std::transform( aText.begin(), aText.end(), aText.begin(),
                    []( unsigned char c ) { return static_cast<char>( std::tolower( c ) ); } );
    return aText;
}


/// The text between the parenthesis at @a aOpen and its match.
inline std::string argumentText( const std::string& aCode, size_t aOpen )
{
    int    depth = 0;
    size_t i = aOpen;

    for( ; i < aCode.size(); ++i )
    {
        if( aCode[i] == '(' )
            ++depth;
        else if( aCode[i] == ')' && --depth == 0 )
            break;
    }

    return aCode.substr( aOpen + 1, i > aOpen ? i - aOpen - 1 : 0 );
}


/// Number of top-level arguments in the parenthesised list at @a aOpen.
inline int topLevelArguments( const std::string& aCode, size_t aOpen )
{
    std::string text = argumentText( aCode, aOpen );
    int         depth = 0;
    int         count = 0;
    bool        any = false;

    for( char c : text )
    {
        if( c == '(' || c == '{' || c == '[' )
            ++depth;
        else if( c == ')' || c == '}' || c == ']' )
            --depth;
        else if( c == ',' && depth == 0 )
            ++count;

        any |= !std::isspace( static_cast<unsigned char>( c ) );
    }

    return any ? count + 1 : 0;
}


/// The top-level arguments of the parenthesised list at @a aOpen, spaces removed.
inline std::vector<std::string> topLevelArgumentTexts( const std::string& aCode, size_t aOpen )
{
    std::string              text = argumentText( aCode, aOpen );
    std::vector<std::string> arguments;
    std::string              current;
    int                      depth = 0;

    for( char c : text )
    {
        if( c == '(' || c == '{' || c == '[' )
            ++depth;
        else if( c == ')' || c == '}' || c == ']' )
            --depth;

        if( c == ',' && depth == 0 )
        {
            arguments.push_back( withoutSpaces( current ) );
            current.clear();
            continue;
        }

        current += c;
    }

    if( !withoutSpaces( current ).empty() || !arguments.empty() )
        arguments.push_back( withoutSpaces( current ) );

    return arguments;
}


/// A definition header that takes a SCH_TRACKED_CHANGE by reference or pointer.
inline bool receivesTracker( const std::string& aHeader )
{
    const std::string name = "SCH_TRACKED_CHANGE";

    for( size_t pos = aHeader.find( name ); pos != std::string::npos; pos = aHeader.find( name, pos + 1 ) )
    {
        size_t after = pos + name.size();

        if( pos > 0 && isIdentChar( aHeader[pos - 1] ) )
            continue;

        while( after < aHeader.size() && std::isspace( static_cast<unsigned char>( aHeader[after] ) ) )
            ++after;

        if( after < aHeader.size() && ( aHeader[after] == '&' || aHeader[after] == '*' )
                && aHeader.find( '(' ) < pos )
        {
            return true;
        }
    }

    return false;
}


/**
 * Scan one C++ translation unit.  Definitions are recognised the way KiCad writes them: a
 * brace at namespace or class level whose header has a parenthesis opens a function body;
 * everything inside it, including lambdas, belongs to that function.
 */
inline SOURCE_SCAN scanSource( const std::string& aSource, const std::set<std::string>& aWatched )
{
    struct FRAME
    {
        char        kind;   ///< 'N'amespace, 'C'lass, 'F'unction, 'B'lock, 'I'nner block.
        std::string name;
        int         id;
    };

    SOURCE_SCAN scan;
    scan.raw = aSource;

    BLANKED_SOURCE blanked = blankCommentsAndLiterals( aSource );
    scan.code = blanked.code;

    const std::string& code = scan.code;
    const size_t       n = code.size();
    std::vector<FRAME> frames;
    std::vector<long>  openFunctions;   ///< Indices into scan.functions still open.
    std::map<std::string, std::set<std::string>> commitVariables;
    size_t             headerStart = 0;
    int                nextId = 0;

    auto insideFunction = [&]()
    {
        return std::any_of( frames.begin(), frames.end(), []( const FRAME& f ) { return f.kind == 'F'; } );
    };

    auto currentFunction = [&]() -> std::string
    {
        for( const FRAME& frame : frames )
        {
            if( frame.kind == 'F' )
                return frame.name;
        }

        return {};
    };

    auto currentScope = [&]()
    {
        std::vector<int> scope;
        bool             started = false;

        for( const FRAME& frame : frames )
        {
            started |= frame.kind == 'F';

            if( started )
                scope.push_back( frame.id );
        }

        return scope;
    };

    size_t i = 0;

    while( i < n )
    {
        char c = code[i];

        if( c == '{' )
        {
            ++nextId;

            if( !insideFunction() )
            {
                std::string rawHeader = code.substr( headerStart, i - headerStart );
                std::string header = collapseWhitespace( rawHeader );
                std::string className;

                if( header.empty() || startsWith( header, "namespace" ) || startsWith( header, "extern" ) )
                {
                    frames.push_back( { 'N', "", nextId } );
                }
                else if( classHeaderName( header, className ) )
                {
                    frames.push_back( { 'C', className, nextId } );
                }
                else if( startsWith( header, "enum" )
                         && ( header.size() == 4 || !isIdentChar( header[4] ) ) )
                {
                    frames.push_back( { 'B', "", nextId } );
                }
                else if( header.find( '(' ) != std::string::npos
                         && header.substr( 0, header.find( '(' ) ).find( '=' ) == std::string::npos )
                {
                    std::string name = definitionName( header );
                    std::string owningClass;
                    bool        inClass = false;

                    for( const FRAME& frame : frames )
                    {
                        if( frame.kind == 'C' )
                        {
                            inClass = true;
                            owningClass = frame.name;
                        }
                    }

                    if( inClass && name.find( "::" ) == std::string::npos )
                        name = owningClass + "::" + name;

                    frames.push_back( { 'F', name, nextId } );
                    scan.functions.push_back( { name, headerStart, std::string::npos } );
                    openFunctions.push_back( static_cast<long>( scan.functions.size() ) - 1 );

                    // A tracker received from the caller routes the whole body.
                    if( receivesTracker( header ) )
                        scan.routes.push_back( { name, { nextId }, headerStart, ROUTE_KIND::TRACKED, "" } );
                }
                else
                {
                    frames.push_back( { 'B', "", nextId } );
                }

                // The next definition header starts after a namespace, class or block brace.
                if( frames.back().kind != 'F' )
                    headerStart = i + 1;
            }
            else
            {
                frames.push_back( { 'I', "", nextId } );
            }

            ++i;
            continue;
        }

        if( c == '}' )
        {
            if( !frames.empty() )
            {
                FRAME frame = frames.back();
                frames.pop_back();

                if( frame.kind == 'F' && !openFunctions.empty() )
                {
                    scan.functions[openFunctions.back()].end = i + 1;
                    openFunctions.pop_back();
                }
            }

            if( !insideFunction() )
                headerStart = i + 1;

            ++i;
            continue;
        }

        if( c == ';' )
        {
            if( !insideFunction() )
                headerStart = i + 1;

            ++i;
            continue;
        }

        if( isIdentStart( c ) && ( i == 0 || !isIdentChar( code[i - 1] ) ) )
        {
            size_t j = i;

            while( j < n && isIdentChar( code[j] ) )
                ++j;

            std::string identifier = code.substr( i, j - i );
            size_t      k = j;

            while( k < n && isLineSpace( code[k] ) )
                ++k;

            bool call = k < n && code[k] == '(';

            if( insideFunction() )
            {
                std::string function = currentFunction();

                if( identifier == "RecordCommittedChange" && call )
                    scan.routes.push_back( { function, currentScope(), i, ROUTE_KIND::RECORD, "" } );

                if( identifier == "SCH_TRACKED_CHANGE" )
                {
                    // Only a declared tracker routes; SCH_TRACKED_CHANGE::Mark() does not.
                    size_t v = k;
                    size_t w = v;

                    while( w < n && isIdentChar( code[w] ) )
                        ++w;

                    size_t open = w;

                    while( open < n && isLineSpace( code[open] ) )
                        ++open;

                    if( w > v && isIdentStart( code[v] ) && open < n && code[open] == '(' )
                    {
                        TRACKER tracker;
                        tracker.function = function;
                        tracker.variable = code.substr( v, w - v );
                        tracker.arguments = topLevelArguments( code, open );
                        tracker.line = static_cast<int>( std::count( code.begin(), code.begin() + i, '\n' ) ) + 1;

                        // A three-argument tracker either compares named persisted parts (its
                        // third argument is a SCH_PERSISTED_PARTS) or is staged: it compares a
                        // commit that must outlive it, one declared earlier in the same function
                        // (commit names are collected in order).
                        if( tracker.arguments == 3 )
                        {
                            const std::string third = topLevelArgumentTexts( code, open )[2];

                            if( startsWith( withoutSpaces( third ), "SCH_PERSISTED_PARTS" ) )
                            {
                                tracker.parts = true;
                            }
                            else
                            {
                                tracker.commit = third;
                                tracker.commitDeclared = commitVariables[function].count( tracker.commit ) > 0;
                            }
                        }

                        scan.trackers.push_back( tracker );
                        scan.routes.push_back( { function, currentScope(), i, ROUTE_KIND::TRACKED, tracker.variable } );
                    }
                }

                if( identifier == "SCH_COMMIT" )
                {
                    size_t v = j;

                    while( v < n && ( isLineSpace( code[v] ) || code[v] == '&' || code[v] == '*' ) )
                        ++v;

                    size_t w = v;

                    while( w < n && isIdentChar( code[w] ) )
                        ++w;

                    if( w > v )
                        commitVariables[function].insert( code.substr( v, w - v ) );
                }

                if( identifier == "Push" && call )
                {
                    std::string object = objectBefore( code, i );

                    if( lowered( object ).find( "commit" ) != std::string::npos
                            || commitVariables[function].count( object ) )
                    {
                        scan.routes.push_back( { function, currentScope(), i, ROUTE_KIND::PUSH, object } );
                    }
                }

                if( call && aWatched.count( identifier ) )
                {
                    CALL_SITE site;
                    site.identifier = identifier;
                    site.function = function;
                    site.receiver = receiverBefore( code, i );
                    site.arguments = withoutSpaces( argumentText( code, k ) );
                    site.line = static_cast<int>( std::count( code.begin(), code.begin() + i, '\n' ) ) + 1;
                    site.position = i;
                    site.scope = currentScope();
                    scan.calls.push_back( std::move( site ) );
                }
            }

            i = j;
            continue;
        }

        ++i;
    }

    for( const auto& [begin, end] : blanked.literals )
    {
        LITERAL literal;
        literal.text = aSource.substr( begin, end - begin );
        literal.begin = begin;

        for( const FUNCTION_RANGE& function : scan.functions )
        {
            if( function.begin <= begin && ( function.end == std::string::npos || begin < function.end ) )
                literal.function = function.name;
        }

        scan.literals.push_back( std::move( literal ) );
    }

    return scan;
}


/// Persisted-field tokens written by each writer function: "(token" in a literal, or the
/// name literal passed to a KICAD_FORMAT formatter writing to m_out.
inline std::map<std::string, std::set<std::string>> writerFields( const SOURCE_SCAN& aScan )
{
    std::map<std::string, std::set<std::string>> fields;

    auto isLowerToken = []( char c )
    {
        return std::islower( static_cast<unsigned char>( c ) ) || c == '_'
               || std::isdigit( static_cast<unsigned char>( c ) );
    };

    for( const LITERAL& literal : aScan.literals )
    {
        if( literal.function.empty() )
            continue;

        const std::string& s = literal.text;

        for( size_t k = 0; k < s.size(); )
        {
            if( s[k] == '(' )
            {
                size_t j = k + 1;

                while( j < s.size() && isLowerToken( s[j] ) )
                    ++j;

                if( j > k + 1 && std::islower( static_cast<unsigned char>( s[k + 1] ) ) )
                    fields[literal.function].insert( s.substr( k + 1, j - k - 1 ) );

                k = j;
                continue;
            }

            ++k;
        }

        if( s.empty() || !std::islower( static_cast<unsigned char>( s[0] ) )
                || !std::all_of( s.begin(), s.end(), isLowerToken ) )
        {
            continue;
        }

        // Walk back from the opening quote over "Format...( formatter, ", where the formatter
        // is the writer's output, passed as m_out, aFormatter, &aOut and the like.
        const std::string& code = aScan.code;
        long               b = static_cast<long>( literal.begin ) - 2;

        auto skipSpace = [&]()
        {
            while( b >= 0 && std::isspace( static_cast<unsigned char>( code[b] ) ) )
                --b;
        };

        skipSpace();

        if( b < 0 || code[b] != ',' )
            continue;

        --b;
        skipSpace();

        long formatterEnd = b + 1;

        while( b >= 0 && isIdentChar( code[b] ) )
            --b;

        if( formatterEnd == b + 1 || !isIdentStart( code[b + 1] ) )
            continue;

        skipSpace();

        if( b >= 0 && ( code[b] == '&' || code[b] == '*' ) )
        {
            --b;
            skipSpace();
        }

        if( b < 0 || code[b] != '(' )
            continue;

        --b;
        skipSpace();

        long end = b + 1;

        while( b >= 0 && isIdentChar( code[b] ) )
            --b;

        if( startsWith( code.substr( b + 1, end - ( b + 1 ) ), "Format" ) )
            fields[literal.function].insert( s );
    }

    return fields;
}


inline bool readFile( const fs::path& aPath, std::string& aText )
{
    std::ifstream stream( aPath, std::ios::binary );

    if( !stream )
        return false;

    std::ostringstream buffer;
    buffer << stream.rdbuf();
    aText = buffer.str();
    return true;
}


inline bool isSourceRoot( const fs::path& aPath )
{
    std::error_code error;
    return !aPath.empty() && fs::exists( aPath / "eeschema" / "sch_commit.cpp", error )
           && fs::exists( aPath / "eeschema" / "sch_io" / "kicad_sexpr" / "sch_io_kicad_sexpr.cpp", error );
}


/// The checkout being tested: KICAD_SOURCE_DIR, the source directory recorded in the build
/// tree's CMakeCache.txt, or an enclosing checkout of the working directory.
inline fs::path findSourceRoot()
{
    if( const char* configured = std::getenv( "KICAD_SOURCE_DIR" ) )
    {
        if( isSourceRoot( configured ) )
            return configured;
    }

    std::error_code error;
    fs::path        directory = fs::current_path( error );

    while( !error && !directory.empty() )
    {
        std::string cache;

        if( readFile( directory / "CMakeCache.txt", cache ) )
        {
            std::istringstream lines( cache );
            std::string        line;
            const std::string  key = "CMAKE_HOME_DIRECTORY:INTERNAL=";

            while( std::getline( lines, line ) )
            {
                if( !line.empty() && line.back() == '\r' )
                    line.pop_back();

                if( startsWith( line, key ) && isSourceRoot( line.substr( key.size() ) ) )
                    return line.substr( key.size() );
            }
        }

        if( isSourceRoot( directory ) )
            return directory;

        if( directory == directory.parent_path() )
            break;

        directory = directory.parent_path();
    }

    return {};
}


inline std::vector<fs::path> sourceFiles( const fs::path& aRoot, const std::string& aRelative )
{
    std::vector<fs::path> files;
    fs::path              start = aRoot / aRelative;
    std::error_code       error;

    if( fs::is_regular_file( start, error ) )
        return { start };

    for( fs::recursive_directory_iterator it( start, error ), end; !error && it != end; it.increment( error ) )
    {
        // Headers too: an inline function can own a change as well as a source file can.
        if( it->is_regular_file( error )
                && ( it->path().extension() == ".cpp" || it->path().extension() == ".h" ) )
        {
            files.push_back( it->path() );
        }
    }

    std::sort( files.begin(), files.end() );
    return files;
}


inline std::string relativeName( const fs::path& aRoot, const fs::path& aFile )
{
    return fs::relative( aFile, aRoot ).generic_string();
}


/// Calls of @a aName in blanked code, wherever they are: an identifier followed by '('.
inline size_t countCalls( const std::string& aCode, const std::string& aName )
{
    size_t count = 0;

    for( size_t pos = aCode.find( aName ); pos != std::string::npos; pos = aCode.find( aName, pos + 1 ) )
    {
        size_t after = pos + aName.size();

        if( ( pos > 0 && isIdentChar( aCode[pos - 1] ) ) || ( after < aCode.size() && isIdentChar( aCode[after] ) ) )
            continue;

        while( after < aCode.size() && isLineSpace( aCode[after] ) )
            ++after;

        count += after < aCode.size() && aCode[after] == '(' ? 1 : 0;
    }

    return count;
}


// ---------------------------------------------------------------------------------------------
// Journey scanning (C#)
// ---------------------------------------------------------------------------------------------

/// Blank C# comments, preprocessor lines and the contents of string and character literals
/// (regular, verbatim, interpolated and raw), keeping every offset and line break, so that a
/// journey step named only in a comment or a message never counts as a step.
inline BLANKED_SOURCE blankCSharp( const std::string& aSource )
{
    BLANKED_SOURCE result{ aSource, {} };
    std::string&   out = result.code;
    const size_t   n = aSource.size();
    size_t         i = 0;
    bool           lineStart = true;

    auto blank = [&]( size_t aBegin, size_t aEnd )
    {
        for( size_t k = aBegin; k < aEnd && k < n; ++k )
        {
            if( aSource[k] != '\n' )
                out[k] = ' ';
        }
    };

    auto literal = [&]( size_t aBegin, size_t aEnd )
    {
        aEnd = std::min( aEnd, n );
        result.literals.emplace_back( aBegin, std::max( aBegin, aEnd ) );
        blank( aBegin, aEnd );
    };

    while( i < n )
    {
        const char c = aSource[i];

        if( c == '\n' )
        {
            lineStart = true;
            ++i;
            continue;
        }

        if( lineStart && ( c == ' ' || c == '\t' || c == '\r' ) )
        {
            ++i;
            continue;
        }

        if( lineStart && c == '#' )
        {
            size_t j = aSource.find( '\n', i );
            j = j == std::string::npos ? n : j;
            blank( i, j );
            i = j;
            continue;
        }

        lineStart = false;

        if( c == '/' && i + 1 < n && aSource[i + 1] == '/' )
        {
            size_t j = aSource.find( '\n', i );
            j = j == std::string::npos ? n : j;
            blank( i, j );
            i = j;
            continue;
        }

        if( c == '/' && i + 1 < n && aSource[i + 1] == '*' )
        {
            size_t j = aSource.find( "*/", i + 2 );
            j = j == std::string::npos ? n : j + 2;
            blank( i, j );
            i = j;
            continue;
        }

        if( c == '"' || ( ( c == '$' || c == '@' ) && !( i > 0 && isIdentChar( aSource[i - 1] ) ) ) )
        {
            // Any run of '$' and '@' prefixes a literal only when a quote follows it.
            size_t q = i;

            while( q < n && ( aSource[q] == '$' || aSource[q] == '@' ) )
                ++q;

            if( q >= n || aSource[q] != '"' )
            {
                i = q > i ? q : i + 1;
                continue;
            }

            const bool verbatim = aSource.substr( i, q - i ).find( '@' ) != std::string::npos;
            size_t     run = 0;

            while( q + run < n && aSource[q + run] == '"' )
                ++run;

            if( run >= 3 )
            {
                // Raw literal: ends at the first run of as many quotes.
                const std::string quotes( run, '"' );
                size_t            j = aSource.find( quotes, q + run );
                j = j == std::string::npos ? n : j;
                literal( q + run, j );
                i = std::min( n, j + run );
                continue;
            }

            size_t j = q + 1;

            if( verbatim )
            {
                // A doubled quote is a quote inside a verbatim literal.
                while( j < n && !( aSource[j] == '"' && !( j + 1 < n && aSource[j + 1] == '"' ) ) )
                    j += aSource[j] == '"' ? 2 : 1;
            }
            else
            {
                while( j < n && aSource[j] != '"' && aSource[j] != '\n' )
                    j += aSource[j] == '\\' ? 2 : 1;
            }

            literal( q + 1, j );
            i = std::min( n, j + 1 );
            continue;
        }

        if( c == '\'' && !( i > 0 && isIdentChar( aSource[i - 1] ) ) )
        {
            size_t j = i + 1;

            while( j < n && aSource[j] != '\'' && aSource[j] != '\n' )
                j += aSource[j] == '\\' ? 2 : 1;

            blank( i + 1, j );
            i = std::min( n, j + 1 );
            continue;
        }

        ++i;
    }

    return result;
}


/// The body [begin, end) of the method or local function @a aName declared in blanked C#
/// code: "Task Name(...) {" or "Task<T> Name(...) {".  Calls do not count.
inline std::pair<size_t, size_t> csharpMethodBody( const std::string& aCode, const std::string& aName,
                                                   size_t aBegin = 0, size_t aEnd = std::string::npos )
{
    const size_t end = std::min( aEnd, aCode.size() );

    for( size_t pos = aCode.find( aName, aBegin ); pos != std::string::npos && pos < end;
         pos = aCode.find( aName, pos + 1 ) )
    {
        size_t after = pos + aName.size();

        if( ( pos > 0 && isIdentChar( aCode[pos - 1] ) ) || after >= end || aCode[after] != '(' )
            continue;

        size_t typeEnd = pos;

        while( typeEnd > aBegin && std::isspace( static_cast<unsigned char>( aCode[typeEnd - 1] ) ) )
            --typeEnd;

        size_t typeBegin = typeEnd;

        while( typeBegin > aBegin
               && ( isIdentChar( aCode[typeBegin - 1] ) || aCode[typeBegin - 1] == '<' || aCode[typeBegin - 1] == '>' ) )
        {
            --typeBegin;
        }

        if( !startsWith( aCode.substr( typeBegin, typeEnd - typeBegin ), "Task" ) )
            continue;

        int    depth = 0;
        size_t k = after;

        for( ; k < end; ++k )
        {
            if( aCode[k] == '(' )
                ++depth;
            else if( aCode[k] == ')' && --depth == 0 )
                break;
        }

        size_t brace = aCode.find_first_not_of( " \t\r\n", k + 1 );

        if( brace == std::string::npos || brace >= end || aCode[brace] != '{' )
            continue;

        depth = 0;

        for( size_t m = brace; m < end; ++m )
        {
            if( aCode[m] == '{' )
                ++depth;
            else if( aCode[m] == '}' && --depth == 0 )
                return { brace + 1, m };
        }
    }

    return { std::string::npos, std::string::npos };
}


/// An awaited call with a plain string literal argument: the literal and the offset of its
/// "await" keyword.
struct AWAITED_CALL
{
    std::string literal;
    size_t      await = 0;
};


/// The string literal passed as argument @a aIndex of every awaited call of @a aName in
/// [aBegin, aEnd) of blanked C# @a aCode: "await Name(a, "text", ...)".  The literal is read
/// from @a aRaw at the same offsets; an argument that is not a plain literal yields nothing.
inline std::vector<AWAITED_CALL> awaitedLiteralCalls( const std::string& aRaw, const std::string& aCode,
                                                      const std::string& aName, size_t aIndex,
                                                      size_t aBegin, size_t aEnd )
{
    std::vector<AWAITED_CALL> found;
    const size_t              end = std::min( aEnd, aCode.size() );

    for( size_t pos = aCode.find( aName, aBegin ); pos != std::string::npos && pos < end;
         pos = aCode.find( aName, pos + 1 ) )
    {
        const size_t open = pos + aName.size();

        if( ( pos > 0 && isIdentChar( aCode[pos - 1] ) ) || open >= end || aCode[open] != '(' )
            continue;

        size_t before = pos;

        while( before > 0 && std::isspace( static_cast<unsigned char>( aCode[before - 1] ) ) )
            --before;

        if( before < 5 || aCode.compare( before - 5, 5, "await" ) != 0
                || ( before > 5 && isIdentChar( aCode[before - 6] ) ) )
        {
            continue;
        }

        int    depth = 0;
        size_t index = 0;
        size_t argumentBegin = open + 1;

        for( size_t k = open; k < end; ++k )
        {
            const char c = aCode[k];
            const bool closes = c == ')' || c == '}' || c == ']';

            if( c == '(' || c == '{' || c == '[' )
                ++depth;
            else if( closes )
                --depth;

            if( ( c == ',' && depth == 1 ) || ( closes && depth == 0 ) )
            {
                if( index == aIndex )
                {
                    const std::string text = aRaw.substr( argumentBegin, k - argumentBegin );
                    const size_t      first = text.find_first_not_of( " \t\r\n" );
                    const size_t      last = text.find_last_not_of( " \t\r\n" );

                    if( first != std::string::npos && last > first && text[first] == '"' && text[last] == '"'
                            && text.find( '"', first + 1 ) == last )
                    {
                        found.push_back( { text.substr( first + 1, last - first - 1 ), before - 5 } );
                    }

                    break;
                }

                ++index;
                argumentBegin = k + 1;
            }

            if( closes && depth == 0 )
                break;
        }
    }

    return found;
}


/// How the statements of a brace block inside a C# method body run.
enum class CS_BLOCK
{
    ALWAYS,        ///< Every time the enclosing statements do: a bare block, a try without a catch,
                   ///< finally, using or lock.
    CONDITIONAL,   ///< Perhaps not, or more than once: if, else, a loop, switch, catch, or a try
                   ///< whose catch could swallow a failed assertion.
    NOT_A_STATEMENT_OF_THE_METHOD   ///< A local function, lambda, anonymous method or initializer.
};


/// The opening brace of every block enclosing @a aPos, from [aBegin, aPos) of blanked C# code.
inline std::vector<size_t> csharpEnclosingBraces( const std::string& aCode, size_t aBegin, size_t aPos )
{
    std::vector<size_t> open;

    for( size_t k = aBegin; k < aPos && k < aCode.size(); ++k )
    {
        if( aCode[k] == '{' )
            open.push_back( k );
        else if( aCode[k] == '}' && !open.empty() )
            open.pop_back();
    }

    return open;
}


/// The start of the statement or header that ends at @a aPos: just after the previous ';', '{',
/// '}' or ',' outside parentheses, or after an unmatched '(' or '[' (the position is then inside
/// an argument list).  @a aInsideArguments reports that last case.
inline size_t csharpStatementStart( const std::string& aCode, size_t aBegin, size_t aPos,
                                    bool* aInsideArguments = nullptr )
{
    size_t k = aPos;
    int    depth = 0;

    if( aInsideArguments )
        *aInsideArguments = false;

    while( k > aBegin )
    {
        const char c = aCode[k - 1];

        if( c == ')' || c == ']' )
        {
            ++depth;
        }
        else if( c == '(' || c == '[' )
        {
            if( depth == 0 )
            {
                if( aInsideArguments )
                    *aInsideArguments = true;

                break;
            }

            --depth;
        }
        else if( depth == 0 && ( c == ';' || c == '{' || c == '}' || c == ',' ) )
        {
            break;
        }

        --k;
    }

    return k;
}


inline std::string trimmed( const std::string& aText )
{
    const size_t first = aText.find_first_not_of( " \t\r\n" );

    if( first == std::string::npos )
        return {};

    return aText.substr( first, aText.find_last_not_of( " \t\r\n" ) - first + 1 );
}


inline std::string leadingWord( const std::string& aText )
{
    size_t k = 0;

    while( k < aText.size() && isIdentChar( aText[k] ) )
        ++k;

    return aText.substr( 0, k );
}


/// C# keywords that begin a statement that may skip, repeat or leave what follows.
inline const std::set<std::string>& csharpControlKeywords()
{
    static const std::set<std::string> keywords{ "if", "else", "for", "foreach", "while", "do", "switch", "catch",
                                                 "case", "default", "when", "return", "goto", "break",
                                                 "continue", "throw", "yield" };
    return keywords;
}


/// How the block opened by the brace at @a aBrace runs, judged by its header.
inline CS_BLOCK csharpBlockKind( const std::string& aCode, size_t aBegin, size_t aEnd, size_t aBrace )
{
    const size_t      start = csharpStatementStart( aCode, aBegin, aBrace );
    const std::string header = trimmed( aCode.substr( start, aBrace - start ) );
    const std::string word = leadingWord( header );

    if( header.empty() || word == "finally" || word == "using" || word == "lock" )
        return CS_BLOCK::ALWAYS;

    if( word == "try" )
    {
        // A catch after the try block could swallow a failed assertion inside it.
        int    depth = 0;
        size_t close = aBrace;

        for( ; close < aEnd; ++close )
        {
            if( aCode[close] == '{' )
                ++depth;
            else if( aCode[close] == '}' && --depth == 0 )
                break;
        }

        const size_t next = aCode.find_first_not_of( " \t\r\n", close + 1 );
        const bool   caught = next != std::string::npos && next < aEnd && aCode.compare( next, 5, "catch" ) == 0
                            && !( next + 5 < aCode.size() && isIdentChar( aCode[next + 5] ) );

        return caught ? CS_BLOCK::CONDITIONAL : CS_BLOCK::ALWAYS;
    }

    if( csharpControlKeywords().count( word ) )
        return CS_BLOCK::CONDITIONAL;

    return CS_BLOCK::NOT_A_STATEMENT_OF_THE_METHOD;
}


/// True when the awaited call at @a aAwait is a statement the method body [aBegin, aEnd) always
/// runs: every enclosing block runs unconditionally, the call is a whole statement (optionally
/// assigned to a variable), and no return or goto of the method itself comes before it.  A call
/// inside a branch, loop, catch, local function or lambda, part of a conditional expression, or
/// after an early return proves nothing about the journey.
inline bool csharpUnconditionalStatement( const std::string& aCode, size_t aBegin, size_t aEnd, size_t aAwait )
{
    for( size_t brace : csharpEnclosingBraces( aCode, aBegin, aAwait ) )
    {
        if( csharpBlockKind( aCode, aBegin, aEnd, brace ) != CS_BLOCK::ALWAYS )
            return false;
    }

    // Only "await Call(...)", "name = await Call(...)" or "Type name = await Call(...)".
    bool              insideArguments = false;
    const size_t      start = csharpStatementStart( aCode, aBegin, aAwait, &insideArguments );
    const std::string prefix = trimmed( aCode.substr( start, aAwait - start ) );

    if( insideArguments )
        return false;

    if( !prefix.empty() )
    {
        if( prefix.back() != '=' || prefix.size() < 2
                || std::string( "=!<>" ).find( prefix[prefix.size() - 2] ) != std::string::npos )
        {
            return false;
        }

        std::istringstream       words( prefix.substr( 0, prefix.size() - 1 ) );
        std::vector<std::string> tokens;

        for( std::string token; words >> token; )
            tokens.push_back( token );

        if( tokens.empty() || tokens.size() > 2 || leadingWord( tokens.back() ) != tokens.back()
                || csharpControlKeywords().count( leadingWord( tokens.front() ) ) )
        {
            return false;
        }

        for( char c : tokens.front() )
        {
            if( !isIdentChar( c ) && std::string( "<>.?[]" ).find( c ) == std::string::npos )
                return false;
        }
    }

    // A return or goto of the method itself before the call can skip it.
    for( const char* exit : { "return", "goto" } )
    {
        const std::string keyword( exit );

        for( size_t pos = aCode.find( keyword, aBegin ); pos != std::string::npos && pos < aAwait;
             pos = aCode.find( keyword, pos + 1 ) )
        {
            if( ( pos > 0 && isIdentChar( aCode[pos - 1] ) )
                    || ( pos + keyword.size() < aCode.size() && isIdentChar( aCode[pos + keyword.size()] ) ) )
            {
                continue;
            }

            bool ownExit = true;

            for( size_t brace : csharpEnclosingBraces( aCode, aBegin, pos ) )
            {
                if( csharpBlockKind( aCode, aBegin, aEnd, brace ) == CS_BLOCK::NOT_A_STATEMENT_OF_THE_METHOD )
                    ownExit = false;
            }

            if( ownExit )
                return false;
        }
    }

    return true;
}


// ---------------------------------------------------------------------------------------------
// Reviewed owners
// ---------------------------------------------------------------------------------------------

enum class DISPOSITION
{
    ROUTED,                  ///< A route runs before every call, in its scope or an enclosing one.
    ROUTED_UNLESS_RECORDED,  ///< Records its change unless a commit inside it already did (evidence).
    ROUTED_BY_CALLERS,       ///< A helper: every call to it is routed (see HELPERS), or evidence shows
                             ///< its only schematic caller records the change.
    DIALOG_DRAFT,            ///< DIALOG_SHIM::OnModify: marks the dialog title only.
    OTHER_DOCUMENT,          ///< Symbol library, library table, board or simulator workbook.
    MODIFIED_FLAG_ONLY,      ///< Marks the editor modified for state that is already observed.
    LOAD_BASELINE,           ///< Normalisation inside the load that starts a new journal epoch.
    COMMIT_MACHINERY,        ///< SCH_COMMIT itself or an OnModify override chaining to its base.
    PENDING                  ///< Unrouted persisted change: keeps tracking reported incomplete.
};


inline const char* dispositionName( DISPOSITION aDisposition )
{
    switch( aDisposition )
    {
    case DISPOSITION::ROUTED:                 return "routed";
    case DISPOSITION::ROUTED_UNLESS_RECORDED: return "routed unless recorded";
    case DISPOSITION::ROUTED_BY_CALLERS:      return "routed by callers";
    case DISPOSITION::DIALOG_DRAFT:           return "dialog draft";
    case DISPOSITION::OTHER_DOCUMENT:         return "other document";
    case DISPOSITION::MODIFIED_FLAG_ONLY:     return "modified flag only";
    case DISPOSITION::LOAD_BASELINE:          return "load baseline";
    case DISPOSITION::COMMIT_MACHINERY:       return "commit machinery";
    case DISPOSITION::PENDING:                return "pending";
    }

    return "unknown";
}


/// A reviewed fact about another function: its body must contain @a needle ("@routes" means
/// any SCH_COMMIT push, RecordCommittedChange or SCH_TRACKED_CHANGE).
struct EVIDENCE
{
    std::string file;
    std::string function;
    std::string needle;
};


struct OWNER
{
    std::string              file;
    std::string              function;
    std::string              receiver;
    int                      calls;
    DISPOSITION              disposition;
    std::vector<std::string> groups;       ///< Writer groups or "project-settings" affected.
    std::vector<EVIDENCE>    evidence;
    std::string              reason;
    int                      estimateLines = 0;
};


const std::string G_PROJECT = "project-settings";
const std::string G_FORMAT = "SCH_IO_KICAD_SEXPR::Format";
const std::string G_SYMBOL = "SCH_IO_KICAD_SEXPR::saveSymbol";
const std::string G_FIELD = "SCH_IO_KICAD_SEXPR::saveField";
const std::string G_SHEET = "SCH_IO_KICAD_SEXPR::saveSheet";
const std::string G_INSTANCES = "SCH_IO_KICAD_SEXPR::saveInstances";
const std::string G_TEXT = "SCH_IO_KICAD_SEXPR::saveText";
const std::string G_LINE = "SCH_IO_KICAD_SEXPR::saveLine";
const std::string G_JUNCTION = "SCH_IO_KICAD_SEXPR::saveJunction";
const std::string G_GROUP = "SCH_IO_KICAD_SEXPR::saveGroup";
const std::string G_PIN_MAP = "formatPinMapOverride";
const std::string G_LIB_CACHE = "SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol";
const std::string G_TITLE = "TITLE_BLOCK::Format";
const std::string G_PAGE = "PAGE_INFO::Format";
const std::string G_EMBEDDED = "EMBEDDED_FILES::WriteEmbeddedFiles";
const std::string ANY_ROUTE = "@routes";

const std::string SYMBOL_FIELDS_DIALOG = "eeschema/dialogs/dialog_symbol_fields_table.cpp";
const std::string SYMBOL_DIALOG = "eeschema/dialogs/dialog_symbol_properties.cpp";
const std::string EDIT_TOOL = "eeschema/tools/sch_edit_tool.cpp";
const std::string DRAWING_TOOLS = "eeschema/tools/sch_drawing_tools.cpp";
const std::string EDITOR_CONTROL = "eeschema/tools/sch_editor_control.cpp";
const std::string SETUP_CONFIG = "eeschema/eeschema_config.cpp";
const std::string SIM_FRAME = "eeschema/sim/simulator_frame.cpp";
const std::string ANNOTATE_DIALOG = "eeschema/dialogs/dialog_annotate.cpp";
const std::string ANNOTATE_SOURCE = "eeschema/annotate.cpp";
const std::string JOURNEY = "automation/tests/KiCad.Automation.Tests/NativeEventJourney.cs";
const std::string JOURNEY_METHOD = "VerifyDirectOwnerTracking";


inline std::vector<OWNER> reviewedOwners()
{
    const std::string LIB_DIALOG = "eeschema/dialogs/dialog_lib_symbol_properties.cpp";
    const std::string SIM_UI = "eeschema/sim/simulator_frame_ui.cpp";
    const std::string SYM_EDIT = "eeschema/tools/symbol_editor_edit_tool.cpp";
    const std::string FIELDS_TABLE = "common/dialogs/dialog_fields_table.cpp";
    const std::string LIB_TABLE = "common/lib_table_grid_tricks.cpp";

    const EVIDENCE symbolDialogPersists{ SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::TransferDataFromWindow",
                                         "PushOrRevert(" };
    const EVIDENCE fieldsTablePersists{ SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::TransferDataFromWindow",
                                        ANY_ROUTE };
    const std::string symbolDraft = "Symbol Properties dialog draft; the dialog's TransferDataFromWindow "
                                    "pushes or reverts one tracked commit.";
    const std::string fieldsDraft = "Fields table draft; the schematic table applies its edits in one commit.";
    const std::string libraryEdit = "Symbol editor library buffer, not the schematic; a schematic symbol "
                                    "returns through SCH_EDIT_FRAME::SaveSymbolToSchematic's commit.";
    const std::string libraryTable = "Library table grid, saved to library table files.";
    const std::string workbook = "Simulator workbook (.wbk), a separate document.";

    std::vector<OWNER> owners = {
        // Schematic editor frame and its tools.  Annotation (SCH_EDIT_FRAME::AnnotateSymbols) no
        // longer marks the document modified: it stages into the caller's commit, whose push does,
        // and a cancelled placement reverts it (see the AnnotateSymbols helper review).
        { SETUP_CONFIG, "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "", 1, DISPOSITION::ROUTED,
          { G_PROJECT, G_EMBEDDED, G_LIB_CACHE }, {},
          "Schematic Setup compares the project settings and the first top-level sheet, which carries the "
          "schematic-wide data its commit can change; an accepted change is recorded once, by the dialog's "
          "commit or by the tracked change, then marked modified." },
        { "eeschema/files-io.cpp", "SCH_EDIT_FRAME::OpenProjectFiles", "", 3, DISPOSITION::LOAD_BASELINE,
          { G_FORMAT, G_SYMBOL, G_SHEET, G_INSTANCES }, {},
          "Legacy conversion, page-number repair and bus migration run inside the load that creates a new "
          "SCHEMATIC and journal epoch; clients read that baseline after the load." },
        { "eeschema/sch_commit.cpp", "SCH_COMMIT::Push", "frame->", 1, DISPOSITION::COMMIT_MACHINERY, {},
          { { "eeschema/sch_commit.cpp", "SCH_COMMIT::pushSchEdit", "RecordCommittedChange(" } },
          "The commit marks its own edit modified after pushSchEdit recorded it." },
        { "eeschema/sch_edit_frame.cpp", "SCH_EDIT_FRAME::OnModify", "EDA_BASE_FRAME::", 1,
          DISPOSITION::COMMIT_MACHINERY, {}, {}, "The override chaining to its base implementation." },
        { "eeschema/sch_edit_frame.cpp", "SCH_EDIT_FRAME::CopyVariant", "", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant registry and overrides are staged in a commit." },
        { "eeschema/sch_edit_frame.cpp", "SCH_EDIT_FRAME::RemoveVariant", "", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant registry and overrides are staged in a commit." },
        { "eeschema/sch_edit_frame.cpp", "SCH_EDIT_FRAME::RenameVariant", "", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant registry and overrides are staged in a commit." },
        { "eeschema/toolbars_sch_editor.cpp", "SCH_EDIT_FRAME::ShowAddVariantDialog", "", 1,
          DISPOSITION::ROUTED, { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant registry is staged in a commit." },
        { "eeschema/api/api_handler_sch.cpp", "API_HANDLER_SCH::onModified", "m_frame->", 1,
          DISPOSITION::ROUTED_BY_CALLERS, { G_FORMAT, G_TITLE, G_PAGE, G_PROJECT }, {},
          "Called only after API title-block and page-settings edits that record their change." },
        { "eeschema/tools/assign_footprints.cpp", "SCH_EDITOR_CONTROL::ImportFPAssignments", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FIELD }, {},
          "Footprint link import is compared with the persisted state; unchanged imports record nothing." },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::doSyncSheetsPins", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_SHEET, G_TEXT }, {}, "Each synchronised pin edit is its own commit." },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "m_frame->", 2, DISPOSITION::ROUTED,
          { G_FORMAT, G_SYMBOL, G_FIELD, G_SHEET, G_INSTANCES, G_TEXT, G_LINE, G_JUNCTION, G_GROUP, G_LIB_CACHE,
            G_EMBEDDED, G_PROJECT }, {},
          "Importing sheet content or a design block loads cached definitions, schematic-wide data and bus "
          "aliases and renumbers duplicated identities outside the placement commit; a cancelled or refused "
          "placement compares those parts (see its tracker review) and is recorded only if it left a change." },
        { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "m_frame->", 2,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD, G_SHEET, G_INSTANCES, G_EMBEDDED, G_LIB_CACHE }, {},
          "Sheet Properties compares its staged sheet, whose file name field changes with a non-undoable "
          "file change, and marks a change the dialog left when cancelled; field placement after Symbol "
          "Properties is staged and pushed by its own commit." },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::Swap", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_FIELD, G_TEXT, G_LINE }, {},
          "A local commit is pushed first; otherwise the swap is staged in the caller's commit." },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::SwapPins", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_FORMAT, G_SYMBOL, G_LINE, G_JUNCTION }, {},
          "A local commit is pushed first; otherwise the swap is staged in the caller's commit." },
        { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", "m_frame->", 2,
          DISPOSITION::ROUTED, { G_FORMAT, G_TITLE, G_PAGE, G_EMBEDDED, G_PROJECT }, {},
          "Page Settings compares every screen's paper and title block, the schematic-wide data and the project "
          "settings (see its tracker review); only a real change becomes a revision, an undo entry and a "
          "modified document, and a cancel restores the preview." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::rescueProject", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_SYMBOL, G_LIB_CACHE, G_PROJECT }, {},
          "Symbol rescue is compared with the persisted schematic and project state." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::Undo", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_SYMBOL, G_SHEET }, {}, "Records an UNDO journal entry first." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::Redo", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_SYMBOL, G_SHEET }, {}, "Records a REDO journal entry first." },
        { "eeschema/tools/sch_group_tool.cpp", "SCH_GROUP_TOOL::Group", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_GROUP }, {}, "The new group is pushed as a commit." },
        { "eeschema/widgets/hierarchy_pane.cpp", "HIERARCHY_PANE::resyncAfterTopLevelSheetChange", "m_frame->",
          1, DISPOSITION::ROUTED_BY_CALLERS, { G_SHEET, G_INSTANCES, G_PROJECT }, {},
          "Called only after a top-level sheet is added or removed inside a tracked change." },

        // Schematic dialogs.
        { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::~DIALOG_ANNOTATE", "schFrame->", 1,
          DISPOSITION::ROUTED, { G_PROJECT }, {}, "Changed annotation settings record a committed change." },
        { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "m_Parent->", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_FIELD, G_SHEET, G_TEXT, G_LINE, G_JUNCTION, G_GROUP, G_PROJECT }, {},
          "Annotate stages its symbols and keeps the reference inventory in a tracked commit; repairing "
          "duplicated identities renumbers items of any kind outside it and is reported to the tracked "
          "change, which then records the change once and marks it even when nothing was staged." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::TransferDataFromWindow", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD, G_PROJECT }, {}, "Field and BOM edits are pushed as a commit." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onDeleteVariant", "m_parent->", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant deletion is pushed as a commit." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onVariantSelectionChange", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD }, {},
          "Pending field edits are pushed as a commit before switching variants." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onBomSettingsChanged", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_PROJECT }, {},
          "Release fallback when an export name change arrives outside an interactive export: the replaced "
          "name is unknown, so the change is recorded and marked modified without an undo entry." },
        { "eeschema/dialogs/dialog_symbol_remap.cpp", "DIALOG_SYMBOL_REMAP::OnRemapSymbols", "parent->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_SYMBOL, G_LIB_CACHE, G_PROJECT }, {},
          "Rescue and remapping are compared with the persisted schematic and project state." },
        { "eeschema/dialogs/dialog_update_from_pcb.cpp", "DIALOG_UPDATE_FROM_PCB::OnUpdateClick", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_SYMBOL, G_FIELD, G_TEXT, G_PIN_MAP }, {},
          "Back annotation pushes its commit; the reference refresh after it is tracked with it." },
        { "eeschema/fields_grid_table.cpp", "FIELDS_GRID_TABLE::SetValue", "m_dialog->", 1,
          DISPOSITION::DIALOG_DRAFT, { G_FIELD }, {},
          "Marks the owning dialog's title; see the FIELDS_GRID_TABLE helper for the persisting dialogs." },
        { "eeschema/fields_grid_table.cpp", "FIELDS_GRID_TABLE::SetValueAsBool", "m_dialog->", 1,
          DISPOSITION::DIALOG_DRAFT, { G_FIELD }, {},
          "Marks the owning dialog's title; see the FIELDS_GRID_TABLE helper for the persisting dialogs." },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnAddField", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnCheckBox", "", 1, DISPOSITION::DIALOG_DRAFT, { G_SYMBOL },
          { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnDeleteField", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnEditSpiceModel", "", 1, DISPOSITION::DIALOG_DRAFT,
          { G_FIELD }, { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnMoveDown", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnMoveUp", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnPinTableCellEdited", "", 1, DISPOSITION::DIALOG_DRAFT,
          { G_SYMBOL }, { symbolDialogPersists }, symbolDraft },
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::OnUnitChoice", "", 1, DISPOSITION::DIALOG_DRAFT, { G_SYMBOL },
          { symbolDialogPersists }, symbolDraft },

        // Shared base classes in common/ that schematic editors derive from or open.
        { FIELDS_TABLE, "FIELDS_TABLE_GRID_TRICKS::doPopupSelection", "m_dialog->", 2, DISPOSITION::DIALOG_DRAFT,
          { G_FIELD }, { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::ShowHideColumn", "", 1, DISPOSITION::DIALOG_DRAFT, { G_PROJECT },
          { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::OnViewControlsCellChanged", "", 2, DISPOSITION::DIALOG_DRAFT,
          { G_PROJECT }, { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::OnAddField", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::OnRemoveField", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::OnRenameField", "", 1, DISPOSITION::DIALOG_DRAFT, { G_FIELD },
          { fieldsTablePersists }, fieldsDraft },
        { FIELDS_TABLE, "DIALOG_FIELDS_TABLE::onBomSettingsChanged", "m_parentFrame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {},
          { { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onBomSettingsChanged", ANY_ROUTE },
            { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onBomSettingsChanged", "SetBomSettings(" } },
          "The base implementation serves only the library and board fields tables; the schematic table "
          "overrides it and applies the saved export file name as a commit." },
        { "common/dialogs/dialog_page_settings.cpp", "DIALOG_PAGES_SETTINGS::TransferDataFromWindow", "m_parent->",
          1, DISPOSITION::ROUTED_BY_CALLERS, { G_TITLE, G_PAGE, G_EMBEDDED, G_PROJECT },
          { { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", "DIALOG_EESCHEMA_PAGE_SETTINGSdlg(" },
            { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", "dlg.DeferModifiedNotification();" },
            { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", ANY_ROUTE } },
          "The schematic editor opens the shared page dialog only from PageSetup, which defers the dialog's "
          "own modified flag and records and marks a real change itself; other editors own other documents." },
        { "common/tool/group_tool.cpp", "GROUP_TOOL::Ungroup", "m_frame->", 1, DISPOSITION::ROUTED, { G_GROUP },
          {}, "The ungrouping commit is pushed first." },
        { "common/tool/group_tool.cpp", "GROUP_TOOL::AddToGroup", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_GROUP }, {}, "The group membership commit is pushed first." },
        { "common/tool/group_tool.cpp", "GROUP_TOOL::RemoveFromGroup", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_GROUP }, {}, "The group membership commit is pushed first." },
        { "common/widgets/wx_grid.cpp", "WX_GRID::CommitPendingChanges", "dlg->", 1, DISPOSITION::DIALOG_DRAFT, {},
          {}, "Marks the dialog that hosts the grid; the dialog's own transfer persists the edit." },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::onGridCellLeftClick", "model->", 1, DISPOSITION::OTHER_DOCUMENT, {},
          {}, libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::doPopupSelection", "model->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::paste_text", "model->", 2, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::AppendRowHandler", "model->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::DeleteRowHandler", "GetTable())->", 1, DISPOSITION::OTHER_DOCUMENT,
          {}, {}, libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::MoveUpHandler", "model->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryTable },
        { LIB_TABLE, "LIB_TABLE_GRID_TRICKS::MoveDownHandler", "model->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryTable },

        // Simulator.
        { SIM_FRAME, "SIMULATOR_FRAME::EditAnalysis", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_FRAME, "SIMULATOR_FRAME::EditAnalysis", "m_schematicFrame->", 2, DISPOSITION::ROUTED, { G_PROJECT }, {},
          "The analysis dialog writes the project's live ngspice settings; they are compared with the whole "
          "saved state, accepted or cancelled, and a real change is recorded and marks the schematic." },
        { "eeschema/sim/simulator_frame.cpp", "SIMULATOR_FRAME::SaveSettings", "m_schematicFrame->", 1,
          DISPOSITION::MODIFIED_FLAG_ONLY, { G_PROJECT }, {},
          "Copies already-live ngspice values into the project store so the next save writes them; the "
          "observed project state does not change here." },
        { "eeschema/sim/simulator_frame.cpp", "SIMULATOR_FRAME::OnModify", "KIWAY_PLAYER::", 1,
          DISPOSITION::COMMIT_MACHINERY, {}, {}, "The override chaining to its base implementation." },
        { "eeschema/sim/simulator_frame.cpp", "SIMULATOR_FRAME::StartSimulation", "", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::UpdateTunerValue", "m_schematicFrame->", 1, DISPOSITION::ROUTED,
          { G_FIELD }, {}, "A tuned value is applied as an undoable tracked commit." },
        { SIM_UI, "SIMULATOR_FRAME_UI::OnModify", "m_simulatorFrame->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          workbook },
        { SIM_UI, "MEASUREMENTS_GRID_TRICKS::doPopupSelection", "m_parent->", 2, DISPOSITION::OTHER_DOCUMENT, {},
          {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::AddMeasurement", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::AddTrace", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::AddTuner", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::CreateNewCursor", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::DeleteCursor", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::OnUpdateUI", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::RemoveTuner", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::SIMULATOR_FRAME_UI", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::SetUserDefinedSignals", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::ToggleSmithChart", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::onCursorsGridCellChanged", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::onMeasurementsGridCellChanged", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::onPlotClose", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::onPlotCursorUpdate", "", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { SIM_UI, "SIMULATOR_FRAME_UI::onSignalsGridCellChanged", "", 5, DISPOSITION::OTHER_DOCUMENT, {}, {},
          workbook },
        { "eeschema/tools/simulator_control.cpp", "SIMULATOR_CONTROL::NewAnalysisTab", "m_simulatorFrame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { "eeschema/tools/simulator_control.cpp", "SIMULATOR_CONTROL::ToggleDottedSecondary",
          "m_simulatorFrame->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { "eeschema/tools/simulator_control.cpp", "SIMULATOR_CONTROL::ToggleGrid", "m_simulatorFrame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },
        { "eeschema/tools/simulator_control.cpp", "SIMULATOR_CONTROL::ToggleLegend", "m_simulatorFrame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, workbook },

        // Symbol library editor and library tables.
        { "eeschema/dialogs/panel_sym_lib_table.cpp", "SYMBOL_GRID_TRICKS::optionsEditor", "tbl->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryTable },
        { "eeschema/dialogs/dialog_lib_fields_table.cpp", "DIALOG_LIB_FIELDS_TABLE::TransferDataFromWindow",
          "m_parent->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/dialogs/dialog_lib_fields_table.cpp", "LIB_FIELDS_EDITOR_GRID_TRICKS::doFieldsTablePopupSelection",
          "m_dlg->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/symbol_editor/symbol_edit_frame.cpp", "SYMBOL_EDIT_FRAME::OnModify", "EDA_BASE_FRAME::", 1,
          DISPOSITION::COMMIT_MACHINERY, {}, {}, "The override chaining to its base implementation." },
        { "eeschema/symbol_editor/symbol_editor.cpp", "SYMBOL_EDIT_FRAME::UpdateAfterSymbolProperties", "", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/symbol_editor/symbol_editor_undo_redo.cpp", "SYMBOL_EDIT_FRAME::GetSymbolFromRedoList", "", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/symbol_editor/symbol_editor_undo_redo.cpp", "SYMBOL_EDIT_FRAME::GetSymbolFromUndoList", "", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/tools/symbol_editor_control.cpp", "SYMBOL_EDITOR_CONTROL::RenameSymbol", "editFrame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/tools/symbol_editor_drawing_tools.cpp", "SYMBOL_EDITOR_DRAWING_TOOLS::PlaceAnchor", "m_frame->",
          1, DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::EditSymbolPinMaps", "m_frame->", 1, DISPOSITION::OTHER_DOCUMENT, {},
          {}, libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::Mirror", "m_frame->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::Swap", "m_frame->", 1, DISPOSITION::OTHER_DOCUMENT, {}, {},
          libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::editShapeProperties", "m_frame->", 1, DISPOSITION::OTHER_DOCUMENT,
          {}, {}, libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::editSymbolProperties", "m_frame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::editTextBoxProperties", "m_frame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { SYM_EDIT, "SYMBOL_EDITOR_EDIT_TOOL::editTextProperties", "m_frame->", 1, DISPOSITION::OTHER_DOCUMENT,
          {}, {}, libraryEdit },
        { "eeschema/tools/symbol_editor_pin_tool.cpp", "SYMBOL_EDITOR_PIN_TOOL::PlacePin", "m_frame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
        { "eeschema/tools/symbol_editor_pin_tool.cpp", "SYMBOL_EDITOR_PIN_TOOL::PushPinProperties", "m_frame->", 1,
          DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit },
    };

    for( const char* handler : { "OnAddBodyStyle", "OnAddField", "OnAddFootprintFilter", "OnAddJumperGroup",
                                 "OnBodyStyleMoveDown", "OnBodyStyleMoveUp", "OnCheckBox", "OnCombobox",
                                 "OnDeleteBodyStyle", "OnDeleteField", "OnEditFootprintFilter", "OnEditSpiceModel",
                                 "OnGridCellChanged", "OnMoveDown", "OnMoveUp", "OnRemoveJumperGroup",
                                 "OnSymbolNameText", "OnText", "OnUnitSpinCtrl", "OnUnitSpinCtrlEnter",
                                 "OnUnitSpinCtrlKillFocus", "onPowerCheckBox",
                                 "DIALOG_LIB_SYMBOL_PROPERTIES" } )
    {
        owners.push_back( { LIB_DIALOG, std::string( "DIALOG_LIB_SYMBOL_PROPERTIES::" ) + handler, "", 1,
                            DISPOSITION::OTHER_DOCUMENT, {}, {}, libraryEdit } );
    }

    return owners;
}


enum class CALLER_RULE
{
    ROUTED_CALL,     ///< A route runs before each call, in its scope or an enclosing one.
    STAGED_COMMIT,   ///< Each call stages into the commit named first in its arguments, which the
                     ///< same function pushes after the call (a revert undoes the staged edit too).
    EVIDENCE,        ///< Reviewed facts about the owning schematic code (see evidence).
    OTHER_DOCUMENT   ///< The caller edits a library, not the schematic.
};


/// A function calling a helper whose own OnModify is not routed, and how its calls are routed.
struct HELPER_CALLER
{
    std::string           file;
    std::string           function;
    int                   calls;
    CALLER_RULE           rule;
    std::vector<EVIDENCE> evidence;
};


struct HELPER
{
    std::string                identifier;
    std::vector<std::string>   roots;
    std::vector<HELPER_CALLER> callers;
    std::string                reason;
};


inline std::vector<HELPER> reviewedHelpers()
{
    const std::string api = "eeschema/api/api_handler_sch.cpp";
    const std::string editor = "common/api/api_handler_editor.cpp";
    const std::string drawing = "eeschema/tools/sch_drawing_tools.cpp";

    return {
        { "AnnotateSymbols", { "eeschema" },
          { { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", 1, CALLER_RULE::ROUTED_CALL, {} },
            { drawing, "SCH_DRAWING_TOOLS::ImportSheet", 2, CALLER_RULE::STAGED_COMMIT, {} },
            { drawing, "SCH_DRAWING_TOOLS::DrawSheet", 2, CALLER_RULE::STAGED_COMMIT, {} },
            { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::RepeatDrawItem", 1, CALLER_RULE::STAGED_COMMIT,
              {} } },
          "Annotation edits the caller's commit, keeping the reference inventory in it so a cancelled "
          "placement returns the designators it handed out; the caller's push marks the document modified.  "
          "Annotate Schematic declares a tracked change on that commit first, which pushes or reverts it and "
          "records the identities annotation replaced outside it (see its tracker review); it is the only caller "
          "that asks for that repair (identityRepairCallers)." },
        { "resyncAfterTopLevelSheetChange", { "eeschema/widgets/hierarchy_pane.cpp" },
          { { "eeschema/widgets/hierarchy_pane.cpp", "HIERARCHY_PANE::onRightClick", 2, CALLER_RULE::ROUTED_CALL,
              {} } },
          "New and deleted top-level sheets are tracked by the context-menu owner." },
        { "onModified", { api, editor },
          { { api, "API_HANDLER_SCH::handleSetPageSettings", 1, CALLER_RULE::ROUTED_CALL, {} },
            { editor, "API_HANDLER_EDITOR::handleSetTitleBlockInfo", 1, CALLER_RULE::EVIDENCE,
              { { api, "API_HANDLER_SCH::handleSetTitleBlockInfo", "API_HANDLER_EDITOR::handleSetTitleBlockInfo(" },
                { api, "API_HANDLER_SCH::handleSetTitleBlockInfo", ANY_ROUTE } } },
            { editor, "API_HANDLER_EDITOR::handleSetPageSettings", 1, CALLER_RULE::EVIDENCE,
              { { api, "API_HANDLER_SCH::API_HANDLER_SCH", "&API_HANDLER_SCH::handleSetPageSettings" } } } },
          "Schematic title blocks reach the shared editor handler only through the schematic override, "
          "which records the change; schematic page settings use their own registered handler." },
        { "FIELDS_GRID_TABLE", { "eeschema" },
          { { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::DIALOG_SYMBOL_PROPERTIES", 1, CALLER_RULE::EVIDENCE,
              { { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::TransferDataFromWindow", "PushOrRevert(" } } },
            { "eeschema/dialogs/dialog_label_properties.cpp", "DIALOG_LABEL_PROPERTIES::DIALOG_LABEL_PROPERTIES", 1,
              CALLER_RULE::EVIDENCE,
              { { "eeschema/dialogs/dialog_label_properties.cpp", "DIALOG_LABEL_PROPERTIES::TransferDataFromWindow",
                  ANY_ROUTE } } },
            { "eeschema/dialogs/dialog_sheet_properties.cpp", "DIALOG_SHEET_PROPERTIES::DIALOG_SHEET_PROPERTIES", 1,
              CALLER_RULE::EVIDENCE, { { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::EditProperties",
                                         "PushOrRevert(" } } },
            { "eeschema/dialogs/dialog_lib_symbol_properties.cpp",
              "DIALOG_LIB_SYMBOL_PROPERTIES::DIALOG_LIB_SYMBOL_PROPERTIES", 1, CALLER_RULE::OTHER_DOCUMENT, {} } },
          "Every dialog that hosts the fields grid persists through a routed owner (or edits a library)." },
    };
}


/// AnnotateSymbols' ninth argument asks it to repair item identities that sheets repeat.  The
/// repair gives items new identities outside the caller's commit, which can neither compare nor
/// restore them, so AnnotateSymbols returns how many it replaced.  Only a caller reviewed here,
/// which reports that count as a change outside its commit, may ask for the repair; every other
/// call must pass the literal false.
inline std::vector<EVIDENCE> identityRepairCallers()
{
    return { { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "int replaced = m_Parent->AnnotateSymbols(" },
             { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "if( replaced > 0 ) change.ChangedOutsideCommit();" } };
}


/// The repair argument of an AnnotateSymbols call (comments removed), or empty when the call
/// does not pass the eleven arguments the review knows.
inline std::string identityRepairArgument( const CALL_SITE& aCall )
{
    const std::vector<std::string> arguments = topLevelArgumentTexts( "(" + aCall.arguments + ")", 0 );
    return arguments.size() == 11 ? arguments[8] : std::string();
}


/// A reviewed SCH_TRACKED_CHANGE declaration site.  Every tracker compares the whole persisted
/// state (every screen and the project settings, the same groups as the lifecycle digest)
/// unless it compares only its commit's staged items, named persisted parts and the project
/// settings, or named screens and the project settings, which needs a reason and may need
/// evidence that the owner reports or compares what lies outside that narrower comparison.
struct TRACKER_SITE
{
    std::string           file;
    std::string           function;
    int                   wholeState;   ///< Two-argument declarations: every screen and the project.
    int                   staged;       ///< Three-argument declarations: only the named commit's items.
    int                   parts;        ///< Three-argument SCH_PERSISTED_PARTS declarations: named parts.
    int                   screens;      ///< Four-argument declarations: named screens and the project.
    std::string           reason;       ///< Why the narrower trackers cannot miss a change.
    std::vector<EVIDENCE> evidence = {}; ///< Code the reason depends on.
};


/// Every tracker declaration.  Whole-state trackers write the whole design twice (on the
/// largest demo design, seconds per action in a Debug build), so they are only for edits whose
/// reach outside a commit no narrower comparison covers; an owner whose every edit is staged
/// compares the staged items instead, one whose reach outside its commits is a known set of
/// persisted parts (pages, schematic-wide data, item identities, named library caches) compares
/// those parts, and one that changes only the project settings and named screens compares
/// those, at a cost that does not grow with the size of the sheets.
inline std::vector<TRACKER_SITE> reviewedTrackers()
{
    const std::string STATE_PARTS = "eeschema/api/api_sch_state_groups.cpp";
    const std::string PAGE_DIALOG = "common/dialogs/dialog_page_settings.cpp";
    const std::string SCH_PAGE_DIALOG = "eeschema/dialogs/dialog_eeschema_page_settings.cpp";

    return {
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::TransferDataFromWindow", 0, 1, 0, 0,
          "Every persisted edit is staged: the symbol and the other units it synchronises, with the symbol's "
          "own definition (embedded files and pin maps are applied after the snapshot and the definition is "
          "compared with the item), and other symbols only on a real pin-map edit." },
        { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", 0, 1, 0, 0,
          "Annotation stages every symbol it annotates before annotating it, and the commit keeps the "
          "reference inventory from before, which the staged comparison compares too, so designators handed "
          "out are seen even when every symbol compares unchanged.  Repairing duplicated identities renumbers "
          "items outside the commit; AnnotateSymbols returns how many it replaced and the dialog reports them "
          "as a change outside the commit.",
          { { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "AnnotateSymbols( &commit," },
            { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "change.ChangedOutsideCommit()" },
            { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "change.PushOrRevert( commit," },
            { ANNOTATE_SOURCE, "SCH_EDIT_FRAME::AnnotateSymbols", "replaced = screens.ReplaceDuplicateTimeStamps()" },
            { ANNOTATE_SOURCE, "SCH_EDIT_FRAME::AnnotateSymbols", "return replaced;" },
            { ANNOTATE_SOURCE, "SCH_EDIT_FRAME::AnnotateSymbols", "aCommit->KeepReferenceInventory()" },
            { ANNOTATE_SOURCE, "SCH_EDIT_FRAME::AnnotateSymbols",
              "aCommit->Modify( symbol, sheet->LastScreen() ); ref.Annotate();" },
            { "eeschema/sch_commit.cpp", "SCH_COMMIT::PersistsChange", "if( m_referenceInventoryKept )" } } },
        { "eeschema/dialogs/dialog_symbol_remap.cpp", "DIALOG_SYMBOL_REMAP::OnRemapSymbols", 1, 0, 0, 0, "" },
        { "eeschema/dialogs/dialog_update_from_pcb.cpp", "DIALOG_UPDATE_FROM_PCB::OnUpdateClick", 1, 0, 0, 0, "" },
        { "eeschema/sim/simulator_frame_ui.cpp", "SIMULATOR_FRAME_UI::UpdateTunerValue", 0, 1, 0, 0,
          "A tuned value is written into the staged symbol's fields and nowhere else." },
        { "eeschema/tools/assign_footprints.cpp", "SCH_EDITOR_CONTROL::ImportFPAssignments", 1, 0, 0, 0, "" },
        { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::rescueProject", 1, 0, 0, 0, "" },
        { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", 0, 0, 1, 0,
          "Page Settings writes outside any commit, but only the paper and title block of the current screen "
          "and of each screen it exports them to, the drawing sheet file name (a project setting) and the "
          "embedded drawing sheet, which choosing a file adds and accepting the dialog removes (schematic "
          "embedded files).  It writes no item, identity or library cache, so the pages, the schematic-wide "
          "data and the project settings are everything it can change; each is compared as it is saved.",
          { { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", "SCH_PERSISTED_PARTS::PageSettings()" },
            { STATE_PARTS, "SCH_PERSISTED_PARTS::PageSettings", "parts.pages = true;" },
            { STATE_PARTS, "SCH_PERSISTED_PARTS::PageSettings", "parts.schematicWide = true;" },
            { PAGE_DIALOG, "DIALOG_PAGES_SETTINGS::SavePageSettings", "m_parent->SetDrawingSheetFileName( fileName );" },
            { PAGE_DIALOG, "DIALOG_PAGES_SETTINGS::SavePageSettings", "m_parent->SetPageSettings( m_pageInfo );" },
            { PAGE_DIALOG, "DIALOG_PAGES_SETTINGS::SavePageSettings", "m_parent->SetTitleBlock( m_tb );" },
            { PAGE_DIALOG, "DIALOG_PAGES_SETTINGS::SavePageSettings", "m_embeddedFiles->RemoveFile( name );" },
            { PAGE_DIALOG, "DIALOG_PAGES_SETTINGS::OnWksFileSelection", "m_embeddedFiles->AddFile( fn, true );" },
            { SCH_PAGE_DIALOG, "DIALOG_EESCHEMA_PAGE_SETTINGS::onSavePageSettings",
              "screen->SetPageSettings( m_pageInfo );" },
            { SCH_PAGE_DIALOG, "DIALOG_EESCHEMA_PAGE_SETTINGS::onSavePageSettings", "screen->SetTitleBlock( tb2 );" } } },
        { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", 0, 2, 0, 0,
          "Sheet Properties stages its sheet and also compares the sheet's screen and that screen's file name, "
          "which a file change sets outside the commit (a rename that fails later keeps the screen's new file "
          "name while the dialog restores the field) and reports as a change outside the commit; loading the "
          "file and clearing annotation are part of that one revision.  Field placement after Symbol "
          "Properties moves only that symbol's fields: a symbol still being placed, pasted or moved belongs to "
          "the carrying tool's commit and gets no tracker of its own; otherwise, when the dialog recorded "
          "nothing, it is staged on the symbol, and when it did, it is part of the dialog's revision, whose "
          "undo entry restores it.",
          { { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "symbol->GetEditFlags() != 0" },
            { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "sheet->GetScreen() != screenBefore" },
            { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "change.ChangedOutsideCommit()" } } },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", 0, 0, 1, 0,
          "Every placed item is staged in the placement commit, which a kept placement pushes (one revision) "
          "and a cancel reverts, returning the designators its annotation handed out.  Outside that commit, "
          "loading the file appends into the screen it was chosen for, merging that screen's cached library "
          "definitions and refreshing an equal one in place; renumbers whichever duplicate of a repeated "
          "identity comes later in sheet order, on any screen; and adds the embedded files, embedded fonts flag, "
          "net chains and bus aliases the file carries.  The item identities of every screen, that screen's "
          "library cache, the schematic-wide data and the project settings (bus aliases, reference inventory) "
          "are compared as they are saved; the file's child sheets get screens of their own, which leave with "
          "their placed sheet.",
          { { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "SCH_PERSISTED_PARTS::SheetImport( sheetPath.LastScreen() )" },
            { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "m_frame->LoadSheetFromFile( sheetPath.Last(), &sheetPath," },
            { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "commit.Added( item, screen );" },
            { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "commit.Revert();" },
            { STATE_PARTS, "SCH_PERSISTED_PARTS::SheetImport", "parts.identities = true;" },
            { STATE_PARTS, "SCH_PERSISTED_PARTS::SheetImport", "parts.schematicWide = true;" },
            { STATE_PARTS, "SCH_PERSISTED_PARTS::SheetImport", "parts.libraryCaches = { aScreen };" },
            { "eeschema/sheet.cpp", "SCH_EDIT_FRAME::LoadSheetFromFile", "aSheet->GetScreen()->Append( newScreen );" },
            { "eeschema/sheet.cpp", "SCH_EDIT_FRAME::LoadSheetFromFile", "allProjectScreens.ReplaceDuplicateTimeStamps();" },
            { "eeschema/sch_screen.cpp", "SCH_SCREENS::ReplaceDuplicateTimeStamps",
              "const_cast<KIID&>( item->m_Uuid ) = KIID();" },
            { "eeschema/sch_screen.cpp", "SCH_SCREEN::Append",
              "*foundSymbol->GetEmbeddedFiles() = *symbol->GetLibSymbolRef()->GetEmbeddedFiles();" } } },
        { SETUP_CONFIG, "SCH_EDIT_FRAME::ShowSchematicSetupDialog", 0, 0, 0, 1,
          "Setup changes the project settings and, through its own commit (already a revision when pushed), the "
          "schematic-wide data saved with the first top-level sheet and library caches.  Outside the commit only "
          "the project settings change; connectivity is cleaned up across every sheet only when the bus "
          "aliases, themselves project settings, changed, which is then already a change.",
          { { SETUP_CONFIG, "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "{ Schematic().RootScreen() }" },
            { SETUP_CONFIG, "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "if( oldAliases != newAliases )" } } },
        { SIM_FRAME, "SIMULATOR_FRAME::EditAnalysis", 0, 0, 0, 1,
          "The simulation settings dialog writes only the ngspice settings, which are project settings; the "
          "analysis command is workbook state and no sheet is edited.",
          { { SIM_FRAME, "SIMULATOR_FRAME::EditAnalysis", "{ schematic.RootScreen() }" } } },
        { "eeschema/widgets/hierarchy_pane.cpp", "HIERARCHY_PANE::onRightClick", 2, 0, 0, 0, "" },
    };
}


/// A routed native path whose change is not yet proven end to end by a rendered journey.
/// Each keeps native tracking reported as incomplete until its journey exists.
struct UNPROVEN
{
    std::string file;
    std::string function;
    std::string needle;          ///< Evidence that the route is in place ("@routes" or text).
    int         estimateLines;
    std::string reason;
};


inline std::vector<UNPROVEN> unprovenRoutes()
{
    return {
        { "eeschema/dialogs/dialog_symbol_remap.cpp", "DIALOG_SYMBOL_REMAP::OnRemapSymbols", ANY_ROUTE, 120,
          "Remap Symbols needs a legacy-library project fixture (cache library, no symbol library table)." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::rescueProject", ANY_ROUTE, 60,
          "Rescue Symbols needs the same legacy-library fixture as Remap Symbols." },
        { "eeschema/dialogs/dialog_update_from_pcb.cpp", "DIALOG_UPDATE_FROM_PCB::OnUpdateClick", ANY_ROUTE, 150,
          "Update Schematic from PCB needs a board of the same project open in the PCB editor." },
        { "eeschema/tools/assign_footprints.cpp", "SCH_EDITOR_CONTROL::ImportFPAssignments", ANY_ROUTE, 80,
          "Import Footprint Assignments needs a .cmp link file chosen through the native file dialog." },
        { "eeschema/tools/assign_footprints.cpp", "SCH_EDITOR_CONTROL::AssignFootprints", ANY_ROUTE, 120,
          "Footprint assignment from the footprint assignment tool (CvPcb), including hiding an empty "
          "footprint field inside the same commit." },
        { "eeschema/widgets/sch_properties_panel.cpp", "SCH_PROPERTIES_PANEL::onEditPinMap", "SelectPinMapPage()",
          60, "The properties panel pin-map button needs a symbol with an associated footprint; its dialog is "
              "the tracked Symbol Properties dialog." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onBomSettingsChanged", ANY_ROUTE, 70,
          "Exporting a BOM to a new file name from the Symbol Fields Table." },
        { "eeschema/sim/simulator_frame_ui.cpp", "SIMULATOR_FRAME_UI::UpdateTunerValue", ANY_ROUTE, 90,
          "Applying a tuned value needs a simulation fixture with a tuner." },
        { SIM_FRAME, "SIMULATOR_FRAME::EditAnalysis", ANY_ROUTE, 60,
          "Editing the simulation settings needs the simulator window open on a simulation fixture." },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "SCH_ACTIONS::placeDesignBlock", 120,
          "Placing a design block shares the proven sheet-import placement, but its journey needs a design "
          "block library registered in the fixture project before the editor starts." },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::PlaceSymbol", "RestoreReferenceInventory(", 110,
          "Cancelling a symbol placed from the symbol chooser returns the designator its annotation handed out "
          "through Place Symbol's own copy of the reference inventory, restored when the carried symbol is "
          "dropped, not through a commit.  Its journey needs a symbol library registered in the fixture "
          "project before the editor starts; only the copy and restore helpers are checked (unit level)." },
        { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "change.ChangedOutsideCommit()", 90,
          "A Sheet Properties file change that is applied to the sheet's screen and then fails (the dialog "
          "restores the file name field) is reported as a change outside the commit; the journey has no file "
          "change that fails after it was applied, so only the staged tracker's report is checked (unit "
          "level)." },
    };
}


/// A routed owner whose change and whose cancel and no-op precision the rendered journey
/// proves: among the statements the journey method always runs, an awaited OneChange(...) call
/// asserts exactly one revision with this journal description, an awaited Unchanged(...) call
/// asserts each cancel or no-op step left the revision, saved state, modified flag and journal
/// alone, and an awaited Undone(...) call asserts each undo step was exactly one undo revision
/// (its caller then requires the state that undo restores).
struct PROOF
{
    std::string              file;          ///< The owner's source.
    std::string              function;      ///< The owner; it must still route its change.
    std::string              description;   ///< The revision's exact journal description.
    std::vector<std::string> unchanged;     ///< The exact cancel and no-op step names.
    std::vector<std::string> undone = {};   ///< The exact undo step names (awaited Undone(...)).
};


inline std::vector<PROOF> journeyProofs()
{
    const std::string pane = "eeschema/widgets/hierarchy_pane.cpp";

    return {
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::TransferDataFromWindow", "Edit Symbol Properties",
          { "Cancelling Symbol Properties", "Accepting unchanged Symbol Properties" } },
        { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "Edit Sheet Properties",
          { "Cancelling Sheet Properties", "Accepting unchanged Sheet Properties" } },
        // Field placement after Symbol Properties on a symbol that a duplication or a move still
        // carries: the carrying tool's cancel must leave nothing, not even an undo entry.
        { EDIT_TOOL, "SCH_EDIT_TOOL::EditProperties", "Edit Symbol Properties",
          { "Cancelling a duplicated symbol after editing its properties",
            "Cancelling a symbol move after editing its properties" } },
        { pane, "HIERARCHY_PANE::onRightClick", "New Top-Level Sheet", { "Cancelling a new top-level sheet" } },
        { pane, "HIERARCHY_PANE::onRightClick", "Delete Top-Level Sheet",
          { "Declining to delete a top-level sheet" } },
        { DRAWING_TOOLS, "SCH_DRAWING_TOOLS::ImportSheet", "Import Schematic Sheet Content",
          { "Cancelling a sheet import" } },
        { EDITOR_CONTROL, "SCH_EDITOR_CONTROL::PageSetup", "Edit Page Settings",
          { "Cancelling Page Settings", "Accepting unchanged Page Settings" } },
        { SETUP_CONFIG, "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "Edit Schematic Setup",
          { "Cancelling Schematic Setup", "Accepting unchanged Schematic Setup" } },
        { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::~DIALOG_ANNOTATE", "Edit Annotation Settings",
          { "Closing unchanged Annotate Schematic" } },
        // Annotate itself.  Repairing one duplicated identity with nothing else to annotate is one
        // revision, both when every symbol is staged and pushed (the undo that follows is one
        // revision) and when nothing is staged (the dialog records it; the undo that follows undoes
        // the fence edit below it), and so is handing out a designator again with every staged
        // symbol unchanged (the kept reference inventory differs).  Annotating an annotated
        // schematic is no revision and leaves no undo entry (the undo undoes the fence edit).
        { ANNOTATE_DIALOG, "DIALOG_ANNOTATE::OnAnnotateClick", "Annotate",
          { "Annotating an annotated schematic" },
          { "Undoing an identity repair that staged every symbol", "Undoing after annotating an annotated schematic",
            "Undoing after an identity repair that staged nothing" } },
    };
}


/// Assertions the journey's step helpers must keep: Unchanged compares the revision, the saved
/// state digest, the modified flag and the journal; OneChange requires exactly one change with
/// the expected kind and description.
inline std::map<std::string, std::vector<std::string>> journeyStepAssertions()
{
    return {
        { "Unchanged",
          { "Assert.AreEqual(baseline.Revision,after.Revision", "Assert.AreEqual(baseline.StateSha256,after.StateSha256",
            "Assert.AreEqual(baseline.NativeContentDirty,after.NativeContentDirty",
            "Assert.IsEmpty((awaitChanges(baseline)).Changes" } },
        { "OneChange",
          { "Assert.IsFalse(journal.ResetRequired)", "Assert.HasCount(1,journal.Changes",
            "Assert.AreEqual(kind,change.Kind)", "Assert.AreEqual(description,change.Description)" } },
        // An undo step must see the undo happen: a lost key or an undo with nothing to undo records
        // no revision, so the step fails instead of passing unseen.
        { "Undone",
          { "awaitAdvanced(after,", "Assert.IsFalse(journal.ResetRequired)", "Assert.HasCount(1,journal.Changes",
            "Assert.AreEqual(SchematicChange.Types.Kind.Undo,journal.Changes.Single().Kind" } },
    };
}


/// The journey steps found in the journey method: descriptions asserted by OneChange and step
/// names asserted by Unchanged, or failures when the method or a step helper is missing.  Only
/// statements the method always runs count; a step call found only elsewhere in the method
/// (a branch, loop, catch, local function, lambda or conditional expression, or after an early
/// return) is listed in @a conditional instead.
struct JOURNEY_STEPS
{
    std::set<std::string>    changes;
    std::set<std::string>    unchanged;
    std::set<std::string>    undone;
    std::set<std::string>    conditional;
    std::vector<std::string> failures;
};


inline JOURNEY_STEPS journeySteps( const std::string& aSource, const std::string& aMethod )
{
    JOURNEY_STEPS        steps;
    const BLANKED_SOURCE blanked = blankCSharp( aSource );
    const auto [begin, end] = csharpMethodBody( blanked.code, aMethod );

    if( begin == std::string::npos )
    {
        steps.failures.push_back( "The journey method " + aMethod + " is missing." );
        return steps;
    }

    for( const auto& [helper, needles] : journeyStepAssertions() )
    {
        const auto [helperBegin, helperEnd] = csharpMethodBody( blanked.code, helper, begin, end );

        if( helperBegin == std::string::npos )
        {
            steps.failures.push_back( aMethod + " no longer defines its " + helper + " step helper." );
            continue;
        }

        const std::string body = withoutSpaces( blanked.code.substr( helperBegin, helperEnd - helperBegin ) );

        for( const std::string& needle : needles )
        {
            if( body.find( needle ) == std::string::npos )
                steps.failures.push_back( helper + " in " + aMethod + " no longer asserts " + needle + "." );
        }
    }

    for( const auto& [helper, found] : { std::make_pair( std::string( "OneChange" ), &steps.changes ),
                                         std::make_pair( std::string( "Unchanged" ), &steps.unchanged ),
                                         std::make_pair( std::string( "Undone" ), &steps.undone ) } )
    {
        for( const AWAITED_CALL& call : awaitedLiteralCalls( aSource, blanked.code, helper, 1, begin, end ) )
        {
            if( csharpUnconditionalStatement( blanked.code, begin, end, call.await ) )
                found->insert( call.literal );
            else
                steps.conditional.insert( call.literal );
        }
    }

    return steps;
}


/// How a native call that deletes ERC markers or changes their exclusion keeps the saved
/// exclusions tracked.  An excluded marker is a saved exclusion of the project, but deleting or
/// excluding a marker never marks the document modified by itself, so the OnModify owner review
/// cannot see these paths; they are reviewed here instead.
enum class EXCLUSION_RULE
{
    RECORDED,     ///< Its function records the change: a tracker, a recorded change or a commit push.
    HELPER,       ///< Inside a helper whose every call site is reviewed here as well.
    RERESOLVED,   ///< The exclusions are recorded first and resolved again afterwards (evidence).
    STAGED,       ///< Staged in the SCH_COMMIT that its caller pushes (evidence).
    PROVIDER,     ///< The ERC items provider acting for a reviewed tree-model deletion (evidence).
    RESOLUTION    ///< Restores the saved exclusions onto markers; not a user edit.
};


struct EXCLUSION_SITE
{
    std::string           file;
    std::string           function;
    std::string           identifier;
    int                   calls;
    EXCLUSION_RULE        rule;
    std::vector<EVIDENCE> evidence;
    std::string           reason;
};


/// Calls that delete ERC markers or change whether a marker is excluded.
inline std::set<std::string> exclusionIdentifiers()
{
    return { "DeleteMarkers", "DeleteAllMarkers", "DeleteMarker", "DeleteCurrentItem", "DeleteItems",
             "SetExcluded", "SetMarkerExcluded", "setMarkerExcluded", "deleteAllMarkers" };
}


inline std::vector<EXCLUSION_SITE> reviewedExclusionSites()
{
    const std::string dialog = "eeschema/dialogs/dialog_erc.cpp";
    const std::string provider = "eeschema/erc/erc_settings.cpp";
    const std::string commit = "eeschema/sch_commit.cpp";

    return {
        { dialog, "DIALOG_ERC::OnDeleteOneClick", "DeleteCurrentItem", 1, EXCLUSION_RULE::RECORDED, {},
          "Deleting an excluded violation pushes one undoable commit that keeps the exclusion by sort key; "
          "deleting a computed violation reverts the unchanged commit and records nothing." },
        { dialog, "DIALOG_ERC::OnDeleteAllClick", "deleteAllMarkers", 1, EXCLUSION_RULE::RECORDED, {},
          "Deleting the exclusions with every marker pushes one undoable commit; deleting only computed "
          "violations records nothing." },
        { dialog, "DIALOG_ERC::OnRunERCClick", "deleteAllMarkers", 1, EXCLUSION_RULE::RERESOLVED,
          { { dialog, "DIALOG_ERC::OnRunERCClick", "RecordERCExclusions();deleteAllMarkers(true);" },
            { dialog, "DIALOG_ERC::OnRunERCClick", "testErc();" },
            { dialog, "DIALOG_ERC::testErc", "RunTests(" },
            { "eeschema/erc/erc.cpp", "ERC_TESTER::RunTests", "ResolveERCExclusionsPostUpdate();" } },
          "Running the checks records every exclusion before clearing the markers and resolves the same "
          "exclusions onto the new markers afterwards." },
        { dialog, "DIALOG_ERC::deleteAllMarkers", "DeleteItems", 1, EXCLUSION_RULE::HELPER, {},
          "Removes the tree nodes of the markers the helper deletes." },
        { dialog, "DIALOG_ERC::deleteAllMarkers", "DeleteAllMarkers", 1, EXCLUSION_RULE::HELPER, {},
          "Deletes the ERC markers for the helper's reviewed callers." },
        { dialog, "DIALOG_ERC::OnERCItemRClick", "setMarkerExcluded", 3, EXCLUSION_RULE::RECORDED, {},
          "Exclusions, restorations and comments push one undoable 'Edit ERC overrides' commit." },
        { dialog, "DIALOG_ERC::OnERCItemRClick", "DeleteMarkers", 1, EXCLUSION_RULE::RECORDED, {},
          "Ignoring a rule deletes its markers, exclusions included, inside the undoable severity commit." },
        { dialog, "DIALOG_ERC::ExcludeMarker", "setMarkerExcluded", 1, EXCLUSION_RULE::RECORDED, {},
          "The exclusion hotkey and canvas action push one undoable 'Edit ERC overrides' commit." },
        { dialog, "setMarkerExcluded", "SetMarkerExcluded", 1, EXCLUSION_RULE::HELPER, {},
          "Routes the exclusion through the provider so its cached counts follow it." },
        { provider, "SHEETLIST_ERC_ITEMS_PROVIDER::SetMarkerExcluded", "SetExcluded", 2, EXCLUSION_RULE::HELPER,
          {}, "Changes the exclusion for the dialog helper's reviewed callers." },
        { provider, "SHEETLIST_ERC_ITEMS_PROVIDER::DeleteItem", "DeleteMarker", 1, EXCLUSION_RULE::PROVIDER,
          { { "common/rc_item.cpp", "RC_TREE_MODEL::DeleteItems", "DeleteItem(" } },
          "The provider deletes a marker only for a deep tree-model deletion; every schematic tree-model "
          "deletion is reviewed here." },
        { "eeschema/sch_screen.cpp", "SCH_SCREENS::DeleteAllMarkers", "DeleteMarkers", 1, EXCLUSION_RULE::HELPER,
          {}, "Deletes every marker of a type for its reviewed callers." },
        { commit, "SCH_COMMIT::SetErcSettings", "SetExcluded", 2, EXCLUSION_RULE::STAGED,
          { { commit, "SCH_COMMIT::SetErcSettings", "if(!StageErcEdit())" },
            { commit, "SCH_COMMIT::SetErcSettings", "history.created.insert(exclusion.marker->m_Uuid);" } },
          "An ERC replacement captures the exclusions by sort key in its commit before changing a marker, "
          "and records every marker it adds by identity; history never keeps a marker pointer." },
        { commit, "SCH_ERC_HISTORY::Restore", "SetExcluded", 3, EXCLUSION_RULE::RESOLUTION, {},
          "Undo, Redo and Revert return the markers to a history entry's exclusions; Undo and Redo record "
          "their own journal entries and Revert discards a commit that was never pushed." },
        { "eeschema/schematic.cpp", "SCHEMATIC::ResolveERCExclusions", "SetExcluded", 2,
          EXCLUSION_RULE::RESOLUTION, {},
          "Restores the saved exclusions onto markers after a load or an ERC run." },
    };
}


struct WRITER
{
    std::string              file;
    std::vector<std::string> functions;   ///< Empty: every function in the file writes.
};


/// Every source that writes the persisted schematic: the SCH_IO_KICAD_SEXPR writer, its shared
/// shape and library-cache writers, and the common formatters it calls.
inline std::vector<WRITER> schematicWriters()
{
    return {
        { "eeschema/sch_io/kicad_sexpr/sch_io_kicad_sexpr.cpp", {} },
        { "eeschema/sch_io/kicad_sexpr/sch_io_kicad_sexpr_common.cpp", {} },
        { "eeschema/sch_io/kicad_sexpr/sch_io_kicad_sexpr_lib_cache.cpp",
          { "SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol", "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveDcmInfoAsFields",
            "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePinMapData", "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveSymbolDrawItem",
            "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveField", "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePin",
            "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveText", "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveTextBox" } },
        { "common/title_block.cpp", { "TITLE_BLOCK::Format" } },
        { "common/page_info.cpp", { "PAGE_INFO::Format" } },
        { "common/eda_text.cpp", { "EDA_TEXT::Format" } },
        { "common/stroke_params.cpp", { "STROKE_PARAMS::Format" } },
        { "common/embedded_files.cpp", { "EMBEDDED_FILES::WriteEmbeddedFiles" } },
    };
}


/// Writer groups whose state belongs to placed schematic items.  Every item edit is staged in an
/// SCH_COMMIT (Modify, Add or Remove keeps the item's previous copy) and SCH_COMMIT::pushSchEdit
/// records it, so these groups are covered by the commit machinery; direct owners that change
/// them outside a commit are listed with the groups they change.
inline std::set<std::string> commitCoveredGroups()
{
    return { "SCH_IO_KICAD_SEXPR::saveBitmap",  "SCH_IO_KICAD_SEXPR::saveBusEntry",
             "SCH_IO_KICAD_SEXPR::saveField",   "SCH_IO_KICAD_SEXPR::saveGroup",
             "SCH_IO_KICAD_SEXPR::saveJunction", "SCH_IO_KICAD_SEXPR::saveLine",
             "SCH_IO_KICAD_SEXPR::saveNoConnect", "SCH_IO_KICAD_SEXPR::saveRuleArea",
             "SCH_IO_KICAD_SEXPR::saveSheet",
             "SCH_IO_KICAD_SEXPR::saveSymbol",  "SCH_IO_KICAD_SEXPR::saveTable",
             "SCH_IO_KICAD_SEXPR::saveText",    "SCH_IO_KICAD_SEXPR::saveTextBox",
             "formatPinMapOverride",            "formatFill", "formatArc", "formatCircle", "formatRect",
             "formatBezier", "formatPoly", "formatEllipse", "formatEllipseArc", "EDA_TEXT::Format",
             "STROKE_PARAMS::Format",
             // The cached library symbol definitions change with the symbols that use them.
             "SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol", "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveField",
             "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePin", "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePinMapData",
             "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveText", "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveTextBox" };
}


/// The pinned persisted fields of every schematic writer.  A writer change must be reviewed
/// here and in the owner groups above before the oracle passes again.
inline std::map<std::string, std::set<std::string>> pinnedWriterFields()
{
    return {
        { "EDA_TEXT::Format",
          { "bold", "color", "effects", "face", "font", "href", "italic", "justify", "line_spacing", "size",
            "thickness" } },
        { "EMBEDDED_FILES::WriteEmbeddedFiles",
          { "checksum", "data", "embedded_files", "file", "name", "type" } },
        { "PAGE_INFO::Format", { "paper" } },
        { "SCH_IO_KICAD_SEXPR::Format",
          { "color", "embedded_fonts", "excluded_nets", "excluded_pin", "from", "generator",
            "generator_version", "kicad_sch", "lib_symbols", "net_chain", "net_class", "nets", "to", "version" } },
        { "SCH_IO_KICAD_SEXPR::saveBitmap", { "at", "image", "locked", "scale" } },
        { "SCH_IO_KICAD_SEXPR::saveBusEntry", { "at", "bus_entry", "locked", "size" } },
        { "SCH_IO_KICAD_SEXPR::saveField", { "at", "do_not_autoplace", "hide", "property", "show_name" } },
        { "SCH_IO_KICAD_SEXPR::saveGroup", { "group", "lib_id", "locked", "members" } },
        { "SCH_IO_KICAD_SEXPR::saveInstances", { "page", "path", "sheet_instances" } },
        { "SCH_IO_KICAD_SEXPR::saveJunction", { "at", "color", "diameter", "junction", "locked" } },
        { "SCH_IO_KICAD_SEXPR::saveLine", { "locked", "pts", "xy" } },
        { "SCH_IO_KICAD_SEXPR::saveNoConnect", { "at", "locked", "no_connect" } },
        { "SCH_IO_KICAD_SEXPR::saveRuleArea",
          { "dnp", "exclude_from_sim", "in_bom", "locked", "on_board", "rule_area" } },
        { "SCH_IO_KICAD_SEXPR::saveSheet",
          { "at", "color", "dnp", "exclude_from_sim", "field", "fields_autoplaced", "fill", "in_bom",
            "instances", "locked", "name", "on_board", "page", "path", "pin", "project", "sheet", "size",
            "value", "variant" } },
        { "SCH_IO_KICAD_SEXPR::saveSymbol",
          { "alternate", "at", "body_style", "dnp", "exclude_from_sim", "field", "fields_autoplaced", "in_bom",
            "in_pos_files", "instances", "lib_id", "lib_name", "locked", "mirror", "name", "on_board",
            "passthrough", "path", "pin", "project", "reference", "symbol", "symbol_override", "unit", "value",
            "variant" } },
        { "SCH_IO_KICAD_SEXPR::saveTable",
          { "border", "cells", "cols", "column_count", "column_widths", "external", "header", "locked",
            "row_heights", "rows", "separators", "table" } },
        { "SCH_IO_KICAD_SEXPR::saveText",
          { "at", "exclude_from_sim", "fields_autoplaced", "length", "locked", "shape" } },
        { "SCH_IO_KICAD_SEXPR::saveTextBox",
          { "at", "exclude_from_sim", "locked", "margins", "size", "span" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::SaveSymbol",
          { "body_styles", "duplicate_pin_numbers_are_jumpers", "embedded_fonts", "exclude_from_sim",
            "extends", "hide", "in_bom", "in_pos_files", "jumper_pin_groups", "lib_id", "offset", "on_board",
            "pin_names", "pin_numbers", "power", "symbol", "unit_name" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveField",
          { "at", "do_not_autoplace", "hide", "property", "show_name" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePin",
          { "alternate", "at", "effects", "font", "hide", "length", "name", "number", "pin", "size" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::savePinMapData",
          { "associated_footprints", "entry", "footprint", "map", "pin_map", "pin_maps" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveText", { "at", "text" } },
        { "SCH_IO_KICAD_SEXPR_LIB_CACHE::saveTextBox", { "at", "margins", "size", "text_box" } },
        { "STROKE_PARAMS::Format", { "color", "stroke", "type", "width" } },
        { "TITLE_BLOCK::Format", { "comment", "company", "date", "rev", "title", "title_block" } },
        { "formatArc", { "arc", "end", "locked", "mid", "start", "uuid" } },
        { "formatBezier", { "bezier", "locked", "pts", "uuid", "xy" } },
        { "formatCircle", { "center", "circle", "locked", "radius", "uuid" } },
        { "formatEllipse",
          { "center", "ellipse", "locked", "major_radius", "minor_radius", "rotation_angle", "uuid" } },
        { "formatEllipseArc",
          { "center", "ellipse_arc", "end_angle", "locked", "major_radius", "minor_radius", "rotation_angle",
            "start_angle", "uuid" } },
        { "formatFill", { "color", "fill", "type" } },
        { "formatPinMapOverride", { "edit", "map", "mode", "pin_map_override" } },
        { "formatPoly", { "locked", "polyline", "pts", "uuid", "xy" } },
        { "formatRect", { "end", "locked", "radius", "rectangle", "start", "uuid" } },
    };
}


struct ORACLE_FIXTURE
{
    ORACLE_FIXTURE() : root( findSourceRoot() ) {}

    const SOURCE_SCAN& Scan( const std::string& aRelative )
    {
        auto it = scans.find( aRelative );

        if( it == scans.end() )
        {
            std::string text;
            readFile( root / aRelative, text );
            it = scans.emplace( aRelative, scanSource( text, watched ) ).first;
        }

        return it->second;
    }

    bool Evidence( const EVIDENCE& aEvidence, std::string& aFailure )
    {
        const SOURCE_SCAN& scan = Scan( aEvidence.file );

        if( !scan.HasFunction( aEvidence.function ) )
        {
            aFailure = aEvidence.file + ": " + aEvidence.function + " no longer exists";
            return false;
        }

        bool found = aEvidence.needle == ANY_ROUTE ? scan.FunctionRoutes( aEvidence.function )
                                                : scan.FunctionContains( aEvidence.function, aEvidence.needle );

        if( !found )
        {
            aFailure = aEvidence.file + ": " + aEvidence.function + " lacks "
                       + ( aEvidence.needle == ANY_ROUTE ? std::string( "a commit push or recorded change" )
                                                      : "'" + aEvidence.needle + "'" );
        }

        return found;
    }

    /// Every source file of the schematic editor and the shared code it is built from.
    std::vector<std::string> OwnerFiles()
    {
        std::vector<std::string> files;

        for( const char* rootDirectory : { "eeschema", "common", "include" } )
        {
            for( const fs::path& file : sourceFiles( root, rootDirectory ) )
                files.push_back( relativeName( root, file ) );
        }

        return files;
    }

    fs::path                           root;
    std::set<std::string>              watched{ "OnModify", "AnnotateSymbols", "resyncAfterTopLevelSheetChange",
                                                "onModified", "FIELDS_GRID_TABLE" };
    std::map<std::string, SOURCE_SCAN> scans;
};


inline std::string ownerKey( const std::string& aFile, const std::string& aFunction, const std::string& aReceiver )
{
    return aFile + " | " + aFunction + " | " + ( aReceiver.empty() ? "this" : aReceiver );
}


inline bool changesState( DISPOSITION aDisposition )
{
    return aDisposition == DISPOSITION::ROUTED || aDisposition == DISPOSITION::ROUTED_UNLESS_RECORDED
           || aDisposition == DISPOSITION::ROUTED_BY_CALLERS || aDisposition == DISPOSITION::PENDING;
}

/// Every setter that reports whether native change tracking is complete.
inline std::set<std::string> completeTrackingSetters()
{
    return { "set_tracking_complete", "set_complete_change_tracking" };
}


// The test suites live in this namespace, not behind a using-directive: in the eeschema-linked
// builds the editor's own headers are included as well, and a global name there must never make
// an oracle name ambiguous.

BOOST_AUTO_TEST_SUITE( SchChangeTracking )


BOOST_AUTO_TEST_CASE( ScannerSeparatesRoutedDraftAndUnreviewedOwners )
{
    // Recall and precision of the scanner itself, on sources that exercise each rule.
    const std::string source = R"src(
// OnModify( in a comment is not a call.
void FRAME::Routed()
{
    SCH_COMMIT commit( this );
    commit.Modify( item );

    if( !commit.Empty() )
        commit.Push( "Edit" );

    m_frame->OnModify();
}

void FRAME::SiblingOnly( bool a )
{
    if( a )
    {
        SCH_COMMIT commit( this );
        commit.Push( "Edit" );
    }
    else
    {
        m_frame->OnModify();
    }
}

void FRAME::ChildBlockOnly( bool a )
{
    SCH_COMMIT commit( this );

    if( a )
    {
        commit.Push( "Edit" );
    }

    m_frame->OnModify();
}

void FRAME::RouteAfterCall()
{
    m_frame->OnModify();
    SCH_TRACKED_CHANGE change( Schematic(), "Too late" );
    Schematic().RecordCommittedChange( KIND::COMMIT, "Too late" );
}

void FRAME::Tracked()
{
    SCH_TRACKED_CHANGE change( Schematic(), "Direct edit" );
    directEdit();

    if( change.Complete() )
        OnModify();
}

void FRAME::MarkOnly()
{
    const SCH_TRACKED_CHANGE::MARK started = SCH_TRACKED_CHANGE::Mark( Schematic() );
    directEdit();
    OnModify();
}

void FRAME::ScreenTracked( SCH_SCREEN* aScreen, const SCH_TRACKED_CHANGE::MARK& aSince )
{
    SCH_TRACKED_CHANGE placement( Schematic(), "Place", { aScreen }, aSince );

    if( placement.Complete() )
        OnModify();
}

void FRAME::Received( SCH_TRACKED_CHANGE& aChange )
{
    OnModify();
}

void FRAME::PartsTracked()
{
    SCH_TRACKED_CHANGE parts( Schematic(), "Page", SCH_PERSISTED_PARTS::PageSettings() );

    if( parts.Complete() )
        OnModify();
}

void FRAME::StagedTracker( SCH_COMMIT* aOuter )
{
    SCH_TRACKED_CHANGE early( Schematic(), "Declared before its commit", late );
    SCH_COMMIT late( this );
    SCH_TRACKED_CHANGE staged( Schematic(), "Staged edit", late );
    staged.PushOrRevert( late, "Staged edit" );
}

int FRAME::Unrouted()
{
    wxString text = "m_frame->OnModify(); SCH_TRACKED_CHANGE";
    m_frame->OnModify();
    return 0;
}

void FRAME::LambdaOnly()
{
    SCH_COMMIT& c = *provided;
    auto lambda = [&]() { c.Push( "Lambda" ); };
    lambda();
    parent.OnModify();
}

void FRAME::Staged()
{
    SCH_COMMIT commit( this );
    SCH_COMMIT other( this );
    AnnotateSymbols( &commit, 1 );
    AnnotateSymbols( &other, 2 );
    commit.Push( "Annotate" );
}

class LOCAL_TRICKS : public GRID_TRICKS
{
    void doPopup() { m_dialog->OnModify(); }
};

#define LABEL _HKI( "Label" )
#define MULTI_LINE( x ) \
    x->OnModify()

class DECLARES : public BASE
{
    void OnModify() override;
};

void WRITER::save()
{
    m_out->Print( "(sheet (at %s) (size %s)" );
    KICAD_FORMAT::FormatBool( m_out, "in_bom", true );
    KICAD_FORMAT::FormatBool( &aFormatter, "hide", true );
    wxLogTrace( "(not_a_field", "plain" );
}
)src";

    SOURCE_SCAN scan = scanSource( source, { "OnModify", "AnnotateSymbols" } );
    std::map<std::string, const CALL_SITE*> calls;
    std::vector<const CALL_SITE*>           staged;

    for( const CALL_SITE& call : scan.calls )
    {
        if( call.identifier == "OnModify" )
            calls[call.function] = &call;
        else
            staged.push_back( &call );
    }

    BOOST_REQUIRE_EQUAL( calls.size(), 12u );
    BOOST_REQUIRE_EQUAL( staged.size(), 2u );

    for( const char* routed : { "FRAME::Routed", "FRAME::Tracked", "FRAME::ScreenTracked", "FRAME::Received",
                                "FRAME::PartsTracked" } )
        BOOST_CHECK_MESSAGE( calls.count( routed ) && scan.Routed( *calls[routed] ), routed );

    // A push in a sibling branch, only in a nested block or lambda, or after the call does not
    // route it; neither do route names inside a string or a journal mark.
    for( const char* unrouted : { "FRAME::SiblingOnly", "FRAME::ChildBlockOnly", "FRAME::RouteAfterCall",
                                  "FRAME::MarkOnly", "FRAME::Unrouted", "FRAME::LambdaOnly",
                                  "LOCAL_TRICKS::doPopup" } )
    {
        BOOST_CHECK_MESSAGE( calls.count( unrouted ) && !scan.Routed( *calls[unrouted] ), unrouted );
    }

    // A staged helper call is routed only by a later push of the commit it was given.
    BOOST_CHECK( scan.PushedAfter( *staged[0] ) );
    BOOST_CHECK( !scan.PushedAfter( *staged[1] ) );

    BOOST_CHECK_EQUAL( calls["FRAME::Routed"]->receiver, "m_frame->" );
    BOOST_CHECK_EQUAL( calls["FRAME::Tracked"]->receiver, "" );
    BOOST_CHECK_EQUAL( calls["FRAME::LambdaOnly"]->receiver, "parent." );
    BOOST_CHECK_EQUAL( calls["LOCAL_TRICKS::doPopup"]->receiver, "m_dialog->" );

    // Declared trackers and their constructor arguments; a journal mark is not a tracker.
    std::map<std::string, int> trackers;

    for( const TRACKER& tracker : scan.trackers )
        trackers[tracker.function + " " + tracker.variable] = tracker.arguments;

    BOOST_CHECK_EQUAL( trackers.size(), 6u );
    BOOST_CHECK_EQUAL( trackers["FRAME::Tracked change"], 2 );
    BOOST_CHECK_EQUAL( trackers["FRAME::ScreenTracked placement"], 4 );
    BOOST_CHECK_EQUAL( trackers["FRAME::RouteAfterCall change"], 2 );
    BOOST_CHECK_EQUAL( trackers["FRAME::StagedTracker staged"], 3 );
    BOOST_CHECK_EQUAL( trackers["FRAME::StagedTracker early"], 3 );
    BOOST_CHECK_EQUAL( trackers["FRAME::PartsTracked parts"], 3 );

    // A staged tracker must compare a commit declared before it, which therefore outlives it; a
    // three-argument tracker naming persisted parts is the parts form and compares no commit.
    for( const TRACKER& tracker : scan.trackers )
    {
        BOOST_CHECK_MESSAGE( tracker.parts == ( tracker.function == "FRAME::PartsTracked" ), tracker.variable );

        if( tracker.function != "FRAME::StagedTracker" )
            continue;

        BOOST_CHECK_EQUAL( tracker.commit, "late" );
        BOOST_CHECK_MESSAGE( tracker.commitDeclared == ( tracker.variable == "staged" ), tracker.variable );
    }

    std::map<std::string, std::set<std::string>> fields = writerFields( scan );
    std::set<std::string> expected{ "sheet", "at", "size", "in_bom", "hide", "not_a_field" };
    BOOST_CHECK( fields["WRITER::save"] == expected );

    // An owner the review has not seen is reported by its exact identity.
    BOOST_CHECK_EQUAL( ownerKey( "eeschema/x.cpp", calls["FRAME::Unrouted"]->function,
                                 calls["FRAME::Unrouted"]->receiver ),
                       "eeschema/x.cpp | FRAME::Unrouted | m_frame->" );
}


BOOST_AUTO_TEST_CASE( EveryDirectModifyOwnerIsReviewedAndRouted )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE_MESSAGE( !oracle.root.empty(),
                           "The KiCad source tree was not found; set KICAD_SOURCE_DIR to run the oracle." );

    std::map<std::string, int>              found;
    std::map<std::string, std::vector<int>> unroutedLines;
    size_t                                  totalCalls = 0;
    size_t                                  commonCalls = 0;

    for( const std::string& relative : oracle.OwnerFiles() )
    {
        const SOURCE_SCAN& scan = oracle.Scan( relative );

        for( const CALL_SITE& call : scan.calls )
        {
            if( call.identifier != "OnModify" )
                continue;

            ++totalCalls;
            commonCalls += startsWith( relative, "common/" ) ? 1 : 0;
            std::string key = ownerKey( relative, call.function, call.receiver );
            ++found[key];

            if( !scan.Routed( call ) )
                unroutedLines[key].push_back( call.line );
        }
    }

    // Guard against a vacuous pass on a wrong or partial tree.
    BOOST_REQUIRE_MESSAGE( totalCalls >= 100 && commonCalls >= 10,
                           "Only " + std::to_string( totalCalls ) + " OnModify calls (" + std::to_string( commonCalls )
                                   + " in common/) were found under " + oracle.root.string() );

    std::map<std::string, OWNER> reviewed;

    for( const OWNER& owner : reviewedOwners() )
    {
        std::string key = ownerKey( owner.file, owner.function, owner.receiver );
        BOOST_CHECK_MESSAGE( reviewed.emplace( key, owner ).second, "Duplicate review entry: " + key );
    }

    for( const auto& [key, count] : found )
    {
        auto it = reviewed.find( key );

        if( it == reviewed.end() )
        {
            BOOST_ERROR( "Unreviewed native mutation owner calls OnModify " + std::to_string( count )
                         + " time(s): " + key
                         + ". Route it through SCH_COMMIT, RecordCommittedChange or SCH_TRACKED_CHANGE, "
                           "or review it into the allow-list with its reason." );
            continue;
        }

        const OWNER& owner = it->second;

        BOOST_CHECK_MESSAGE( owner.calls == count,
                             key + " now calls OnModify " + std::to_string( count ) + " time(s); the review covers "
                                     + std::to_string( owner.calls ) + "." );

        if( owner.disposition == DISPOSITION::ROUTED )
        {
            std::string lines;

            for( int line : unroutedLines[key] )
                lines += ( lines.empty() ? "" : ", " ) + std::to_string( line );

            BOOST_CHECK_MESSAGE( unroutedLines[key].empty(),
                                 key + " marks the document modified without a commit push, recorded change or "
                                       "tracked change before it in scope at line(s) " + lines + "." );
        }

        if( owner.disposition == DISPOSITION::ROUTED_UNLESS_RECORDED )
            BOOST_CHECK_MESSAGE( !owner.evidence.empty(), key + " needs evidence of its recording." );

        if( owner.disposition == DISPOSITION::PENDING )
            BOOST_CHECK_MESSAGE( owner.estimateLines > 0, key + " is pending without an estimate." );

        for( const EVIDENCE& evidence : owner.evidence )
        {
            std::string failure;
            BOOST_CHECK_MESSAGE( oracle.Evidence( evidence, failure ), key + ": " + failure );
        }
    }

    for( const auto& [key, owner] : reviewed )
    {
        BOOST_CHECK_MESSAGE( found.count( key ),
                             "Stale review entry, no OnModify call remains: " + key
                                     + ". Remove it so the allow-list stays exact." );
    }
}


BOOST_AUTO_TEST_CASE( EveryHelperCallIsRouted )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    for( const HELPER& helper : reviewedHelpers() )
    {
        std::map<std::string, const HELPER_CALLER*> expected;

        for( const HELPER_CALLER& caller : helper.callers )
            expected[caller.file + " | " + caller.function] = &caller;

        std::map<std::string, int> actual;

        for( const std::string& root : helper.roots )
        {
            for( const fs::path& file : sourceFiles( oracle.root, root ) )
            {
                const std::string  relative = relativeName( oracle.root, file );
                const SOURCE_SCAN& scan = oracle.Scan( relative );

                for( const CALL_SITE& call : scan.calls )
                {
                    // A qualified call names a definition in another class or the base class.
                    if( call.identifier != helper.identifier || call.receiver.find( "::" ) != std::string::npos )
                        continue;

                    const std::string key = relative + " | " + call.function;
                    ++actual[key];

                    auto it = expected.find( key );

                    if( it == expected.end() )
                    {
                        BOOST_ERROR( "Unreviewed caller of " + helper.identifier + ": " + key + " line "
                                     + std::to_string( call.line ) + ". " + helper.reason );
                        continue;
                    }

                    // Every call site is checked, not just its function.
                    const std::string where = key + " line " + std::to_string( call.line );

                    if( it->second->rule == CALLER_RULE::ROUTED_CALL )
                    {
                        BOOST_CHECK_MESSAGE( scan.Routed( call ),
                                             where + " calls " + helper.identifier
                                                     + " without a commit push, recorded change or tracked change "
                                                       "before it in scope." );
                    }
                    else if( it->second->rule == CALLER_RULE::STAGED_COMMIT )
                    {
                        BOOST_CHECK_MESSAGE( scan.PushedAfter( call ),
                                             where + " stages " + helper.identifier
                                                     + " into a commit that is not pushed after the call." );
                    }
                }
            }
        }

        for( const auto& [key, caller] : expected )
        {
            BOOST_CHECK_MESSAGE( actual[key] == caller->calls,
                                 key + " calls " + helper.identifier + " " + std::to_string( actual[key] )
                                         + " time(s); the review covers " + std::to_string( caller->calls ) + "." );

            BOOST_CHECK_MESSAGE( caller->rule != CALLER_RULE::EVIDENCE || !caller->evidence.empty(),
                                 key + " is reviewed by evidence but names none." );

            for( const EVIDENCE& evidence : caller->evidence )
            {
                std::string failure;
                BOOST_CHECK_MESSAGE( oracle.Evidence( evidence, failure ),
                                     helper.identifier + " via " + caller->function + ": " + failure );
            }
        }
    }
}


BOOST_AUTO_TEST_CASE( OnlyAReportingCallerRepairsIdentities )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    std::map<std::string, int> reporting;

    for( const EVIDENCE& evidence : identityRepairCallers() )
    {
        reporting[evidence.file + " | " + evidence.function] = 0;

        std::string failure;
        BOOST_CHECK_MESSAGE( oracle.Evidence( evidence, failure ), "AnnotateSymbols repair: " + failure );
    }

    size_t calls = 0;

    for( const fs::path& file : sourceFiles( oracle.root, "eeschema" ) )
    {
        const std::string relative = relativeName( oracle.root, file );

        for( const CALL_SITE& call : oracle.Scan( relative ).calls )
        {
            // A qualified name is the definition, not a call.
            if( call.identifier != "AnnotateSymbols" || call.receiver.find( "::" ) != std::string::npos )
                continue;

            ++calls;

            const std::string key = relative + " | " + call.function;
            const std::string where = key + " line " + std::to_string( call.line );
            const std::string repair = identityRepairArgument( call );

            BOOST_CHECK_MESSAGE( !repair.empty(), where + " calls AnnotateSymbols with arguments the review does not "
                                                          "know: " + call.arguments );

            if( auto it = reporting.find( key ); it != reporting.end() )
            {
                ++it->second;
                continue;
            }

            BOOST_CHECK_MESSAGE( repair == "false", where + " asks AnnotateSymbols to repair identities (" + repair
                                                            + ") without reporting the replaced count as a change "
                                                              "outside its commit." );
        }
    }

    BOOST_CHECK_GT( calls, 0u );

    for( const auto& [key, count] : reporting )
        BOOST_CHECK_MESSAGE( count == 1, key + " calls AnnotateSymbols " + std::to_string( count ) + " time(s); the "
                                                                                                     "review covers 1." );

    // Recall and precision of the argument reading: comments are not arguments, a cast or a
    // nested call keeps its own commas, a repairing call is read as such, and a call with
    // another argument list is not read at all.
    const std::string probe = R"src(
void TOOL::Placed()
{
    m_frame->AnnotateSymbols( &commit, ANNOTATE_SELECTION, (ANNOTATE_ORDER_T) settings.m_AnnotateSortOrder,
                              (ANNOTATE_ALGO_T) settings.m_AnnotateMethod, true /* recursive */,
                              settings.m_AnnotateStartNum, true /* reset */, false,
                              false /* repair, true */, reporter, SYMBOL_FILTER_NON_POWER );
}

void TOOL::Repairing()
{
    m_frame->AnnotateSymbols( &commit, ANNOTATE_ALL, order( a, b ), algo, false, 0, false, false,
                              // false
                              true, reporter, SYMBOL_FILTER_ALL );
}

void TOOL::Shortened()
{
    AnnotateSymbols( &commit, 1 );
}
)src";
    const SOURCE_SCAN probed = scanSource( probe, { "AnnotateSymbols" } );

    BOOST_REQUIRE_EQUAL( probed.calls.size(), 3u );
    BOOST_CHECK_EQUAL( identityRepairArgument( probed.calls[0] ), "false" );
    BOOST_CHECK_EQUAL( identityRepairArgument( probed.calls[1] ), "true" );
    BOOST_CHECK_EQUAL( identityRepairArgument( probed.calls[2] ), "" );
}


BOOST_AUTO_TEST_CASE( EveryTrackedChangeIsReviewed )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    // Whole-state, staged, part-restricted and screen-restricted declarations per function.
    std::map<std::string, std::array<int, 4>> found;

    for( const std::string& relative : oracle.OwnerFiles() )
    {
        for( const TRACKER& tracker : oracle.Scan( relative ).trackers )
        {
            std::array<int, 4>& counts = found[relative + " | " + tracker.function];
            const std::string   where = relative + " line " + std::to_string( tracker.line );

            BOOST_CHECK_MESSAGE( tracker.arguments >= 2 && tracker.arguments <= 4,
                                 where + " constructs a tracker with an unreviewed argument list." );

            // The staged form compares its commit when it completes, so the commit must be a
            // SCH_COMMIT declared before the tracker and destroyed after it.
            BOOST_CHECK_MESSAGE( tracker.arguments != 3 || tracker.parts || tracker.commitDeclared,
                                 where + " stages its tracker on '" + tracker.commit
                                         + "', which is not a SCH_COMMIT declared before it." );

            if( tracker.arguments == 2 )
                counts[0]++;
            else if( tracker.arguments == 3 )
                counts[tracker.parts ? 2 : 1]++;
            else if( tracker.arguments == 4 )
                counts[3]++;
        }
    }

    std::set<std::string> reviewed;

    for( const TRACKER_SITE& site : reviewedTrackers() )
    {
        const std::string        key = site.file + " | " + site.function;
        const std::array<int, 4> expected{ site.wholeState, site.staged, site.parts, site.screens };
        reviewed.insert( key );

        BOOST_CHECK_MESSAGE( found[key] == expected,
                             key + " declares " + std::to_string( found[key][0] ) + " whole-state, "
                                     + std::to_string( found[key][1] ) + " staged, "
                                     + std::to_string( found[key][2] ) + " part-restricted and "
                                     + std::to_string( found[key][3] ) + " screen-restricted tracker(s); the "
                                     "review covers " + std::to_string( site.wholeState ) + ", "
                                     + std::to_string( site.staged ) + ", " + std::to_string( site.parts )
                                     + " and " + std::to_string( site.screens ) + "." );

        BOOST_CHECK_MESSAGE( ( site.screens == 0 && site.staged == 0 && site.parts == 0 ) || !site.reason.empty(),
                             key + " stages or restricts a tracker without a reason." );

        for( const EVIDENCE& evidence : site.evidence )
        {
            std::string failure;
            BOOST_CHECK_MESSAGE( oracle.Evidence( evidence, failure ),
                                 key + ": the reviewed reason no longer holds: " + failure );
        }
    }

    for( const auto& [key, counts] : found )
    {
        BOOST_CHECK_MESSAGE( reviewed.count( key ),
                             "Unreviewed SCH_TRACKED_CHANGE in " + key + ": review what it compares." );
    }
}


BOOST_AUTO_TEST_CASE( PinnedWriterFieldsAreCrossedWithOwners )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    std::map<std::string, std::set<std::string>> actual;

    for( const WRITER& writer : schematicWriters() )
    {
        const SOURCE_SCAN& scan = oracle.Scan( writer.file );

        for( const std::string& function : writer.functions )
            BOOST_CHECK_MESSAGE( scan.HasFunction( function ), writer.file + ": writer " + function + " is gone." );

        for( auto& [group, tokens] : writerFields( scan ) )
        {
            if( writer.functions.empty()
                    || std::find( writer.functions.begin(), writer.functions.end(), group ) != writer.functions.end() )
            {
                actual[group].insert( tokens.begin(), tokens.end() );
            }
        }
    }

    std::map<std::string, std::set<std::string>> pinned = pinnedWriterFields();

    for( const auto& [group, tokens] : actual )
    {
        for( const std::string& token : tokens )
        {
            BOOST_CHECK_MESSAGE( pinned[group].count( token ),
                                 "The schematic writer persists a new field " + group + " \"(" + token
                                         + "\"; review which native owners change it and pin it." );
        }
    }

    for( const auto& [group, tokens] : pinned )
    {
        for( const std::string& token : tokens )
        {
            BOOST_CHECK_MESSAGE( actual[group].count( token ),
                                 "Pinned writer field " + group + " \"(" + token + "\" is no longer written." );
        }
    }

    // Cross the writer groups with their owners: every pinned group must be changed only by the
    // commit machinery or by reviewed, routed owners, and no owner may name an unknown group.
    std::map<std::string, std::vector<std::string>> routedByGroup;
    std::map<std::string, std::vector<std::string>> pendingByGroup;
    const std::set<std::string>                     commitCovered = commitCoveredGroups();

    for( const std::string& group : commitCovered )
    {
        BOOST_CHECK_MESSAGE( pinned.count( group ), "Commit-covered group " + group + " is not a pinned writer." );
    }

    for( const OWNER& owner : reviewedOwners() )
    {
        BOOST_CHECK_MESSAGE( !changesState( owner.disposition ) || !owner.groups.empty(),
                             owner.function + " changes persisted state but names no writer group." );

        for( const std::string& group : owner.groups )
        {
            BOOST_CHECK_MESSAGE( group == G_PROJECT || pinned.count( group ),
                                 owner.function + " names unknown writer group " + group );

            if( owner.disposition == DISPOSITION::PENDING )
                pendingByGroup[group].push_back( owner.function );
            else if( changesState( owner.disposition ) )
                routedByGroup[group].push_back( owner.function );
        }
    }

    for( const auto& [group, tokens] : pinned )
    {
        BOOST_CHECK_MESSAGE( commitCovered.count( group ) || !routedByGroup[group].empty(),
                             "Writer group " + group + " has no owner: neither the commit machinery nor a "
                             "reviewed direct owner changes it." );

        BOOST_TEST_MESSAGE( group << ": " << tokens.size() << " pinned fields; "
                                  << ( commitCovered.count( group ) ? "SCH_COMMIT plus " : "" )
                                  << routedByGroup[group].size() << " routed direct owner(s), "
                                  << pendingByGroup[group].size() << " pending." );
    }

    BOOST_CHECK_MESSAGE( !routedByGroup[G_PROJECT].empty(), "No owner changes the project settings group." );
    BOOST_TEST_MESSAGE( G_PROJECT << ": " << routedByGroup[G_PROJECT].size() << " routed direct owner(s), "
                                << pendingByGroup[G_PROJECT].size() << " pending." );
}


BOOST_AUTO_TEST_CASE( TrackingStaysIncompleteWhileOwnersArePending )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    int    estimate = 0;
    size_t pending = 0;

    for( const OWNER& owner : reviewedOwners() )
    {
        if( owner.disposition == DISPOSITION::PENDING )
        {
            ++pending;
            estimate += owner.estimateLines;
            BOOST_TEST_MESSAGE( "Pending owner " << owner.file << " " << owner.function << " (~"
                                                 << owner.estimateLines << " lines): " << owner.reason );
        }
    }

    for( const UNPROVEN& route : unprovenRoutes() )
    {
        std::string failure;
        BOOST_CHECK_MESSAGE( oracle.Evidence( { route.file, route.function, route.needle }, failure ),
                             "Unproven route " + route.function + ": " + failure );
        BOOST_CHECK_MESSAGE( route.estimateLines > 0, route.function + " has no estimate." );

        ++pending;
        estimate += route.estimateLines;
        BOOST_TEST_MESSAGE( "Routed but not proven end to end " << route.file << " " << route.function << " (~"
                                                                << route.estimateLines << " lines): "
                                                                << route.reason );
    }

    BOOST_TEST_MESSAGE( pending << " owner(s) pending or unproven, about " << estimate << " lines." );

    // The journal, its notifications and the lifecycle state may claim complete tracking only
    // once nothing is pending.  Every claim in every scanned source, api_handler_sch_render.cpp
    // and any new file included, must be the literal false until then; each setter must still
    // be called, and every call must sit where the scan reviews it.
    ORACLE_FIXTURE claims;
    claims.watched = completeTrackingSetters();

    std::map<std::string, size_t> reviewedCalls;
    std::map<std::string, size_t> textualCalls;

    for( const std::string& relative : claims.OwnerFiles() )
    {
        const SOURCE_SCAN& scan = claims.Scan( relative );

        for( const std::string& setter : claims.watched )
            textualCalls[setter] += countCalls( scan.code, setter );

        for( const CALL_SITE& call : scan.calls )
        {
            ++reviewedCalls[call.identifier];

            BOOST_CHECK_MESSAGE( pending == 0 || call.arguments == "false",
                                 relative + " line " + std::to_string( call.line ) + " claims complete tracking ("
                                         + call.arguments + ") while " + std::to_string( pending )
                                         + " owner(s) are pending or unproven." );
        }
    }

    for( const std::string& setter : claims.watched )
    {
        BOOST_CHECK_MESSAGE( reviewedCalls[setter] > 0, "No scanned source reports " + setter + "() any more." );
        BOOST_CHECK_MESSAGE( reviewedCalls[setter] == textualCalls[setter],
                             setter + " is called " + std::to_string( textualCalls[setter] ) + " time(s), but only "
                                     + std::to_string( reviewedCalls[setter] )
                                     + " inside a function body where the claim is checked." );
    }

    // Recall and precision of the claim scan: a commented or quoted claim is no claim, a split
    // argument list is read whole, and a claim outside a function body is still counted.
    const std::string probe = "void API::Report( RESULT& result )\n"
                              "{\n"
                              "    // result.set_tracking_complete( true );\n"
                              "    wxLogTrace( \"set_tracking_complete( true )\" );\n"
                              "    result.set_tracking_complete(\n"
                              "            false );\n"
                              "    other->set_complete_change_tracking( complete );\n"
                              "}\n"
                              "static const bool claimed = []{ RESULT r; r.set_tracking_complete( true ); return true; }();\n";
    const SOURCE_SCAN probed = scanSource( probe, completeTrackingSetters() );

    BOOST_REQUIRE_EQUAL( probed.calls.size(), 2u );
    BOOST_CHECK_EQUAL( probed.calls[0].arguments, "false" );
    BOOST_CHECK_EQUAL( probed.calls[1].arguments, "complete" );
    BOOST_CHECK_EQUAL( countCalls( probed.code, "set_tracking_complete" ), 2u );
    BOOST_CHECK_EQUAL( countCalls( probed.code, "set_complete_change_tracking" ), 1u );
}


BOOST_AUTO_TEST_CASE( EveryProvenOwnerKeepsItsRenderedSteps )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    std::string journey;
    BOOST_REQUIRE_MESSAGE( readFile( oracle.root / JOURNEY, journey ), JOURNEY + " is missing." );

    const JOURNEY_STEPS steps = journeySteps( journey, JOURNEY_METHOD );

    for( const std::string& failure : steps.failures )
        BOOST_ERROR( JOURNEY + ": " + failure );

    for( const PROOF& proof : journeyProofs() )
    {
        // The proof is about the routed owner: it must still exist and route its change.
        std::string failure;
        BOOST_CHECK_MESSAGE( oracle.Evidence( { proof.file, proof.function, ANY_ROUTE }, failure ),
                             "Proven owner " + failure );

        auto where = [&]( const std::string& aLiteral )
        {
            return steps.conditional.count( aLiteral )
                           ? std::string( " (it is called only where it may not run: a branch, loop, catch, "
                                          "local function, lambda or conditional expression, or after an early "
                                          "return)" )
                           : std::string();
        };

        BOOST_CHECK_MESSAGE( steps.changes.count( proof.description ),
                             proof.function + " is no longer proven: " + JOURNEY_METHOD
                                     + " has no unconditional awaited OneChange(..., \"" + proof.description
                                     + "\", ...) step" + where( proof.description ) + "." );

        for( const std::string& step : proof.unchanged )
        {
            BOOST_CHECK_MESSAGE( steps.unchanged.count( step ),
                                 proof.function + " is no longer proven: " + JOURNEY_METHOD
                                         + " has no unconditional awaited Unchanged(..., \"" + step + "\") step"
                                         + where( step ) + "." );
        }

        for( const std::string& step : proof.undone )
        {
            BOOST_CHECK_MESSAGE( steps.undone.count( step ),
                                 proof.function + " is no longer proven: " + JOURNEY_METHOD
                                         + " has no unconditional awaited Undone(..., \"" + step + "\", ...) step"
                                         + where( step ) + "." );
        }
    }

    // Recall and precision of the journey scan: steps named in comments, strings, raw strings
    // or other methods do not count, steps the method may not run do not count, and helpers
    // must keep their assertions.
    const std::string probe = R"cs(
private static async Task VerifyDirectOwnerTracking(NativeClient client)
{
    async Task Unchanged(DocumentLifecycleState baseline, string step)
    {
        var after = await State();
        Assert.AreEqual(baseline.Revision, after.Revision, $"{step} must not create a revision.");
        Assert.AreEqual(baseline.StateSha256, after.StateSha256, "no");
        Assert.AreEqual(baseline.NativeContentDirty, after.NativeContentDirty, "no");
        Assert.IsEmpty((await Changes(baseline)).Changes, "no");
    }
    async Task<SchematicChange> OneChange(DocumentLifecycleState baseline, string description, SchematicChange.Types.Kind kind)
    {
        var journal = await Changes(baseline);
        Assert.IsFalse(journal.ResetRequired);
        Assert.HasCount(1, journal.Changes, $"'{description}' must be exactly one revision.");
        var change = journal.Changes.Single();
        Assert.AreEqual(kind, change.Kind);
        // Assert.AreEqual(description, change.Description);
        return change;
    }
    async Task<DocumentLifecycleState> Undone(DocumentLifecycleState after, string step, string slug)
    {
        var undone = await Advanced(after, slug);
        var journal = await Changes(after);
        Assert.IsFalse(journal.ResetRequired);
        Assert.HasCount(1, journal.Changes, $"{step} must be exactly one revision.");
        Assert.AreEqual(SchematicChange.Types.Kind.Undo, journal.Changes.Single().Kind);
        return undone;
    }
    // await OneChange(clean, "Commented", SchematicChange.Types.Kind.Commit);
    // await Undone(clean, "Commented undo", "slug");
    var undone = await Undone(clean, "Real undo", "real-undo");
    if (clean.NativeContentDirty) await Undone(clean, "Undo inside a branch", "branch-undo");
    var text = "await OneChange(clean, "Quoted", kind)";
    var raw = """
        await Unchanged(clean, "Raw");
        """;
    var verbatim = @"await Unchanged(clean, ""Verbatim"")";
    await OneChange(clean, "Real change", SchematicChange.Types.Kind.Commit);
    await Unchanged(clean, "Real cancel");
    await Unchanged(clean, $"Interpolated {text}");
    var assigned = await OneChange(clean, "Assigned change", SchematicChange.Types.Kind.Commit);
    try { await Unchanged(clean, "Inside a try"); } finally { Cleanup(); }
    using (var scope = Scope()) { await Unchanged(clean, "Inside a using"); }
    if (clean.NativeContentDirty) { await Unchanged(clean, "Inside a branch"); }
    if (clean.NativeContentDirty) await Unchanged(clean, "Braceless branch");
    else await Unchanged(clean, "Braceless else");
    foreach (var item in items) { await OneChange(clean, "Inside a loop", kind); }
    try { await Unchanged(clean, "Swallowed"); } catch (Exception) { }
    async Task Local() { await Unchanged(clean, "Inside a local function"); }
    await Assert.ThrowsAsync<Exception>(async () => await Unchanged(clean, "Inside a lambda"));
    var either = flag ? await OneChange(clean, "Inside a conditional expression", kind) : null;
    async Task<int> Helper() { return 1; }
    await Unchanged(clean, "After a local function's return");
    if (flag) return;
    await Unchanged(clean, "After an early return");
    await OneChange(clean, "Also after an early return", kind);
}

private static async Task Elsewhere() { await OneChange(clean, "Elsewhere", kind); }
)cs";
    const JOURNEY_STEPS probed = journeySteps( probe, JOURNEY_METHOD );

    BOOST_CHECK( ( probed.changes == std::set<std::string>{ "Real change", "Assigned change" } ) );
    BOOST_CHECK( ( probed.undone == std::set<std::string>{ "Real undo" } ) );
    BOOST_CHECK( ( probed.unchanged
                   == std::set<std::string>{ "Real cancel", "Inside a try", "Inside a using",
                                             "After a local function's return" } ) );
    BOOST_CHECK( ( probed.conditional
                   == std::set<std::string>{ "Undo inside a branch", "Inside a branch", "Braceless branch",
                                             "Braceless else", "Inside a loop",
                                             "Swallowed", "Inside a local function", "Inside a lambda",
                                             "Inside a conditional expression", "After an early return",
                                             "Also after an early return" } ) );
    BOOST_REQUIRE_EQUAL( probed.failures.size(), 1u );
    BOOST_CHECK_MESSAGE( probed.failures[0].find( "Assert.AreEqual(description,change.Description)" ) != std::string::npos,
                         probed.failures[0] );
    BOOST_CHECK_EQUAL( journeySteps( probe, "Missing" ).failures.size(), 1u );
}


BOOST_AUTO_TEST_CASE( EveryErcExclusionChangeIsReviewed )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );
    oracle.watched = exclusionIdentifiers();

    std::map<std::string, int> found;

    for( const std::string& relative : oracle.OwnerFiles() )
    {
        if( !startsWith( relative, "eeschema/" ) )
            continue;

        for( const CALL_SITE& call : oracle.Scan( relative ).calls )
            ++found[relative + " | " + call.function + " | " + call.identifier];
    }

    // Guard against a vacuous pass on a wrong or partial tree.
    BOOST_REQUIRE_MESSAGE( found.size() >= 10, "Only " + std::to_string( found.size() )
                                                       + " ERC marker deletion or exclusion sites were found." );

    std::map<std::string, EXCLUSION_SITE> reviewed;

    for( const EXCLUSION_SITE& site : reviewedExclusionSites() )
    {
        const std::string key = site.file + " | " + site.function + " | " + site.identifier;
        BOOST_CHECK_MESSAGE( reviewed.emplace( key, site ).second, "Duplicate exclusion review entry: " + key );
    }

    for( const auto& [key, count] : found )
    {
        auto it = reviewed.find( key );

        if( it == reviewed.end() )
        {
            BOOST_ERROR( "Unreviewed ERC marker deletion or exclusion change (" + std::to_string( count )
                         + " call(s)): " + key
                         + ". An excluded marker is a saved exclusion: record the change with SCH_TRACKED_CHANGE, "
                           "RecordCommittedChange or a pushed SCH_COMMIT, or review why it keeps them." );
            continue;
        }

        const EXCLUSION_SITE& site = it->second;

        BOOST_CHECK_MESSAGE( site.calls == count, key + " now has " + std::to_string( count )
                                                          + " call(s); the review covers "
                                                          + std::to_string( site.calls ) + "." );
        BOOST_CHECK_MESSAGE( !site.reason.empty(), key + " is reviewed without a reason." );

        switch( site.rule )
        {
        case EXCLUSION_RULE::RECORDED:
            BOOST_CHECK_MESSAGE( oracle.Scan( site.file ).FunctionRoutes( site.function ),
                                 key + " deletes or changes a saved exclusion without recording the change." );
            break;

        case EXCLUSION_RULE::HELPER:
        {
            const size_t      separator = site.function.rfind( "::" );
            const std::string name = separator == std::string::npos ? site.function
                                                                    : site.function.substr( separator + 2 );

            BOOST_CHECK_MESSAGE( oracle.watched.count( name ),
                                 key + " is reviewed as a helper, but calls to " + name + " are not reviewed." );
            break;
        }

        case EXCLUSION_RULE::RERESOLVED:
        case EXCLUSION_RULE::STAGED:
        case EXCLUSION_RULE::PROVIDER:
            BOOST_CHECK_MESSAGE( !site.evidence.empty(), key + " needs evidence for its rule." );
            break;

        case EXCLUSION_RULE::RESOLUTION:
            break;
        }

        for( const EVIDENCE& evidence : site.evidence )
        {
            std::string failure;
            BOOST_CHECK_MESSAGE( oracle.Evidence( evidence, failure ), key + ": " + failure );
        }
    }

    for( const auto& [key, site] : reviewed )
    {
        BOOST_CHECK_MESSAGE( found.count( key ), "Stale exclusion review entry, the call is gone: " + key
                                                         + ". Remove it so the review stays exact." );
    }

    // Recall and precision of the scan itself: a deletion inside an unrouted function is found
    // with its function, and a comment or text mentioning one is not a call.
    const SOURCE_SCAN probe = scanSource( "void DIALOG_X::OnClear( wxCommandEvent& aEvent )\n"
                                          "{\n"
                                          "    // screens.DeleteAllMarkers( MARKER_BASE::MARKER_ERC, true );\n"
                                          "    wxLogDebug( \"DeleteMarkers( all )\" );\n"
                                          "    screens.DeleteAllMarkers( MARKER_BASE::MARKER_ERC, true );\n"
                                          "}\n",
                                          exclusionIdentifiers() );

    BOOST_REQUIRE_EQUAL( probe.calls.size(), 1u );
    BOOST_CHECK_EQUAL( probe.calls[0].function, "DIALOG_X::OnClear" );
    BOOST_CHECK_EQUAL( probe.calls[0].identifier, "DeleteAllMarkers" );
    BOOST_CHECK( !probe.FunctionRoutes( "DIALOG_X::OnClear" ) );
}


BOOST_AUTO_TEST_SUITE_END()

} // namespace SCH_CHANGE_TRACKING_ORACLE


#if defined( EESCHEMA )

// ---------------------------------------------------------------------------------------------
// The tracked change itself and the cost of what it compares (eeschema-linked builds only)
// ---------------------------------------------------------------------------------------------

namespace SCH_CHANGE_TRACKING_ORACLE
{
/// A loaded, empty schematic with a project, as the editor has one: the project gives the
/// saved state its project-settings group.
struct TRACKED_SCHEMATIC
{
    TRACKED_SCHEMATIC()
    {
        settings.LoadProject( "" );
        schematic = std::make_unique<SCHEMATIC>( &settings.Prj() );
        schematic->CreateDefaultScreens();
    }

    SETTINGS_MANAGER           settings;
    std::unique_ptr<SCHEMATIC> schematic;
};


inline double elapsedMs( std::chrono::steady_clock::time_point aStart )
{
    return std::chrono::duration<double, std::milli>( std::chrono::steady_clock::now() - aStart ).count();
}


/// Median, minimum and maximum of @a aSamples, in milliseconds.
inline std::string timing( std::vector<double> aSamples )
{
    std::sort( aSamples.begin(), aSamples.end() );
    std::ostringstream text;
    text << std::fixed << std::setprecision( 1 ) << "median=" << aSamples[aSamples.size() / 2]
         << " min=" << aSamples.front() << " max=" << aSamples.back() << " samples=" << aSamples.size();
    return text.str();
}


inline double median( std::vector<double> aSamples )
{
    std::sort( aSamples.begin(), aSamples.end() );
    return aSamples[aSamples.size() / 2];
}


BOOST_AUTO_TEST_SUITE( SchTrackedChange )


BOOST_FIXTURE_TEST_CASE( RecordsOnlyRealEditsOfTheSameDocument, TRACKED_SCHEMATIC )
{
    SCHEMATIC& doc = *schematic;

    // Recall: an edit made outside a commit is recorded once, by description.
    {
        SCH_TRACKED_CHANGE change( doc, "Add a note" );
        doc.RootScreen()->Append( new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "note" ) ) );
        BOOST_CHECK( change.Complete() );
        BOOST_CHECK( !change.Complete() );
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().ReadAfter( doc.ChangeJournal().Epoch(), 0 ).entries.at( 0 ).description,
                       "Add a note" );

    // Precision: nothing changed, nothing recorded, and not marked changed.
    {
        SCH_TRACKED_CHANGE change( doc, "Nothing" );
        BOOST_CHECK( !change.Complete() );
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );

    // The screen-restricted form compares the project settings as well as its screens:
    // precision when neither changed, recall for a change to the settings alone.
    {
        SCH_TRACKED_CHANGE change( doc, "Settings", { doc.RootScreen() }, SCH_TRACKED_CHANGE::Mark( doc ) );
        BOOST_CHECK( !change.Complete() );
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );

    {
        SCH_TRACKED_CHANGE change( doc, "Settings", { doc.RootScreen() }, SCH_TRACKED_CHANGE::Mark( doc ) );
        doc.Project().GetProjectFile().GetSheets().emplace_back( KIID(), wxS( "Tracked settings" ) );
        BOOST_CHECK( change.Complete() );
    }

    doc.Project().GetProjectFile().GetSheets().pop_back();
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 2u );

    // A replaced document starts a new journal epoch.  It is not an edit by the owner that was
    // running across the replacement: nothing is recorded and the owner must not mark it.
    const std::string oldEpoch = doc.ChangeJournal().Epoch();

    {
        SCH_TRACKED_CHANGE change( doc, "Across a replacement" );
        doc.CreateDefaultScreens();
        BOOST_CHECK_NE( doc.ChangeJournal().Epoch(), oldEpoch );
        BOOST_CHECK( !change.Complete() );
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );

    // The destructor completes an owner that returned early, with the same rules.
    {
        SCH_TRACKED_CHANGE change( doc, "Returned early" );
        doc.RootScreen()->Append( new SCH_TEXT( VECTOR2I( 1000, 0 ), wxS( "early" ) ) );
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );
}


/// A project KiCad has not saved yet, its sheet files and project file written by a harness or
/// another program: the project file has none of the entries a save writes from the schematic
/// itself.  KiCad's first save writes them (the sheet list, the top-level sheet list, the root
/// sheet's revision kept for IPC-2581 and the project file name), which changes the saved state
/// digest but must leave the save-stable digest alone, so publication can recognize that save as
/// the planned one (ReadDocumentLifecycleState.save_stable_state_sha256).  Any other change moves
/// the save-stable digest.
BOOST_AUTO_TEST_CASE( FirstSaveOfAnUnsavedProjectKeepsTheSaveStableState )
{
    const fs::path source( KI_TEST::GetEeschemaTestDataDir() );
    const fs::path copy = fs::temp_directory_path() / ( "kicad-save-stable-" + KIID().AsStdString() );
    struct CLEANUP { fs::path path; ~CLEANUP() { std::error_code error; fs::remove_all( path, error ); } } cleanup{ copy };
    fs::create_directories( copy );

    for( const char* name : { "issue13212.kicad_sch", "issue13212_subsheet_1.kicad_sch",
                              "issue13212_subsheet_2.kicad_sch" } )
    {
        fs::copy_file( source / name, copy / name );
    }

    {
        // Written the way the native journeys' harness writes it: the settings format and nothing a
        // save derives.
        std::ofstream project( copy / "issue13212.kicad_pro" );
        project << R"({ "meta": { "version": 3 } })";
    }

    SETTINGS_MANAGER           settings;
    std::unique_ptr<SCHEMATIC> schematic;
    KI_TEST::LoadSchematic( settings,
                            fs::relative( copy / "issue13212", KI_TEST::GetEeschemaTestDataDir() ).generic_string(),
                            schematic );
    BOOST_REQUIRE( schematic );
    schematic->RefreshHierarchy();
    PROJECT& project = schematic->Project();
    BOOST_REQUIRE( project.GetProjectFile().GetSheets().empty() );

    size_t sheets = 0;

    for( const SCH_SHEET_PATH& path : schematic->Hierarchy() )
    {
        if( !path.Last()->IsVirtualRootSheet() )
            ++sheets;
    }

    BOOST_REQUIRE_GT( sheets, 1u );

    const SCH_STATE_GROUPS before = SCH_STATE_GROUPS::Capture( *schematic );
    BOOST_REQUIRE_EQUAL( before.SaveStableSha256().size(), 64u );
    BOOST_CHECK_NE( before.SaveStableSha256(), before.DocumentSha256() );

    // The save's own project-file writing.  UpdateProjectFile sets the entries it derives from the
    // schematic and then asks the program's settings manager to write the file; this test's project
    // belongs to its own settings manager, which then writes it exactly as that save does.
    SCH_API_SAVE::UpdateProjectFile( *schematic, project );
    BOOST_REQUIRE( settings.SaveProject( project.GetProjectFullName(), &project ) );
    BOOST_CHECK_EQUAL( project.GetProjectFile().GetSheets().size(), sheets );

    std::ifstream written( copy / "issue13212.kicad_pro" );
    const nlohmann::json file = nlohmann::json::parse( written );
    BOOST_REQUIRE( file.contains( "sheets" ) );
    BOOST_CHECK_EQUAL( file.at( "sheets" ).size(), sheets );
    BOOST_CHECK_EQUAL( file.at( "meta" ).at( "filename" ).get<std::string>(), "issue13212.kicad_pro" );

    // Recall of the scenario: the save changed the project settings and nothing else, and the
    // save-stable digest recognizes exactly that change.
    const SCH_STATE_GROUPS saved = SCH_STATE_GROUPS::Capture( *schematic );
    BOOST_CHECK_NE( before.DocumentSha256(), saved.DocumentSha256() );
    BOOST_CHECK( before.ChangedGroups( saved ) == std::vector<std::string>{ "project-settings" } );
    BOOST_CHECK_EQUAL( before.SaveStableSha256(), saved.SaveStableSha256() );

    // A second save writes the same entries again: nothing changes.
    SCH_API_SAVE::UpdateProjectFile( *schematic, project );
    BOOST_REQUIRE( settings.SaveProject( project.GetProjectFullName(), &project ) );
    BOOST_CHECK_EQUAL( SCH_STATE_GROUPS::Capture( *schematic ).DocumentSha256(), saved.DocumentSha256() );

    // Precision: a project setting the save does not derive moves the save-stable digest, and
    // putting it back restores it.
    project.GetTextVars()[wxS( "SAVE_STABLE_PROBE" )] = wxS( "changed" );
    BOOST_CHECK_NE( SCH_STATE_GROUPS::Capture( *schematic ).SaveStableSha256(), saved.SaveStableSha256() );
    project.GetTextVars().erase( wxS( "SAVE_STABLE_PROBE" ) );
    BOOST_CHECK_EQUAL( SCH_STATE_GROUPS::Capture( *schematic ).SaveStableSha256(), saved.SaveStableSha256() );

    // So does any edit of a sheet.
    schematic->RootScreen()->Append( new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "save-stable probe" ) ) );
    BOOST_CHECK_NE( SCH_STATE_GROUPS::Capture( *schematic ).SaveStableSha256(), saved.SaveStableSha256() );

    // A capture without the project settings has no save-stable digest.
    BOOST_CHECK( SCH_STATE_GROUPS::CaptureScreens( *schematic, { schematic->RootScreen() } ).SaveStableSha256().empty() );
}


BOOST_FIXTURE_TEST_CASE( StagedCommitsCompareOnlyTheirItems, TRACKED_SCHEMATIC )
{
    SCHEMATIC&  doc = *schematic;
    SCH_SCREEN* screen = doc.RootScreen();
    TOOL_MANAGER manager;

    auto* text = new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "value" ) );
    screen->Append( text );

    LIB_SYMBOL library( wxS( "Tracked" ) );
    library.SetLibId( LIB_ID( wxS( "Automation" ), wxS( "Tracked" ) ) );
    auto* symbol = new SCH_SYMBOL;
    symbol->SetLibId( library.GetLibId() );
    symbol->SetLibSymbol( new LIB_SYMBOL( library ) );
    symbol->SetSchSymbolLibraryName( wxS( "Automation:Tracked" ) );
    screen->Append( symbol, false );

    {
        SCH_COMMIT commit( &manager );
        commit.Modify( text, screen );
        commit.Modify( symbol, screen );

        // Staged but unchanged, and a flag that is never saved: no persisted change.
        BOOST_CHECK( !commit.PersistsChange( doc ) );
        text->SetFlags( SELECTED );
        BOOST_CHECK( !commit.PersistsChange( doc ) );
        text->ClearFlags( SELECTED );

        // Recall for the item's own saved form.
        text->SetText( wxS( "changed" ) );
        BOOST_CHECK( commit.PersistsChange( doc ) );
        text->SetText( wxS( "value" ) );
        BOOST_CHECK( !commit.PersistsChange( doc ) );

        // Recall for the symbol's own library definition, which the screen's cache follows.
        symbol->GetLibSymbolRef()->GetValueField().SetText( wxS( "edited definition" ) );
        BOOST_CHECK( commit.PersistsChange( doc ) );
        symbol->GetLibSymbolRef()->GetValueField().SetText( library.GetValueField().GetText() );
        BOOST_CHECK( !commit.PersistsChange( doc ) );
    }

    {
        // An added item has no copy to compare: it is a change.
        SCH_COMMIT commit( &manager );
        auto       added = std::make_unique<SCH_TEXT>( VECTOR2I( 2000, 0 ), wxS( "added" ) );
        commit.Add( added.get(), screen );
        BOOST_CHECK( commit.PersistsChange( doc ) );
        commit.Abandon();
        BOOST_CHECK( commit.Empty() );
    }

    // A change the owner made outside its commit (a sheet screen renamed by Sheet Properties)
    // counts only when the owner reports it: then it is recorded once although every staged
    // item is unchanged.
    {
        SCH_COMMIT commit( &manager );
        commit.Modify( text, screen );
        SCH_TRACKED_CHANGE change( doc, "Unreported", commit );
        BOOST_CHECK( !change.Complete() );
        commit.Abandon();
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );

    {
        SCH_COMMIT commit( &manager );
        commit.Modify( text, screen );
        SCH_TRACKED_CHANGE change( doc, "Changed outside the commit", commit );
        change.ChangedOutsideCommit();
        BOOST_CHECK( change.Complete() );
        commit.Abandon();
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().ReadAfter( doc.ChangeJournal().Epoch(), 0 ).entries.at( 0 ).description,
                       "Changed outside the commit" );

    // The reference inventory a commit kept before annotating is compared as well: designators
    // handed out since then are saved with the project settings, so they are a change even when
    // every staged symbol compares unchanged (Annotate with "Reset existing annotations" giving a
    // symbol back a designator taken out of the inventory).  The rendered journey reaches both
    // sides through Annotate (an annotated schematic, and that reset); this checks the
    // comparison's own rules, which the journey cannot tell apart: only the first keep counts,
    // and abandoning the commit drops the kept inventory.
    {
        std::shared_ptr<REFDES_TRACKER>& live = doc.Settings().m_refDesTracker;

        if( !live )
            live = std::make_shared<REFDES_TRACKER>();

        live->Insert( "R1" );

        SCH_COMMIT commit( &manager );
        commit.Modify( text, screen );
        commit.KeepReferenceInventory( doc );

        // Precision: nothing handed out since the inventory was kept.
        BOOST_CHECK( !commit.PersistsChange( doc ) );

        // Recall.  Only the first keep counts, so annotating again in the same commit still
        // compares with the inventory from before the first annotation.
        live->Insert( "R2" );
        commit.KeepReferenceInventory( doc );
        BOOST_CHECK( commit.PersistsChange( doc ) );

        SCH_TRACKED_CHANGE change( doc, "Annotate", commit );
        BOOST_CHECK( change.Complete() );

        // Abandoning the commit drops the kept inventory with its items.
        commit.Abandon();
        BOOST_CHECK( !commit.PersistsChange( doc ) );
        live->Clear();
    }

    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 2u );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().ReadAfter( doc.ChangeJournal().Epoch(), 1 ).entries.at( 0 ).description,
                       "Annotate" );

    {
        // Only the staged form takes a report: the other forms compare what they may change.
        SCH_TRACKED_CHANGE whole( doc, "Whole state" );
        BOOST_CHECK_THROW( whole.ChangedOutsideCommit(), std::logic_error );
    }

    // A staged tracker that finishes after the document was replaced neither pushes nor
    // reverts: the replacement freed the document's items.  The staged item here lives on a
    // screen the test owns, so the check can see that it was left exactly as edited.
    SCH_SCREEN kept;
    auto*      keptText = new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "before" ) );
    kept.Append( keptText );

    {
        SCH_COMMIT         commit( &manager );
        SCH_TRACKED_CHANGE change( doc, "Across a replacement", commit );
        commit.Modify( keptText, &kept );
        keptText->SetText( wxS( "after" ) );
        doc.CreateDefaultScreens();
        BOOST_CHECK( !change.PushOrRevert( commit, wxS( "Across a replacement" ) ) );
        BOOST_CHECK( commit.Empty() );
    }

    BOOST_CHECK_EQUAL( keptText->GetText(), wxS( "after" ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );
}


/// A child sheet below the first top-level sheet, with a screen of its own, so a comparison has a
/// screen other than the one an owner starts on.
inline SCH_SCREEN* addChildSheet( SCHEMATIC& aSchematic, const wxString& aFileName )
{
    auto* sheet = new SCH_SHEET( &aSchematic );
    auto* screen = new SCH_SCREEN( &aSchematic );
    sheet->SetScreen( screen );
    sheet->GetField( FIELD_T::SHEET_NAME )->SetText( wxS( "Tracked child" ) );
    sheet->GetField( FIELD_T::SHEET_FILENAME )->SetText( aFileName );
    screen->SetFileName( aFileName );
    aSchematic.RootScreen()->Append( sheet );
    aSchematic.RefreshHierarchy();
    return screen;
}


/// Complete a part-restricted tracker declared with @a aParts around @a aEdit.
template <typename EDIT>
inline bool partsChanged( SCHEMATIC& aSchematic, const SCH_PERSISTED_PARTS& aParts, EDIT aEdit )
{
    SCH_TRACKED_CHANGE change( aSchematic, "Parts", aParts );
    aEdit();
    return change.Complete();
}


/**
 * Page Settings compares every screen's paper and title block, the schematic-wide data and the
 * project settings instead of writing the whole design.  The rendered journey (NativeEventJourney)
 * proves a paper change, a cancel and an unchanged OK; this checks the parts the journey cannot
 * reach through the dialog on its fixture: a title block exported to another sheet, the drawing
 * sheet file name, an embedded drawing sheet, and that what the parts do not name (items) is not
 * written at all.  Unit level because it pins the comparison's own coverage of each part.
 */
BOOST_FIXTURE_TEST_CASE( PageSettingsPartsCompareWhatTheDialogReaches, TRACKED_SCHEMATIC )
{
    SCHEMATIC&                doc = *schematic;
    SCH_SCREEN*               child = addChildSheet( doc, wxS( "tracked_child.kicad_sch" ) );
    const SCH_PERSISTED_PARTS parts = SCH_PERSISTED_PARTS::PageSettings();

    // Precision: nothing changed, nothing recorded.
    BOOST_CHECK( !partsChanged( doc, parts, [] {} ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );

    // Recall: a title block exported to a sheet other than the current one.
    const TITLE_BLOCK title = child->GetTitleBlock();
    BOOST_CHECK( partsChanged( doc, parts,
                               [&]
                               {
                                   TITLE_BLOCK exported = title;
                                   exported.SetComment( 0, wxS( "exported" ) );
                                   child->SetTitleBlock( exported );
                               } ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );

    // Setting back the saved value is a change too; setting the same value is none.
    BOOST_CHECK( partsChanged( doc, parts, [&] { child->SetTitleBlock( title ); } ) );
    BOOST_CHECK( !partsChanged( doc, parts, [&] { child->SetTitleBlock( title ); } ) );

    // Recall: the current screen's paper.
    const PAGE_INFO paper = doc.RootScreen()->GetPageSettings();
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.RootScreen()->SetPageSettings( PAGE_INFO( PAGE_SIZE_TYPE::A3 ) ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.RootScreen()->SetPageSettings( paper ); } ) );

    // Recall: the drawing sheet file name, a project setting.
    const wxString drawingSheet = doc.Settings().m_SchDrawingSheetFileName;
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.Settings().m_SchDrawingSheetFileName = wxS( "tracked.kicad_wks" ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.Settings().m_SchDrawingSheetFileName = drawingSheet; } ) );

    // Recall: choosing a drawing sheet to embed adds it to the schematic's embedded files, even
    // when the dialog is then cancelled; accepting removes the one it replaces.
    auto embedded = std::make_unique<EMBEDDED_FILES::EMBEDDED_FILE>();
    embedded->name = wxS( "tracked.kicad_wks" );
    embedded->type = EMBEDDED_FILES::EMBEDDED_FILE::FILE_TYPE::WORKSHEET;
    embedded->decompressedData = { '(', 'k', 'i', 'c', 'a', 'd', '_', 'w', 'k', 's', ')' };
    BOOST_REQUIRE( EMBEDDED_FILES::CompressAndEncode( *embedded ) == EMBEDDED_FILES::RETURN_CODE::OK );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.GetEmbeddedFiles()->AddFile( embedded.release() ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.GetEmbeddedFiles()->RemoveFile( wxS( "tracked.kicad_wks" ) ); } ) );

    // The parts name no item, so an item is never written: the owner's review in the oracle
    // requires that Page Settings cannot make one.
    BOOST_CHECK( !partsChanged( doc, parts,
                                [&] { child->Append( new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "not a page part" ) ) ); } ) );

    // A tracker must name the parts it compares.
    BOOST_CHECK_THROW( partsChanged( doc, SCH_PERSISTED_PARTS(), [] {} ), std::invalid_argument );
}


/**
 * Import Sheet and design block placement compare the identities of every screen's items, the
 * library cache of the screen they load into, the schematic-wide data and the project settings.
 * The rendered journey proves a kept placement, a cancel that left nothing (with a new library
 * definition and a handed-out designator), and a cancel that renumbered an item on another sheet;
 * this checks the parts the journey's fixture files cannot reach: a cached definition refreshed in
 * place, embedded files, fonts, net chains and bus aliases a file carries, the reference
 * inventory, and a screen that is not part of the design.
 */
BOOST_FIXTURE_TEST_CASE( SheetImportPartsCompareWhatLoadingReaches, TRACKED_SCHEMATIC )
{
    SCHEMATIC&  doc = *schematic;
    SCH_SCREEN* target = doc.RootScreen();
    SCH_SCREEN* child = addChildSheet( doc, wxS( "tracked_import_child.kicad_sch" ) );
    auto*       note = new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "child note" ) );
    child->Append( note );

    LIB_SYMBOL library( wxS( "Imported" ) );
    library.SetLibId( LIB_ID( wxS( "Automation" ), wxS( "Imported" ) ) );
    auto* symbol = new SCH_SYMBOL;
    symbol->SetLibId( library.GetLibId() );
    symbol->SetLibSymbol( new LIB_SYMBOL( library ) );
    symbol->SetSchSymbolLibraryName( wxS( "Automation:Imported" ) );
    target->Append( symbol );
    BOOST_REQUIRE( target->GetLibSymbols().count( wxS( "Automation:Imported" ) ) );

    const SCH_PERSISTED_PARTS parts = SCH_PERSISTED_PARTS::SheetImport( target );

    // Precision: nothing changed, and a placed item that the cancel removed again.
    BOOST_CHECK( !partsChanged( doc, parts, [] {} ) );

    BOOST_CHECK( !partsChanged( doc, parts,
                                [&]
                                {
                                    auto* placed = new SCH_TEXT( VECTOR2I( 2540000, 0 ), wxS( "placed" ) );
                                    target->Append( placed );
                                    target->Remove( placed );
                                    delete placed;
                                } ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );

    // Recall: an existing item on another sheet renumbered because the file repeated its identity.
    BOOST_CHECK( partsChanged( doc, parts, [&] { const_cast<KIID&>( note->m_Uuid ) = KIID(); } ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 1u );

    // Recall: an item left on the screen.
    BOOST_CHECK( partsChanged( doc, parts,
                               [&] { target->Append( new SCH_TEXT( VECTOR2I( 5080000, 0 ), wxS( "left" ) ) ); } ) );

    // Recall: an equal cached definition refreshed in place, and precision when it is put back.
    LIB_SYMBOL* cached = target->GetLibSymbols().at( wxS( "Automation:Imported" ) );
    const wxString value = cached->GetValueField().GetText();
    BOOST_CHECK( partsChanged( doc, parts, [&] { cached->GetValueField().SetText( wxS( "refreshed" ) ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { cached->GetValueField().SetText( value ); } ) );
    BOOST_CHECK( !partsChanged( doc, parts, [&] { cached->GetValueField().SetText( value ); } ) );

    // Recall: the schematic-wide data a loaded file carries.
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.GetEmbeddedFiles()->SetAreFontsEmbedded( true ); } ) );

    CONNECTION_GRAPH::NET_CHAIN_DEFINITION chain;
    chain.terminals = { { wxS( "R1" ), wxS( "1" ) }, { wxS( "R2" ), wxS( "2" ) } };
    chain.excludedNets = { wxS( "Tracked excluded net" ) };
    BOOST_CHECK( partsChanged( doc, parts,
                               [&] { doc.ConnectionGraph()->SetNetChainDefinitions( { { wxS( "TRACKED" ), chain } } ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.ConnectionGraph()->SetNetChainDefinitions( {} ); } ) );

    // Recall: project settings the load changes, bus aliases and the reference inventory.
    auto alias = std::make_shared<BUS_ALIAS>();
    alias->SetName( wxS( "TRACKED" ) );
    alias->AddMember( wxS( "A" ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.AddBusAlias( alias ); } ) );
    BOOST_CHECK( partsChanged( doc, parts, [&] { doc.SetBusAliases( {} ); } ) );

    std::shared_ptr<REFDES_TRACKER>& inventory = doc.Settings().m_refDesTracker;

    if( !inventory )
        inventory = std::make_shared<REFDES_TRACKER>();

    BOOST_CHECK( partsChanged( doc, parts, [&] { inventory->Insert( "R7" ); } ) );
    BOOST_CHECK( !partsChanged( doc, parts, [] {} ) );

    // A screen that is not part of the design cannot be captured, so the comparison counts it
    // as changed rather than comparing nothing.
    SCH_SCREEN outside;
    BOOST_CHECK( partsChanged( doc, SCH_PERSISTED_PARTS::SheetImport( &outside ), [] {} ) );
    BOOST_CHECK_THROW( SCH_PERSISTED_PARTS::SheetImport( nullptr ), std::invalid_argument );

    // A replaced document is not the owner's edit: nothing is recorded.
    const uint64_t recorded = doc.ChangeJournal().Sequence();
    BOOST_CHECK( recorded > 0 );
    BOOST_CHECK( !partsChanged( doc, parts, [&] { doc.CreateDefaultScreens(); } ) );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), 0u );
}


/// A placement, paste or duplication that is cancelled returns the designators its annotation
/// handed out: the reference inventory kept before annotating is restored.  The rendered journey
/// proves this for a cancelled duplication; this checks the keep and restore themselves,
/// including an inventory that did not exist, which the journey cannot reach.
BOOST_FIXTURE_TEST_CASE( DroppedAnnotationReturnsItsDesignators, TRACKED_SCHEMATIC )
{
    SCHEMATIC&                       doc = *schematic;
    std::shared_ptr<REFDES_TRACKER>& live = doc.Settings().m_refDesTracker;

    if( !live )
        live = std::make_shared<REFDES_TRACKER>();

    live->Insert( "R1" );

    std::unique_ptr<REFDES_TRACKER> kept = SCH_COMMIT::CopyReferenceInventory( doc );
    BOOST_REQUIRE( kept );

    // Annotating a symbol that is then dropped hands out R2.
    live->Insert( "R2" );
    SCH_COMMIT::RestoreReferenceInventory( doc, kept.get() );
    BOOST_CHECK( live->Contains( "R1" ) );
    BOOST_CHECK( !live->Contains( "R2" ) );

    // Kept from a project without an inventory: restoring empties it.
    SCH_COMMIT::RestoreReferenceInventory( doc, nullptr );
    BOOST_CHECK( !live->Contains( "R1" ) );

    live.reset();
    BOOST_CHECK( !SCH_COMMIT::CopyReferenceInventory( doc ) );
    SCH_COMMIT::RestoreReferenceInventory( doc, kept.get() );
    BOOST_REQUIRE( live );
    BOOST_CHECK( live->Contains( "R1" ) );
}


/// A global label as an API client or the XML synchronization writes it: KiCad's hidden
/// reference field only when @a aReferenceField, then the custom fields @a aCustom
/// (name, visible), in that order.
inline kiapi::schematic::types::GlobalLabel apiGlobalLabel( const std::string& aText, int aX, bool aReferenceField,
                                                             const std::vector<std::pair<std::string, bool>>& aCustom )
{
    kiapi::schematic::types::GlobalLabel label;
    label.mutable_id()->set_value( KIID().AsStdString() );
    label.mutable_position()->set_x_nm( aX );
    label.mutable_position()->set_y_nm( 25400000 );
    label.set_shape( kiapi::schematic::types::SLSH_BIDI );
    label.set_spin_style( kiapi::schematic::types::SLSS_RIGHT );
    label.mutable_text()->set_text( aText );
    *label.mutable_text()->mutable_position() = label.position();
    label.mutable_text()->mutable_attributes()->mutable_size()->set_x_nm( 1270000 );
    label.mutable_text()->mutable_attributes()->mutable_size()->set_y_nm( 1270000 );
    label.mutable_text()->mutable_attributes()->set_horizontal_alignment( kiapi::common::types::HA_LEFT );
    label.mutable_text()->mutable_attributes()->set_vertical_alignment( kiapi::common::types::VA_CENTER );

    auto field = [&]( kiapi::schematic::types::SchematicField* aField, const std::string& aName,
                      const std::string& aValue, bool aVisible, int aDy )
    {
        aField->set_name( aName );
        aField->set_visible( aVisible );
        aField->set_allow_auto_place( true );
        aField->mutable_text()->set_text( aValue );
        aField->mutable_text()->mutable_position()->set_x_nm( aX );
        aField->mutable_text()->mutable_position()->set_y_nm( 25400000 + aDy );
        aField->mutable_text()->mutable_attributes()->mutable_size()->set_x_nm( 1270000 );
        aField->mutable_text()->mutable_attributes()->mutable_size()->set_y_nm( 1270000 );
        aField->mutable_text()->mutable_attributes()->set_multiline( true );
    };

    // As KiCad serializes the field, but placed away from the label so the request is visible.
    if( aReferenceField )
        field( label.mutable_intersheet_refs_field(), "Intersheetrefs", "${INTERSHEET_REFS}", false, -2540000 );

    int dy = 2540000;

    for( const auto& [name, visible] : aCustom )
    {
        field( label.add_fields(), name, "value of " + name, visible, dy );
        dy += 2540000;
    }

    return label;
}


inline std::unique_ptr<SCH_GLOBALLABEL> deserialized( const kiapi::schematic::types::GlobalLabel& aLabel )
{
    google::protobuf::Any any;
    any.PackFrom( aLabel );
    auto label = std::make_unique<SCH_GLOBALLABEL>();

    if( !label->Deserialize( any ) )
        return nullptr;

    return label;
}


inline std::string serialized( const SCH_GLOBALLABEL& aLabel )
{
    google::protobuf::Any any;
    aLabel.Serialize( any );
    kiapi::schematic::types::GlobalLabel label;
    BOOST_REQUIRE( any.UnpackTo( &label ) );
    return label.SerializeAsString();
}


/// The label's custom fields as (name, visible), in stored order.
inline std::vector<std::pair<std::string, bool>> customFields( const SCH_GLOBALLABEL& aLabel )
{
    std::vector<std::pair<std::string, bool>> fields;

    for( const SCH_FIELD& field : aLabel.GetFields() )
    {
        if( field.GetId() != FIELD_T::INTERSHEET_REFS )
            fields.emplace_back( field.GetName( false ).ToStdString(), field.IsVisible() );
    }

    return fields;
}


/// Exactly one reference field, and it is the first: the place a label read from a file keeps it
/// and the label properties dialog protects.
inline const SCH_FIELD& firstReferenceField( const SCH_GLOBALLABEL& aLabel )
{
    BOOST_REQUIRE( !aLabel.GetFields().empty() );
    BOOST_REQUIRE( std::count_if( aLabel.GetFields().begin(), aLabel.GetFields().end(),
                                  []( const SCH_FIELD& field )
                                  {
                                      return field.GetId() == FIELD_T::INTERSHEET_REFS;
                                  } )
                   == 1 );
    BOOST_REQUIRE( aLabel.GetFields().front().GetId() == FIELD_T::INTERSHEET_REFS );
    return aLabel.GetFields().front();
}


/**
 * A global label written through the API or the XML synchronization keeps KiCad's hidden
 * reference field in its first place, with or without the field in the request and with custom
 * fields, and the Schematic Setup refresh (SCHEMATIC::RecomputeIntersheetRefs(), which an
 * accepted Setup runs under its tracked change) touches only that field.  An unchanged Setup
 * therefore records nothing; showing references records one change that shows exactly the
 * reference fields.  Unit level because it pins the lookup itself: a label whose reference
 * field is not first, or missing, cannot be written through the API, so the native journey
 * (NativeSymbolSheetOwnershipJourney) cannot reach those two cases.
 */
BOOST_FIXTURE_TEST_CASE( ApiGlobalLabelsKeepTheirReferenceFieldThroughSetup, TRACKED_SCHEMATIC )
{
    SCHEMATIC&  doc = *schematic;
    SCH_SCREEN* screen = doc.CurrentSheet().LastScreen();
    BOOST_REQUIRE( screen == doc.RootScreen() );

    const std::vector<std::pair<std::string, bool>> custom = { { "Signal note", true }, { "Reviewer", false } };
    const std::vector<std::pair<std::string, bool>> none;

    const kiapi::schematic::types::GlobalLabel bareProto = apiGlobalLabel( "BARE", 25400000, false, none );
    const kiapi::schematic::types::GlobalLabel explicitProto = apiGlobalLabel( "EXPLICIT", 50800000, true, none );
    const kiapi::schematic::types::GlobalLabel customProto = apiGlobalLabel( "CUSTOM", 76200000, false, custom );
    const kiapi::schematic::types::GlobalLabel bothProto = apiGlobalLabel( "BOTH", 101600000, true, custom );

    std::unique_ptr<SCH_GLOBALLABEL> bare = deserialized( bareProto );
    std::unique_ptr<SCH_GLOBALLABEL> explicitField = deserialized( explicitProto );
    std::unique_ptr<SCH_GLOBALLABEL> customOnly = deserialized( customProto );
    std::unique_ptr<SCH_GLOBALLABEL> both = deserialized( bothProto );
    BOOST_REQUIRE( bare && explicitField && customOnly && both );

    // Without the field in the request: the one a new label has, hidden on the label.
    for( SCH_GLOBALLABEL* label : { bare.get(), customOnly.get() } )
    {
        const SCH_FIELD& refs = firstReferenceField( *label );
        BOOST_CHECK_EQUAL( refs.GetText(), wxS( "${INTERSHEET_REFS}" ) );
        BOOST_CHECK( !refs.IsVisible() );
        BOOST_CHECK( refs.GetTextPos() == label->GetPosition() );
        BOOST_CHECK( refs.GetParent() == label );
    }

    // With it: the request's field, still first.
    for( SCH_GLOBALLABEL* label : { explicitField.get(), both.get() } )
    {
        const SCH_FIELD& refs = firstReferenceField( *label );
        BOOST_CHECK_EQUAL( refs.GetText(), wxS( "${INTERSHEET_REFS}" ) );
        BOOST_CHECK( !refs.IsVisible() );
        BOOST_CHECK( refs.GetTextPos() == label->GetPosition() + VECTOR2I( 0, -schIUScale.mmToIU( 2.54 ) ) );
    }

    // Custom fields follow in the request's order and keep their visibility.
    BOOST_CHECK( customFields( *bare ).empty() );
    BOOST_CHECK( customFields( *explicitField ).empty() );
    BOOST_CHECK( customFields( *customOnly ) == custom );
    BOOST_CHECK( customFields( *both ) == custom );

    // What KiCad reports back carries the field in its own place and the custom fields apart, and
    // writing that back gives the same label.
    for( SCH_GLOBALLABEL* label : { bare.get(), explicitField.get(), customOnly.get(), both.get() } )
    {
        google::protobuf::Any any;
        label->Serialize( any );
        kiapi::schematic::types::GlobalLabel reported;
        BOOST_REQUIRE( any.UnpackTo( &reported ) );
        BOOST_CHECK( reported.has_intersheet_refs_field() );
        BOOST_CHECK_EQUAL( reported.intersheet_refs_field().text().text(), "${INTERSHEET_REFS}" );
        BOOST_CHECK_EQUAL( (size_t) reported.fields_size(), customFields( *label ).size() );

        std::unique_ptr<SCH_GLOBALLABEL> again = deserialized( reported );
        BOOST_REQUIRE( again );
        BOOST_CHECK_EQUAL( serialized( *again ), serialized( *label ) );
    }

    // A custom field under the reference field's name would replace it when the saved file is
    // read back.  It is refused, and a refused request leaves an existing label as it was.
    for( const std::string& reserved : { std::string( "Intersheetrefs" ), std::string( "intersheetrefs" ),
                                         std::string( "Intersheet References" ) } )
    {
        kiapi::schematic::types::GlobalLabel shadowing = apiGlobalLabel( "SHADOW", 127000000, false,
                                                                         { { "Signal note", true },
                                                                           { reserved, false } } );
        BOOST_CHECK_MESSAGE( !deserialized( shadowing ), reserved );

        const std::string before = serialized( *both );
        google::protobuf::Any any;
        shadowing.mutable_id()->set_value( both->m_Uuid.AsStdString() );
        any.PackFrom( shadowing );
        BOOST_CHECK( !both->Deserialize( any ) );
        BOOST_CHECK_EQUAL( serialized( *both ), before );
    }

    // Each label with the custom fields it must keep, as (name, visible).
    const std::vector<std::pair<SCH_GLOBALLABEL*, std::vector<std::pair<std::string, bool>>>> labels = {
        { bare.get(), none }, { explicitField.get(), none }, { customOnly.get(), custom }, { both.get(), custom }
    };

    for( std::unique_ptr<SCH_GLOBALLABEL>* owned : { &bare, &explicitField, &customOnly, &both } )
        screen->Append( owned->release() );

    auto refreshedBySetup = [&]()
    {
        SCH_TRACKED_CHANGE change( doc, "Edit Schematic Setup", { doc.RootScreen() }, SCH_TRACKED_CHANGE::Mark( doc ) );
        doc.RecomputeIntersheetRefs();
        return change.Complete();
    };

    // An unchanged Setup (references hidden) changes nothing and records nothing: in particular
    // the visible custom field stays visible.
    BOOST_REQUIRE( !doc.Settings().m_IntersheetRefsShow );
    const uint64_t unchanged = doc.ChangeJournal().Sequence();
    BOOST_CHECK( !refreshedBySetup() );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), unchanged );

    for( const auto& [label, kept] : labels )
    {
        BOOST_CHECK( !firstReferenceField( *label ).IsVisible() );
        BOOST_CHECK( customFields( *label ) == kept );
    }

    // Showing references shows each reference field and nothing else, as one recorded change;
    // hiding them again restores the visibility.
    doc.Settings().m_IntersheetRefsShow = true;
    BOOST_CHECK( refreshedBySetup() );
    BOOST_CHECK_EQUAL( doc.ChangeJournal().Sequence(), unchanged + 1 );

    for( const auto& [label, kept] : labels )
    {
        BOOST_CHECK( firstReferenceField( *label ).IsVisible() );
        BOOST_CHECK( customFields( *label ) == kept );
    }

    BOOST_CHECK( !refreshedBySetup() );

    doc.Settings().m_IntersheetRefsShow = false;
    BOOST_CHECK( refreshedBySetup() );

    for( const auto& [label, kept] : labels )
    {
        BOOST_CHECK( !firstReferenceField( *label ).IsVisible() );
        BOOST_CHECK( customFields( *label ) == kept );
    }

    // The refresh finds the field by its type, not its place: a label whose custom field comes
    // first shows the reference field, not the custom one, and a label without any field gets
    // the default field first instead of an out-of-range read.
    SCH_GLOBALLABEL* reordered = labels[3].first;
    std::rotate( reordered->GetFields().begin(), reordered->GetFields().begin() + 1, reordered->GetFields().end() );
    BOOST_REQUIRE( reordered->GetFields().front().GetId() == FIELD_T::USER );

    SCH_GLOBALLABEL* fieldless = labels[0].first;
    fieldless->GetFields().clear();

    doc.Settings().m_IntersheetRefsShow = true;
    doc.RecomputeIntersheetRefs();

    const SCH_FIELD* reorderedRefs = static_cast<const SCH_GLOBALLABEL*>( reordered )->GetField( FIELD_T::INTERSHEET_REFS );
    BOOST_REQUIRE( reorderedRefs );
    BOOST_CHECK( reorderedRefs->IsVisible() );
    BOOST_CHECK( customFields( *reordered ) == custom );

    const SCH_FIELD& created = firstReferenceField( *fieldless );
    BOOST_CHECK_EQUAL( created.GetText(), wxS( "${INTERSHEET_REFS}" ) );
    BOOST_CHECK( created.IsVisible() );
    BOOST_CHECK( created.GetParent() == fieldless );

    doc.Settings().m_IntersheetRefsShow = false;
    doc.RecomputeIntersheetRefs();
    BOOST_CHECK( !reorderedRefs->IsVisible() );
    BOOST_CHECK( customFields( *reordered ) == custom );
}


/**
 * Rebuilding deleted schematic files from saved XML (lane 2C, rebuild_screen_identity): only the root KiCad
 * creates for a project whose schematic files are gone may adopt the identity its saved root file had.  Each
 * refusal is checked on its own, with every other condition met, against the precision case it must not catch,
 * and with the exact message KiCad reports for it (the checked batch passes it on to the person as the reason).
 * The ordering rules of the operation (first in its batch, canonical UUID, retry identity) and the rollback of a
 * rejected batch need a live editor; the PSU/CPU rebuild journey (NativeXmlRebuildJourney) proves those.
 */
BOOST_FIXTURE_TEST_CASE( OnlyANewEmptyRootMayAdoptASavedScreenIdentity, TRACKED_SCHEMATIC )
{
    auto refusal = []( SCHEMATIC& aSchematic, std::optional<SCH_SHEET_PATH> aPath = std::nullopt )
    {
        return API_HANDLER_SCH::ScreenIdentityRefusal( aSchematic, aPath ? *aPath : aSchematic.Hierarchy().at( 0 ) );
    };
    const std::string notSingleRoot = "Only the root sheet of a single-root schematic can adopt a screen identity";
    const std::string notNew = "Only a root that was never loaded from or saved to a file can adopt a screen identity";
    const std::string notEmpty = "Only an empty root can adopt a screen identity";
    const std::string notAlone = "Only a schematic with nothing but its root can adopt a screen identity";
    SCHEMATIC&  doc = *schematic;
    SCH_SCREEN* screen = doc.RootScreen();
    BOOST_REQUIRE( doc.Hierarchy().at( 0 ).LastScreen() == screen );

    // Precision: a new root, never loaded or saved, with nothing on it and no file at its path.
    const fs::path file = fs::temp_directory_path() / ( "rebuilt-root-" + KIID().AsStdString() + ".kicad_sch" );
    screen->SetFileName( wxString::FromUTF8( file.string() ) );
    BOOST_CHECK_MESSAGE( !refusal( doc ), refusal( doc ).value_or( "" ) );

    // A root KiCad loaded from its file.
    screen->SetFileFormatVersionAtLoad( 20250318 );
    BOOST_CHECK_EQUAL( refusal( doc ).value_or( "" ), notNew );
    screen->SetFileFormatVersionAtLoad( 0 );

    // A root KiCad knows it saved.
    screen->SetFileExists( true );
    BOOST_CHECK_EQUAL( refusal( doc ).value_or( "" ), notNew );
    screen->SetFileExists( false );

    // A file at the root's path that KiCad never read: it is kept, never replaced by a rebuild.
    {
        std::ofstream( file ) << "(kicad_sch)";
    }
    BOOST_CHECK_EQUAL( refusal( doc ).value_or( "" ), notNew );
    fs::remove( file );
    BOOST_CHECK( !refusal( doc ) );

    // A root holding an object.
    auto* note = new SCH_TEXT( VECTOR2I( 0, 0 ), wxS( "note" ) );
    screen->Append( note );
    BOOST_CHECK_EQUAL( refusal( doc ).value_or( "" ), notEmpty );
    screen->Remove( note );
    delete note;
    BOOST_CHECK( !refusal( doc ) );

    // A child sheet cannot adopt the root's identity, and a root showing one holds its sheet symbol.
    {
        TRACKED_SCHEMATIC nested;
        SCH_SCREEN*       childScreen = addChildSheet( *nested.schematic, wxS( "child.kicad_sch" ) );
        BOOST_REQUIRE_EQUAL( nested.schematic->Hierarchy().size(), 2u );
        BOOST_CHECK_EQUAL( refusal( *nested.schematic ).value_or( "" ), notEmpty );
        BOOST_CHECK_EQUAL( refusal( *nested.schematic, nested.schematic->Hierarchy().at( 1 ) ).value_or( "" ), notSingleRoot );

        // A hierarchy that still shows a sheet the root no longer holds: the root is empty, but the schematic is not.
        SCH_SCREEN* nestedRoot = nested.schematic->RootScreen();
        SCH_SHEET*  childSheet = nested.schematic->Hierarchy().at( 1 ).Last();
        BOOST_REQUIRE( childSheet && childSheet->GetScreen() == childScreen );
        nestedRoot->Remove( childSheet );
        BOOST_REQUIRE( nestedRoot->Items().empty() );
        BOOST_REQUIRE_EQUAL( nested.schematic->Hierarchy().size(), 2u );
        BOOST_CHECK_EQUAL( refusal( *nested.schematic ).value_or( "" ), notAlone );
        nestedRoot->Append( childSheet );
    }

    // A root holding a library cache but no object.
    {
        TRACKED_SCHEMATIC cached;
        cached.schematic->RootScreen()->AddLibSymbol( new LIB_SYMBOL( wxS( "R" ) ) );
        BOOST_CHECK_EQUAL( refusal( *cached.schematic ).value_or( "" ), notEmpty );
    }

    // A schematic with a second top-level sheet: neither root is the project's only root.
    {
        TRACKED_SCHEMATIC twoRoots;
        SCHEMATIC& multi = *twoRoots.schematic;
        const SCH_SHEET_PATH first = multi.Hierarchy().at( 0 );
        BOOST_CHECK( !refusal( multi, first ) );
        auto* second = new SCH_SHEET( &multi );
        second->SetScreen( new SCH_SCREEN( &multi ) );
        second->GetScreen()->SetFileName( wxS( "second.kicad_sch" ) );
        multi.AddTopLevelSheet( second );
        BOOST_REQUIRE_EQUAL( multi.GetTopLevelSheets().size(), 2u );
        BOOST_CHECK_EQUAL( refusal( multi, first ).value_or( "" ), notSingleRoot );
        SCH_SHEET_PATH secondPath;
        secondPath.push_back( second );
        BOOST_CHECK_EQUAL( refusal( multi, secondPath ).value_or( "" ), notSingleRoot );
    }
}


namespace
{
using namespace kiapi::automation::v1;

/// The checked batch controller over a scripted native peer, for its root-identity rule: a checked batch keeps the
/// document's native identity (its root screen's), except that a rebuild's first operation may give it exactly the
/// identity that operation requests, when the native result says it changed it.
struct IDENTITY_CONTROLLER
{
    CHECKED_SCHEMATIC_CONTROLLER controller;
    DocumentLifecycleState       state;
    std::string                  process = KIID().AsStdString();
    std::string                  identityAfter;     ///< Native identity after the batch; empty keeps it.
    bool                         claimChanged = false;
    bool                         reject = false;
    unsigned                     mutations = 0;

    IDENTITY_CONTROLLER()
    {
        auto* document = state.mutable_document();
        document->set_type( kiapi::common::types::DOCTYPE_SCHEMATIC );
        document->mutable_sheet_path()->add_path()->set_value( KIID().AsStdString() );
        document->mutable_project()->set_name( "rebuilt" );
        document->mutable_project()->set_path( fs::temp_directory_path().string() );
        state.set_process_epoch( process );
        state.set_native_identity( KIID().AsStdString() );
        state.mutable_revision()->set_epoch( KIID().AsStdString() );
        state.mutable_revision()->set_sequence( 2 );
        state.set_scope( DLS_SCHEMATIC_HIERARCHY );
        state.set_project_settings_included( true );
        state.set_state_sha256( std::string( 64, 'a' ) );
        state.set_native_content_dirty( true );
        for( const char* name : { "rebuilt.kicad_sch", "rebuilt.kicad_pro" } )
        {
            const std::string path = ( fs::temp_directory_path() / name ).string();
            state.add_native_files( path );
            auto* baseline = state.add_file_baselines();
            baseline->set_path( path );
            baseline->set_baseline_path( path );
            baseline->set_baseline_known( true );
            baseline->set_current_known( true );
            baseline->set_baseline_exists( false );
            baseline->set_current_exists( false );
            baseline->set_status( NFBS_UNCHANGED );
        }
    }

    CheckedSchematicBatch Request( const std::vector<SchematicItemOperation>& aOperations )
    {
        CheckedSchematicBatch request;
        request.mutable_expected_state()->CopyFrom( state );
        auto* batch = request.mutable_batch();
        batch->mutable_document()->CopyFrom( state.document() );
        batch->set_operation_id( KIID().AsStdString() );
        batch->set_document_epoch( state.revision().epoch() );
        batch->mutable_expected_revision()->CopyFrom( state.revision() );
        for( const SchematicItemOperation& operation : aOperations )
            batch->add_operations()->CopyFrom( operation );
        return request;
    }

    API_RESULT Dispatch( ApiRequest& aRequest )
    {
        ApiResponse response;
        response.mutable_status()->set_status( ApiStatusCode::AS_OK );
        if( aRequest.message().Is<ReadDocumentLifecycleState>() )
        {
            response.mutable_message()->PackFrom( state );
            return response;
        }
        if( !aRequest.message().Is<ApplySchematicItemBatch>() )
        {
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Unexpected request" );
            return tl::unexpected( error );
        }
        ++mutations;
        if( reject )
        {
            // KiCad rolled the batch back, the identity included: nothing changed.
            ApiResponseStatus error;
            error.set_status( ApiStatusCode::AS_BAD_REQUEST );
            error.set_error_message( "Atomic operation 1 rejected: scripted failure" );
            return tl::unexpected( error );
        }
        state.mutable_revision()->set_sequence( state.revision().sequence() + 1 );
        state.set_state_sha256( std::string( 64, 'd' ) );
        if( !identityAfter.empty() )
            state.set_native_identity( identityAfter );
        SchematicItemBatchResult result;
        result.mutable_revision()->CopyFrom( state.revision() );
        result.set_screen_identity_changed( claimChanged );
        response.mutable_message()->PackFrom( result );
        return response;
    }

    CheckedSchematicBatchReceipt Apply( const CheckedSchematicBatch& aRequest )
    {
        ApiRequest envelope;
        envelope.mutable_message()->PackFrom( aRequest );
        auto response = controller.Handle( envelope, process, [this]( ApiRequest& aValue ) { return Dispatch( aValue ); } );
        BOOST_REQUIRE( response );
        CheckedSchematicBatchReceipt receipt;
        BOOST_REQUIRE( response->message().UnpackTo( &receipt ) );
        return receipt;
    }
};


SchematicItemOperation adoptIdentity( const std::string& aIdentity )
{
    SchematicItemOperation operation;
    operation.mutable_rebuild_screen_identity()->set_value( aIdentity );
    return operation;
}


SchematicItemOperation titleEdit()
{
    SchematicItemOperation operation;
    operation.mutable_set_title_block()->set_title( "Rebuilt" );
    return operation;
}
} // namespace


BOOST_AUTO_TEST_CASE( CheckedBatchesKeepTheRootIdentityUnlessARebuildAdoptsIt )
{
    const std::string saved = KIID().AsStdString();

    // The rebuild's first operation adopts the saved identity, and KiCad reports exactly that change.
    {
        IDENTITY_CONTROLLER f;
        f.identityAfter = saved;
        f.claimChanged = true;
        const auto receipt = f.Apply( f.Request( { adoptIdentity( saved ), titleEdit() } ) );
        BOOST_CHECK_EQUAL( receipt.status(), CSBS_COMPLETED );
        BOOST_CHECK_EQUAL( receipt.observed_after().native_identity(), saved );
        BOOST_CHECK( receipt.result().screen_identity_changed() );
    }

    // Must-catch: an identity other than the requested one, a change KiCad does not report, a change without an
    // identity operation, and a change requested anywhere but first are never accepted as committed.
    struct CASE { const char* what; std::vector<SchematicItemOperation> operations; std::string after; bool claimed; };
    const std::string other = KIID().AsStdString();
    for( const CASE& c : std::vector<CASE>{
                 { "another identity", { adoptIdentity( saved ) }, other, true },
                 { "unreported change", { adoptIdentity( saved ) }, saved, false },
                 { "no identity operation", { titleEdit() }, other, true },
                 { "identity not first", { titleEdit(), adoptIdentity( saved ) }, saved, true } } )
    {
        IDENTITY_CONTROLLER f;
        f.identityAfter = c.after;
        f.claimChanged = c.claimed;
        const auto receipt = f.Apply( f.Request( c.operations ) );
        BOOST_CHECK_MESSAGE( receipt.status() == CSBS_INDETERMINATE, c.what );
        BOOST_CHECK_MESSAGE( receipt.error_code() == "post_state_mismatch", c.what << ": " << receipt.error_code() );
    }

    // Precision: an identity operation KiCad found already satisfied keeps the identity, and the batch commits.
    {
        IDENTITY_CONTROLLER f;
        const std::string current = f.state.native_identity();
        const auto receipt = f.Apply( f.Request( { adoptIdentity( current ), titleEdit() } ) );
        BOOST_CHECK_EQUAL( receipt.status(), CSBS_COMPLETED );
        BOOST_CHECK_EQUAL( receipt.observed_after().native_identity(), current );
        BOOST_CHECK( !receipt.result().screen_identity_changed() );
    }

    // A batch KiCad rejects after the identity operation leaves the identity it had: a clean rejection, no result.
    {
        IDENTITY_CONTROLLER f;
        f.reject = true;
        const std::string before = f.state.native_identity();
        const auto receipt = f.Apply( f.Request( { adoptIdentity( saved ), titleEdit() } ) );
        BOOST_CHECK_EQUAL( receipt.status(), CSBS_REJECTED );
        BOOST_CHECK_EQUAL( receipt.error_code(), "native_batch_rejected" );
        BOOST_CHECK( !receipt.has_result() );
        BOOST_CHECK_EQUAL( receipt.observed_after().native_identity(), before );
        BOOST_CHECK_EQUAL( f.mutations, 1u );
    }
}


/**
 * The cost of what a tracked owner compares, on the largest demo design (vme-wren), including
 * the per-action comparison of Page Settings and of a cancelled sheet import before (the
 * whole-state tracker they used) and after (the persisted parts they now compare).  It is a
 * measurement, not a pass/fail check of behaviour, so it runs only when asked for by name: the
 * native foundation journey runs it and keeps its output with the journey evidence.
 */
BOOST_AUTO_TEST_CASE( MeasuresTrackingCostOnTheLargestDemo, *boost::unit_test::disabled() )
{
    const fs::path root = findSourceRoot();
    BOOST_REQUIRE_MESSAGE( !root.empty(), "The KiCad source tree was not found; set KICAD_SOURCE_DIR." );

    const fs::path source = root / "demos" / "vme-wren";
    BOOST_REQUIRE_MESSAGE( fs::exists( source / "vme-wren.kicad_sch" ), source.string() + " is missing." );

    // Load a copy of the design's schematic and project files, so loading never writes a lock
    // or anything else next to the checked-in demo.
    const fs::path copy = fs::temp_directory_path() / ( "kicad-tracking-cost-" + KIID().AsStdString() );
    struct CLEANUP { fs::path path; ~CLEANUP() { std::error_code error; fs::remove_all( path, error ); } } cleanup{ copy };
    fs::create_directories( copy );

    for( const fs::directory_entry& entry : fs::directory_iterator( source ) )
    {
        if( entry.path().extension() == ".kicad_sch" || entry.path().extension() == ".kicad_pro" )
            fs::copy_file( entry.path(), copy / entry.path().filename() );
    }

    SETTINGS_MANAGER           settings;
    std::unique_ptr<SCHEMATIC> schematic;
    const auto                 loading = std::chrono::steady_clock::now();

    KI_TEST::LoadSchematic( settings,
                            fs::relative( copy / "vme-wren", KI_TEST::GetEeschemaTestDataDir() ).generic_string(),
                            schematic );
    BOOST_REQUIRE( schematic );
    const double loadMs = elapsedMs( loading );

    // One whole-state capture: every screen and the project settings, as the lifecycle digest
    // and a whole-state tracker write them.
    std::vector<double> whole;
    SCH_STATE_GROUPS    captured;

    for( int sample = 0; sample < 5; ++sample )
    {
        const auto started = std::chrono::steady_clock::now();
        captured = SCH_STATE_GROUPS::Capture( *schematic );
        whole.push_back( elapsedMs( started ) );
    }

    // The largest screen alone: what one sheet costs to write.
    SCH_SCREEN* largest = nullptr;
    size_t      largestItems = 0;
    size_t      symbols = 0;

    for( const SCH_SHEET_PATH& path : schematic->Hierarchy() )
    {
        SCH_SCREEN* screen = path.LastScreen();
        size_t      count = screen ? screen->Items().size() : 0;

        if( count > largestItems )
        {
            largest = screen;
            largestItems = count;
        }
    }

    BOOST_REQUIRE( largest );
    std::vector<double> oneScreen;

    for( int sample = 0; sample < 5; ++sample )
    {
        const auto started = std::chrono::steady_clock::now();
        SCH_STATE_GROUPS::CaptureScreens( *schematic, { largest } );
        oneScreen.push_back( elapsedMs( started ) );
    }

    // The project settings and the first top-level sheet, as the Schematic Setup and the
    // simulation settings owners capture them before and after their dialogs.
    SCH_SCREEN* first = schematic->RootScreen();
    BOOST_REQUIRE( first );
    std::vector<double> settingsAndFirst;

    for( int sample = 0; sample < 5; ++sample )
    {
        const auto started = std::chrono::steady_clock::now();
        SCH_STATE_GROUPS::CaptureScreens( *schematic, { first }, true );
        settingsAndFirst.push_back( elapsedMs( started ) );
    }

    // A staged comparison of the symbol with the largest library definition: the worst single
    // symbol a Symbol Properties, Sheet Properties or tuner commit can stage.
    SCH_SYMBOL* heaviest = nullptr;
    SCH_SCREEN* heaviestScreen = nullptr;
    size_t      heaviestPins = 0;

    for( const SCH_SHEET_PATH& path : schematic->Hierarchy() )
    {
        for( SCH_ITEM* item : path.LastScreen()->Items().OfType( SCH_SYMBOL_T ) )
        {
            SCH_SYMBOL* symbol = static_cast<SCH_SYMBOL*>( item );
            ++symbols;

            if( symbol->GetLibSymbolRef() && symbol->GetLibSymbolRef()->GetPinCount() > (int) heaviestPins )
            {
                heaviest = symbol;
                heaviestScreen = path.LastScreen();
                heaviestPins = symbol->GetLibSymbolRef()->GetPinCount();
            }
        }
    }

    BOOST_REQUIRE( heaviest );
    TOOL_MANAGER        manager;
    SCH_COMMIT          commit( &manager );
    std::vector<double> staged;

    commit.Modify( heaviest, heaviestScreen );

    for( int sample = 0; sample < 5; ++sample )
    {
        const auto started = std::chrono::steady_clock::now();
        BOOST_CHECK( !commit.PersistsChange( *schematic ) );
        staged.push_back( elapsedMs( started ) );
    }

    commit.Abandon();

    // Per action, before and after.  Page Settings and a cancelled or refused sheet import used to
    // compare the whole saved state around the action (a whole-state tracker: two whole-state
    // captures); they now compare only the persisted parts they can reach.  Both comparisons are
    // timed around the same edit: a real title block change on the largest screen for Page
    // Settings, which each comparison must find, and an import that left nothing into the screen
    // with the heaviest library cache, which neither may report.
    SCH_SCREEN* heaviestCache = nullptr;
    int         heaviestCachePins = -1;

    for( const SCH_SHEET_PATH& path : schematic->Hierarchy() )
    {
        int pins = 0;

        for( const auto& [name, definition] : path.LastScreen()->GetLibSymbols() )
            pins += definition ? definition->GetPinCount() : 0;

        if( pins > heaviestCachePins )
        {
            heaviestCache = path.LastScreen();
            heaviestCachePins = pins;
        }
    }

    BOOST_REQUIRE( heaviestCache );
    const TITLE_BLOCK savedTitle = largest->GetTitleBlock();
    int               edits = 0;

    auto pageAction = [&]( SCH_TRACKED_CHANGE& aChange )
    {
        TITLE_BLOCK edited = savedTitle;
        edited.SetComment( 8, wxString::Format( wxS( "tracking cost %d" ), ++edits ) );
        largest->SetTitleBlock( edited );
        BOOST_CHECK( aChange.Complete() );
    };

    std::vector<double> pageWhole, pageParts, importWhole, importParts;

    for( int sample = 0; sample < 3; ++sample )
    {
        auto started = std::chrono::steady_clock::now();
        {
            SCH_TRACKED_CHANGE change( *schematic, "Edit Page Settings" );
            pageAction( change );
        }
        pageWhole.push_back( elapsedMs( started ) );

        started = std::chrono::steady_clock::now();
        {
            SCH_TRACKED_CHANGE change( *schematic, "Import Schematic Sheet Content" );
            BOOST_CHECK( !change.Complete() );
        }
        importWhole.push_back( elapsedMs( started ) );
    }

    for( int sample = 0; sample < 5; ++sample )
    {
        auto started = std::chrono::steady_clock::now();
        {
            SCH_TRACKED_CHANGE change( *schematic, "Edit Page Settings", SCH_PERSISTED_PARTS::PageSettings() );
            pageAction( change );
        }
        pageParts.push_back( elapsedMs( started ) );

        started = std::chrono::steady_clock::now();
        {
            SCH_TRACKED_CHANGE change( *schematic, "Import Schematic Sheet Content",
                                       SCH_PERSISTED_PARTS::SheetImport( heaviestCache ) );
            BOOST_CHECK( !change.Complete() );
        }
        importParts.push_back( elapsedMs( started ) );
    }

    largest->SetTitleBlock( savedTitle );

    // One line per measurement, read by the journey that keeps this output as evidence.
    std::cout << std::fixed << std::setprecision( 1 )
              << "tracking-cost design=vme-wren screens=" << captured.WrittenSheets().size()
              << " symbols=" << symbols << " saved_bytes=" << captured.Bytes() << " load_ms=" << loadMs << "\n"
              << "tracking-cost whole_state_capture_ms " << timing( whole ) << "\n"
              << "tracking-cost largest_screen_capture_ms " << timing( oneScreen ) << " items=" << largestItems
              << "\n"
              << "tracking-cost settings_and_first_sheet_capture_ms " << timing( settingsAndFirst )
              << " items=" << first->Items().size() << "\n"
              << "tracking-cost staged_symbol_compare_ms " << timing( staged ) << " pins=" << heaviestPins
              << "\n"
              << "tracking-cost page_settings_whole_state_compare_ms " << timing( pageWhole )
              << " (before: two whole-state captures)\n"
              << "tracking-cost page_settings_compare_ms " << timing( pageParts ) << " screens="
              << captured.WrittenSheets().size() << "\n"
              << "tracking-cost sheet_import_whole_state_compare_ms " << timing( importWhole )
              << " (before: two whole-state captures)\n"
              << "tracking-cost sheet_import_compare_ms " << timing( importParts )
              << " cache_symbols=" << heaviestCache->GetLibSymbols().size() << " cache_pins=" << heaviestCachePins
              << "\n";
    std::cout.flush();

    // A staged comparison is the point of the staged form: it must stay well below the capture,
    // and so must the restricted capture of the settings and the first sheet.  Page Settings and a
    // sheet import must now cost a small fraction of the whole-state comparison they replaced.
    BOOST_CHECK_LT( median( staged ) * 10, median( whole ) );
    BOOST_CHECK_LT( median( settingsAndFirst ), median( whole ) );
    BOOST_CHECK_LT( median( pageParts ) * 10, median( pageWhole ) );
    BOOST_CHECK_LT( median( importParts ) * 10, median( importWhole ) );
}


BOOST_AUTO_TEST_SUITE_END()

} // namespace SCH_CHANGE_TRACKING_ORACLE

#endif // EESCHEMA
