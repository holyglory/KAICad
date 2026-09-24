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
class wxDC;
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
    /// Its component choices as the draft holds them (Round A4).
    D::BlockDefinitionData definition;
};

// ---- Component choices (Round A4, owner decision n0b2a908b00e78823) ------------------------------
// The seven independent text facets of a block's definition (decision na7aa99408263431e). A facet
// "has a value" once its state is anything but unspecified; only those are shown anywhere.

constexpr int FACETS = 7;
/// The observation name of a facet: "purpose", "type", "manufacturer", "family", "model",
/// "orderable-part" or "package".
const char* FacetName( int aFacet );
wxString FacetLabel( int aFacet );
/// The facet's choice, or nullptr when the definition does not record it.
const D::DefinitionTextChoiceData* Facet( const D::BlockDefinitionData& aDefinition, int aFacet );
D::DefinitionTextChoiceData* MutableFacet( D::BlockDefinitionData* aDefinition, int aFacet );
void ClearFacet( D::BlockDefinitionData* aDefinition, int aFacet );
/// Whether the facet has a value (any state but unspecified).
bool HasValue( const D::DefinitionTextChoiceData* aChoice );
/// Whether the definition records nothing at all (no facet and no knowledge class).
bool EmptyDefinition( const D::BlockDefinitionData& aDefinition );
/// The value text of a choice: the chosen value, the candidates joined by aJoin, or "Unknown".
wxString ChoiceValue( const D::DefinitionTextChoiceData& aChoice, const wxString& aJoin );

/// One chip drawn on a block, in canvas pixels.
struct CHIP
{
    int facet = 0;
    D::DefinitionChoiceStateData state = D::DCSD_UNSPECIFIED;
    wxString text;
    wxRect rect;
};

/// A chosen or candidate facet shown only as its state mark beside the caption, in canvas pixels.
struct CHOICE_MARK
{
    int facet = 0;
    D::DefinitionChoiceStateData state = D::DCSD_UNSPECIFIED;
    wxRect rect;
};

/// What a block shows below its caption: one chip per chosen or candidate facet as far as they fit,
/// a "+N more" chip for the rest, and the Review facets link. A caption-only block shows none of it.
struct BLOCK_CHIPS
{
    bool shown = false;
    std::vector<CHIP> chips;
    /// Chosen or candidate facets without a chip; "+N more" or the state marks stand for them.
    unsigned hidden = 0;
    std::optional<wxRect> more;
    std::optional<wxRect> link;
    /// When no chip fits below the caption (a small or zoomed-out block), the hidden chips' state marks
    /// beside the caption, so the block still shows that it has choices.
    std::vector<CHOICE_MARK> marks;
    /// The caption text as drawn, clipped to the block's content area.
    wxRect caption;
};

/// The block caption's text rectangle inside aInner, the block's content area in canvas pixels, drawn with
/// aCaptionFont and clipped to aInner. The canvas draws the caption there and the chips are laid out below it.
wxRect CaptionRect( wxDC& aDC, const NODE& aNode, const wxRect& aInner, const wxFont& aCaptionFont );
/// Lays out a block's chips inside aBox, the block's content area in canvas pixels, with aSmall, the chip font.
/// aCaption is the caption as CaptionRect placed it.
BLOCK_CHIPS LayoutChips( wxDC& aDC, const NODE& aNode, const wxRect& aBox, const wxFont& aSmall, const wxRect& aCaption );
/// Draws the chips and link LayoutChips placed.
void DrawChips( wxDC& aDC, const BLOCK_CHIPS& aChips, const wxFont& aSmall, bool aDark,
                const wxColour& aForeground, const wxColour& aLink );
/// The state mark shared by chips and the inspector: a check for chosen, a ring for a candidate and a
/// grey dot for unknown.
void DrawChoiceMark( wxDC& aDC, const wxRect& aBox, D::DefinitionChoiceStateData aState, bool aDark );

/** One row of the inspector's facet overview: the facet's name and its value with its state mark.
 * Pressing it (click, Enter or Space) opens that facet's detail. */
class FACET_ROW : public wxWindow
{
public:
    FACET_ROW( wxWindow* aParent, int aFacet, std::function<void( int )> aOpen );
    void SetChoice( const D::DefinitionTextChoiceData& aChoice, bool aOpen );
    int FacetIndex() const { return m_facet; }
    bool AcceptsFocus() const override { return IsShown() && IsEnabled(); }

private:
    void paint();

    int m_facet;
    std::function<void( int )> m_open;
    D::DefinitionChoiceStateData m_state = D::DCSD_UNSPECIFIED;
    wxString m_value;
    bool m_isOpen = false, m_hover = false;
};

/// A root connection as the level draft shows it.
struct LINK
{
    std::string id, name;
    bool isNew = false;
    std::vector<D::DiagramEndpointBindingData> endpoints;
    /// Its direction detail (Round A3); the canvas draws arrowheads for it.
    D::DiagramConnectionDirection direction = D::DCDR_UNSPECIFIED;
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
    /// The caption position a stored route names, if it names one.
    std::optional<POINT> RouteLabel( const std::string& aConnectionId, int aEndpoint ) const;
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
