/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_RECURSIVE_DIAGRAM_CANVAS_H
#define KICAD_RECURSIVE_DIAGRAM_CANVAS_H

#include <api/common/commands/recursive_diagram_commands.pb.h>
#include <bitmaps/bitmaps_list.h>
#include <wx/panel.h>
#include <wx/string.h>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

class wxAnyButton;
class wxButton;
class wxToggleButton;

/** Drawing surface pieces of one per-level diagram: exact presentation decimals, the resolved
 * layout of a level (contract rbg-v2 section 9.2) and the canvas-edge drawing palette
 * (Design Round A1, owner decision n9f7cf92f32090daf). */
namespace RECURSIVE_DIAGRAM
{
namespace D = kiapi::automation::diagrams::v1;

/// Presentation values are kept as exact thousandths of a diagram unit (contract rbg-v2
/// section 3). Text is split at the decimal point, never parsed through binary floating point.
constexpr int64_t QUANTUM = 1000;
bool ParseUnits( const std::string& aText, int64_t& aValue );
std::string FormatUnits( int64_t aValue );

/// Shared editor text and identity helpers.
inline wxString Text( const std::string& aValue ) { return wxString::FromUTF8( aValue ); }
inline std::string Utf8( const wxString& aValue ) { return aValue.ToStdString( wxConvUTF8 ); }
std::string FreshId();
/// A native-editor change origin recorded now, at the protocol's 100 ns precision.
void EditorOrigin( D::DiagramRevisionOriginData* aOrigin, const std::string& aSummary );

struct POINT { int64_t x = 0, y = 0; };
struct RECT
{
    int64_t x = 0, y = 0, w = 0, h = 0;
    int64_t Right() const { return x + w; }
    int64_t Bottom() const { return y + h; }
    bool Contains( const POINT& aPoint ) const { return aPoint.x >= x && aPoint.x <= Right() && aPoint.y >= y && aPoint.y <= Bottom(); }
};

struct BOUNDARY { std::string id, name; };

/// A child block as the level draft shows it: a saved child (with its version) or one drawn in this draft.
struct NODE
{
    std::string id, name;
    int version = 0;
    bool isNew = false;
    std::vector<BOUNDARY> interfaces;
};

/// A root connection as the level draft shows it.
struct LINK
{
    std::string id, name;
    bool isNew = false;
    std::vector<D::DiagramEndpointBindingData> endpoints;
};

/// A port anchor as drawn: stored (PLACED) or from the deterministic fallback (FALLBACK).
struct PORT
{
    std::string blockId, interfaceId, name;
    D::DiagramPortSide side = D::DPS_RIGHT;
    int64_t offset = 0;
    POINT anchor;
    bool placed = false;
    bool boundary = false;
};

/** One level as drawn: stored placements where the level has them, otherwise the legacy grid,
 * port and path rules. Fallback positions are computed, never stored implicitly. */
class LEVEL_LAYOUT
{
public:
    LEVEL_LAYOUT( std::string aScopeId, std::vector<BOUNDARY> aScopeInterfaces, std::vector<NODE> aNodes,
                  std::vector<LINK> aLinks, const D::DiagramPresentationViewData* aView,
                  std::vector<RECT> aNoteBoxes );

    const std::string& ScopeId() const { return m_scope; }
    const std::vector<BOUNDARY>& ScopeInterfaces() const { return m_scopeInterfaces; }
    const std::vector<NODE>& Nodes() const { return m_nodes; }
    const std::vector<LINK>& Links() const { return m_links; }
    const NODE* Node( const std::string& aBlockId ) const;
    const LINK* Link( const std::string& aConnectionId ) const;
    RECT Rect( const std::string& aBlockId ) const;
    bool Placed( const std::string& aBlockId ) const;
    /// Every child port and boundary port as drawn.
    const std::vector<PORT>& Ports() const { return m_ports; }
    const PORT* Port( const std::string& aBlockId, const std::string& aInterfaceId ) const;
    /// Where a connection endpoint attaches, looking toward a peer at aPeerX (rules F2, F2a and F3).
    POINT Anchor( const D::DiagramEndpointBindingData& aEndpoint, int64_t aPeerX ) const;
    /// The drawn path from endpoint 0 to endpoint aEndpoint (rule F4).
    std::vector<POINT> Route( const LINK& aLink, int aEndpoint ) const;
    bool HasRoute( const std::string& aConnectionId, int aEndpoint ) const;
    std::optional<RECT> Frame() const { return m_frame; }
    /// Rule F3: the bounding box of the drawn children, notes and boundary anchors, expanded by 40.
    RECT FallbackFrame() const;
    /// Everything drawn, for fitting the view.
    RECT Bounds() const;
    unsigned Dormant() const { return m_dormant; }
    /// The observation's resolved_layout.
    void Report( D::ResolvedDiagramLayoutData* aOut ) const;

private:
    int64_t centreX( const D::DiagramEndpointBindingData& aEndpoint ) const;

    std::string m_scope;
    std::vector<BOUNDARY> m_scopeInterfaces;
    std::vector<NODE> m_nodes;
    std::vector<LINK> m_links;
    std::vector<RECT> m_rects;
    std::vector<bool> m_placed;
    std::vector<PORT> m_ports;
    std::vector<RECT> m_notes;
    std::vector<D::DiagramConnectionRouteData> m_routes;
    std::optional<RECT> m_frame;
    unsigned m_dormant = 0;
};

enum class TOOL { SELECT, ADD_BLOCK, CONNECT, ADD_PORT, NOTE };

/// One drawing tool button with its icon above its label: a toggle for a tool, a plain button for
/// an action. The toolbar strip and the canvas-edge palette use the same buttons (Round A1).
wxAnyButton* ToolButton( wxWindow* aParent, const wxString& aLabel, BITMAPS aBitmap, const char* aName, bool aToggle );
/// The observation name of a tool: "select", "add-block", "connect", "add-port" or "note".
const char* ToolName( TOOL aTool );

/** The canvas-edge palette (Round A1 option 2): the same drawing tools as the toolbar strip plus
 * Undo. Both entry points drive one active tool, highlighted in both. */
class TOOL_PALETTE : public wxPanel
{
public:
    struct ACTIONS
    {
        std::function<void( TOOL )> choose;
        std::function<void()> remove;
        std::function<void()> undo;
    };

    TOOL_PALETTE( wxWindow* aParent, ACTIONS aActions );
    void SetState( TOOL aActive, bool aEnabled, bool aCanDelete, bool aCanUndo );

private:
    ACTIONS m_actions;
    std::vector<std::pair<TOOL, wxToggleButton*>> m_tools;
    wxButton* m_delete;
    wxButton* m_undo;
};
}

#endif // KICAD_RECURSIVE_DIAGRAM_CANVAS_H
