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
//  - a SCH_TRACKED_CHANGE is declared without review, or is restricted to screens without a
//    reason (every other tracker compares the same whole state as the lifecycle digest);
//  - a schematic writer gains or loses a pinned persisted field, or a pinned writer group has
//    no owner (the commit machinery or a reviewed direct owner);
//  - a proven owner loses its rendered journey step, or complete tracking is claimed while
//    owners are pending or routed but not yet proven end to end.
//
// Owners that change no persisted schematic state are an explicit, reviewed allow-list.
//
// The oracle only reads the source tree.  It links nothing from eeschema, so the focused
// journal check also compiles it (see qa/tests/common/test_document_change_journal.cpp).

#include <boost/test/unit_test.hpp>

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

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
const std::string JOURNEY = "automation/tests/KiCad.Automation.Tests/NativeEventJourney.cs";


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
        // Schematic editor frame and its tools.
        { "eeschema/annotate.cpp", "SCH_EDIT_FRAME::AnnotateSymbols", "", 1, DISPOSITION::ROUTED_BY_CALLERS,
          { G_SYMBOL, G_FIELD }, {}, "Annotation stages into the caller's commit; every caller pushes it." },
        { "eeschema/eeschema_config.cpp", "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "", 1,
          DISPOSITION::ROUTED_UNLESS_RECORDED, { G_PROJECT },
          { { "eeschema/eeschema_config.cpp", "SCH_EDIT_FRAME::ShowSchematicSetupDialog",
              "if(Schematic().ChangeJournal().Sequence()==beforeRevision){Schematic().RecordCommittedChange(" } },
          "Compares the project settings around Schematic Setup; an accepted change is recorded unless a "
          "commit inside the dialog already recorded it, then the document is marked modified." },
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
        { "eeschema/tools/sch_drawing_tools.cpp", "SCH_DRAWING_TOOLS::doSyncSheetsPins", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_SHEET, G_TEXT }, {}, "Each synchronised pin edit is its own commit." },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::EditProperties", "m_frame->", 2,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD, G_SHEET, G_INSTANCES, G_EMBEDDED, G_LIB_CACHE }, {},
          "Field placement after Symbol Properties and the sheet file, annotation and hierarchy changes "
          "after Sheet Properties are tracked against the persisted state." },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::Swap", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_FIELD, G_TEXT, G_LINE }, {},
          "A local commit is pushed first; otherwise the swap is staged in the caller's commit." },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::SwapPins", "m_frame->", 1, DISPOSITION::ROUTED,
          { G_FORMAT, G_SYMBOL, G_LINE, G_JUNCTION }, {},
          "A local commit is pushed first; otherwise the swap is staged in the caller's commit." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::PageSetup", "m_frame->", 1,
          DISPOSITION::ROUTED, { G_FORMAT, G_TITLE, G_PAGE, G_EMBEDDED, G_PROJECT }, {},
          "Accepted page settings record a committed change before the document is marked modified." },
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
        { "eeschema/dialogs/dialog_annotate.cpp", "DIALOG_ANNOTATE::~DIALOG_ANNOTATE", "schFrame->", 1,
          DISPOSITION::ROUTED, { G_PROJECT }, {}, "Changed annotation settings record a committed change." },
        { "eeschema/dialogs/dialog_erc.cpp", "DIALOG_ERC::ExcludeMarker", "m_parent->", 1, DISPOSITION::ROUTED,
          { G_PROJECT }, {}, "ERC exclusions record a committed change." },
        { "eeschema/dialogs/dialog_erc.cpp", "DIALOG_ERC::OnERCItemRClick", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_PROJECT }, {}, "ERC overrides record a committed change." },
        { "eeschema/dialogs/dialog_erc.cpp", "DIALOG_ERC::OnIgnoredItemRClick", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_PROJECT }, {}, "ERC severities record a committed change." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::TransferDataFromWindow", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD, G_PROJECT }, {}, "Field and BOM edits are pushed as a commit." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onDeleteVariant", "m_parent->", 1, DISPOSITION::ROUTED,
          { G_SYMBOL, G_SHEET, G_PROJECT }, {}, "Variant deletion is pushed as a commit." },
        { SYMBOL_FIELDS_DIALOG, "DIALOG_SYMBOL_FIELDS_TABLE::onVariantSelectionChange", "m_parent->", 1,
          DISPOSITION::ROUTED, { G_SYMBOL, G_FIELD }, {},
          "Pending field edits are pushed as a commit before switching variants." },
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
          { { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::PageSetup",
              "DIALOG_EESCHEMA_PAGE_SETTINGSdlg(" },
            { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::PageSetup", "RecordCommittedChange(" } },
          "The schematic editor opens the shared page dialog only from PageSetup, which records the "
          "accepted change; other editors own other documents." },
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
        { "eeschema/sim/simulator_frame.cpp", "SIMULATOR_FRAME::EditAnalysis", "", 1, DISPOSITION::PENDING,
          { G_PROJECT }, {},
          "The analysis dialog edits the project's live ngspice settings; only the workbook is marked "
          "modified and no journal change is recorded until those settings are tracked.",
          40 },
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
          { { "eeschema/dialogs/dialog_annotate.cpp", "DIALOG_ANNOTATE::OnAnnotateClick", 1,
              CALLER_RULE::STAGED_COMMIT, {} },
            { drawing, "SCH_DRAWING_TOOLS::ImportSheet", 2, CALLER_RULE::STAGED_COMMIT, {} },
            { drawing, "SCH_DRAWING_TOOLS::DrawSheet", 2, CALLER_RULE::STAGED_COMMIT, {} },
            { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::RepeatDrawItem", 1, CALLER_RULE::STAGED_COMMIT,
              {} } },
          "Annotation edits the caller's commit." },
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


/// A reviewed SCH_TRACKED_CHANGE declaration site.  Every tracker compares the whole persisted
/// state (every screen and the project settings, the same groups as the lifecycle digest)
/// unless it is restricted to named screens, which needs a reason.
struct TRACKER_SITE
{
    std::string file;
    std::string function;
    int         wholeState;   ///< Two-argument declarations.
    int         screens;      ///< Four-argument, screen-restricted declarations.
    std::string reason;       ///< Why the restricted trackers cannot miss a change.
};


inline std::vector<TRACKER_SITE> reviewedTrackers()
{
    return {
        { SYMBOL_DIALOG, "DIALOG_SYMBOL_PROPERTIES::TransferDataFromWindow", 1, 0, "" },
        { "eeschema/dialogs/dialog_symbol_remap.cpp", "DIALOG_SYMBOL_REMAP::OnRemapSymbols", 1, 0, "" },
        { "eeschema/dialogs/dialog_update_from_pcb.cpp", "DIALOG_UPDATE_FROM_PCB::OnUpdateClick", 1, 0, "" },
        { "eeschema/sim/simulator_frame_ui.cpp", "SIMULATOR_FRAME_UI::UpdateTunerValue", 1, 0, "" },
        { "eeschema/tools/assign_footprints.cpp", "SCH_EDITOR_CONTROL::ImportFPAssignments", 1, 0, "" },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::rescueProject", 1, 0, "" },
        { "eeschema/tools/sch_edit_tool.cpp", "SCH_EDIT_TOOL::EditProperties", 1, 1,
          "Field autoplacement after Symbol Properties moves only that symbol's fields, which are saved with "
          "the current screen; everything the dialog changed is tracked by the dialog's whole-state tracker." },
        { "eeschema/widgets/hierarchy_pane.cpp", "HIERARCHY_PANE::onRightClick", 2, 0, "" },
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
        { "eeschema/dialogs/dialog_annotate.cpp", "DIALOG_ANNOTATE::~DIALOG_ANNOTATE", ANY_ROUTE, 50,
          "Changing and closing the Annotate Schematic options." },
        { "eeschema/tools/sch_editor_control.cpp", "SCH_EDITOR_CONTROL::PageSetup", ANY_ROUTE, 40,
          "An unchanged Page Settings OK still records a revision and an undo entry, because the shared page "
          "dialog marks the screen modified; needs no-op precision." },
        { "eeschema/eeschema_config.cpp", "SCH_EDIT_FRAME::ShowSchematicSetupDialog", "RecordCommittedChange(", 30,
          "Schematic Setup compares only the project settings, without file metadata, instead of the shared "
          "whole-state groups: a Setup that changes a screen outside a commit, or whose project save rewrites "
          "file metadata, changes the lifecycle digest without a revision." },
    };
}


/// Owners whose change, cancel and no-op precision the rendered journey asserts by the exact
/// journal description.
struct PROOF
{
    std::string function;
    std::string description;
};


inline std::vector<PROOF> journeyProofs()
{
    return {
        { "SCH_EDIT_TOOL::EditProperties", "Edit Symbol Properties" },
        { "SCH_EDIT_TOOL::EditProperties", "Edit Sheet Properties" },
        { "HIERARCHY_PANE::onRightClick", "New Top-Level Sheet" },
        { "HIERARCHY_PANE::onRightClick", "Delete Top-Level Sheet" },
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

} // namespace SCH_CHANGE_TRACKING_ORACLE


using namespace SCH_CHANGE_TRACKING_ORACLE;


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

    BOOST_REQUIRE_EQUAL( calls.size(), 11u );
    BOOST_REQUIRE_EQUAL( staged.size(), 2u );

    for( const char* routed : { "FRAME::Routed", "FRAME::Tracked", "FRAME::ScreenTracked", "FRAME::Received" } )
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

    BOOST_CHECK_EQUAL( trackers.size(), 3u );
    BOOST_CHECK_EQUAL( trackers["FRAME::Tracked change"], 2 );
    BOOST_CHECK_EQUAL( trackers["FRAME::ScreenTracked placement"], 4 );
    BOOST_CHECK_EQUAL( trackers["FRAME::RouteAfterCall change"], 2 );

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


BOOST_AUTO_TEST_CASE( EveryTrackedChangeIsReviewed )
{
    ORACLE_FIXTURE oracle;
    BOOST_REQUIRE( !oracle.root.empty() );

    std::map<std::string, std::pair<int, int>> found;

    for( const std::string& relative : oracle.OwnerFiles() )
    {
        for( const TRACKER& tracker : oracle.Scan( relative ).trackers )
        {
            std::pair<int, int>& counts = found[relative + " | " + tracker.function];

            BOOST_CHECK_MESSAGE( tracker.arguments == 2 || tracker.arguments == 4,
                                 relative + " line " + std::to_string( tracker.line )
                                         + " constructs a tracker with an unreviewed argument list." );

            ( tracker.arguments == 4 ? counts.second : counts.first )++;
        }
    }

    std::set<std::string> reviewed;

    for( const TRACKER_SITE& site : reviewedTrackers() )
    {
        const std::string key = site.file + " | " + site.function;
        reviewed.insert( key );

        BOOST_CHECK_MESSAGE( found[key] == std::make_pair( site.wholeState, site.screens ),
                             key + " declares " + std::to_string( found[key].first ) + " whole-state and "
                                     + std::to_string( found[key].second ) + " screen-restricted tracker(s); the "
                                     "review covers " + std::to_string( site.wholeState ) + " and "
                                     + std::to_string( site.screens ) + "." );

        BOOST_CHECK_MESSAGE( site.screens == 0 || !site.reason.empty(),
                             key + " restricts a tracker to screens without a reason." );
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

    // Every proven owner keeps its rendered journey step.
    std::string journey;
    BOOST_CHECK_MESSAGE( readFile( oracle.root / JOURNEY, journey ), JOURNEY + " is missing." );

    for( const PROOF& proof : journeyProofs() )
    {
        BOOST_CHECK_MESSAGE( journey.find( "\"" + proof.description + "\"" ) != std::string::npos,
                             proof.function + " is no longer proven by the journey step asserting \""
                                     + proof.description + "\"." );
    }

    // The journal, its notifications and the lifecycle state may claim complete tracking only
    // once nothing is pending: until then every claim must be the literal false.
    struct CLAIM
    {
        std::string file;
        std::string call;
    };

    for( const CLAIM& claim : { CLAIM{ "eeschema/schematic.cpp", "set_tracking_complete(" },
                                CLAIM{ "eeschema/api/api_handler_sch.cpp", "set_tracking_complete(" },
                                CLAIM{ "eeschema/api/api_handler_sch.cpp", "set_complete_change_tracking(" } } )
    {
        const SOURCE_SCAN& scan = oracle.Scan( claim.file );
        const std::string  code = withoutSpaces( scan.code );
        size_t             count = 0;

        for( size_t pos = code.find( claim.call ); pos != std::string::npos; pos = code.find( claim.call, pos + 1 ) )
        {
            ++count;
            std::string argument = code.substr( pos + claim.call.size(), code.find( ')', pos ) - pos - claim.call.size() );

            BOOST_CHECK_MESSAGE( pending == 0 || argument == "false",
                                 claim.file + " claims complete tracking (" + argument + ") while "
                                         + std::to_string( pending ) + " owner(s) are pending or unproven." );
        }

        BOOST_CHECK_MESSAGE( count > 0, claim.file + " no longer reports " + claim.call + ")." );
    }
}


BOOST_AUTO_TEST_SUITE_END()
