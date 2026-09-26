/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef KICAD_RECURSIVE_DIAGRAM_CANVAS_H
#define KICAD_RECURSIVE_DIAGRAM_CANVAS_H

#include <api/common/commands/recursive_diagram_commands.pb.h>
#include <wx/button.h>
#include <wx/colour.h>
#include <wx/control.h>
#include <wx/panel.h>
#include <wx/sizer.h>
#include <wx/string.h>
#include <wx/tglbtn.h>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <map>
#include <optional>
#include <string>
#include <tuple>
#include <vector>

class wxDC;
class wxTextCtrl;

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

// ---- Colours that stay readable in both themes (design QA P1-1, P2-1, P2-8, P2-11) -----------------

/// The WCAG 2 contrast ratio of two colours, from 1 to 21.
double Contrast( const wxColour& aA, const wxColour& aB );
/// Whether a surface is dark (the dark theme).
bool IsDark( const wxColour& aSurface );
/// aColour with its HSL lightness moved, in steps of 1 %, away from the surfaces until it reaches aMinimum contrast
/// against every one of them (or as far as lightness allows). A colour that already reaches it is returned unchanged.
wxColour Readable( const wxColour& aColour, std::initializer_list<wxColour> aAgainst, double aMinimum );
/// A filled accent control on aSurface: a fill with its hue that stands 3:1 or more from the surface, and a label
/// colour (white where the fill allows it, otherwise near-black) with 4.5:1 or more on the fill.
struct ACCENT_FILL { wxColour fill, text; };
ACCENT_FILL AccentFill( const wxColour& aAccent, const wxColour& aSurface );

/// The canvas colours of the current theme. The accent (selection, connection preview, new-block outline, caption focus
/// ring, Connect highlight) reaches 3:1 or more against both the canvas and the selected block's fill.
struct CANVAS_COLOURS
{
    wxColour background, foreground, muted, accent, selectedFill, blockFill, handleFill;
    bool dark = false;
};
CANVAS_COLOURS CanvasColours();

// ---- Tool glyphs (design QA P2-2, P2-3; owner default n46bd6c44b42d5a61: a hollow square port) --------------

/// Keeps a text entry's text 8 DIP (aHorizontal) and 6 DIP (aVertical) inside its box (design QA P2-9). wxGTK does not set
/// margins on a multi-line entry, so on GTK they are set on its text view directly.
void PadTextBox( wxTextCtrl* aControl, int aHorizontal, int aVertical );

/// The drawing tools' monochrome glyphs, drawn at any size in any colour, so both entry points show one icon family
/// in both themes (a port is a hollow square with a short lead, like the ports on the canvas).
enum class GLYPH { SELECT, ADD_BLOCK, CONNECT, PORT, REMOVE, UNDO };
void DrawGlyph( wxDC& aDC, GLYPH aGlyph, const wxRect& aBox, const wxColour& aColour );

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
    /// The chosen or candidate facets behind "+N more" (or the marks), in facet order.
    std::vector<int> hiddenFacets;
    /// The caption text as drawn, clipped to the block's content area.
    wxRect caption;
    /// The names of the block's ports drawn inside its edge; the chips, "+N more", the marks and the link keep clear of them.
    std::vector<wxRect> portNames;
};

/// The block caption as drawn inside aInner, the block's content area in canvas pixels, with aCaptionFont: the
/// caption shortened with "…" to the content width, a third of the way down a small block and 24 pixels down a full
/// one. The text is drawn whole or not at all: an empty rectangle means the block is too small for it.
struct CAPTION { wxString text; wxRect rect; };
CAPTION BlockCaption( wxDC& aDC, const NODE& aNode, const wxRect& aInner, const wxFont& aCaptionFont );
/// The caption's text rectangle (see BlockCaption). The chips are laid out below it.
wxRect CaptionRect( wxDC& aDC, const NODE& aNode, const wxRect& aInner, const wxFont& aCaptionFont );
/// Where a saved caption-only block draws its "vN" line below aCaption with aDC's font, or an empty rectangle when the
/// whole line does not fit aInner.
wxRect VersionRect( wxDC& aDC, const wxString& aText, const wxRect& aInner, const wxRect& aCaption );
/// Lays out a block's chips inside aBox, the block's content area in canvas pixels, with aSmall, the chip font.
/// aCaption is the caption as CaptionRect placed it. aPortNames are the names of the block's ports drawn inside its
/// edge (see PortNameRect); a row that shares their height stops short of them. A chip always shows its whole
/// "Facet: value": one that does not fit its row goes behind "+N more" (design QA round 2, R2-P2-6).
BLOCK_CHIPS LayoutChips( wxDC& aDC, const NODE& aNode, const wxRect& aBox, const wxFont& aSmall, const wxRect& aCaption,
                         const std::vector<wxRect>& aPortNames = {} );
/// Where a block port on aSide at aAt names itself just inside the block edge, as KiCad labels sheet pins, with
/// aDC's font (the chip font).
wxRect PortNameRect( wxDC& aDC, const wxString& aName, D::DiagramPortSide aSide, const wxPoint& aAt );
/// The lines a canvas note shows in a text area of aWidth by aHeight pixels with aDC's font. Lines break between
/// words (only a word wider than the note breaks inside it), the note's own line breaks are kept, and text that
/// does not fit ends its last shown line with "…".
std::vector<wxString> NoteLines( wxDC& aDC, const wxString& aText, int aWidth, int aHeight );
/// What the "+N more" chip shows on hover: each choice behind it with its value and its state, one per line, for example
/// "Family: TLV755P (candidate)" (design QA round 2, R2-P2-6).
wxString MoreToolTip( const NODE& aNode, const BLOCK_CHIPS& aChips );
/// Draws the chips and link LayoutChips placed.
void DrawChips( wxDC& aDC, const BLOCK_CHIPS& aChips, const wxFont& aSmall, bool aDark,
                const wxColour& aForeground, const wxColour& aLink );
/// The state mark shared by chips and the inspector: a check for chosen, a ring for a candidate and a
/// grey dot for unknown.
void DrawChoiceMark( wxDC& aDC, const wxRect& aBox, D::DefinitionChoiceStateData aState, bool aDark );

/// A root connection as the level draft shows it.
struct LINK
{
    std::string id, name;
    bool isNew = false;
    std::vector<D::DiagramEndpointBindingData> endpoints;
    /// Its direction detail (Round A3); the canvas draws arrowheads for it.
    D::DiagramConnectionDirection direction = D::DCDR_UNSPECIFIED;
};

/// One end of a computed leg (rule F4): where it attaches, which way the leg leaves it, (±1, 0) to the right or left
/// or (0, ±1) down or up, and the child block it is on (an index into the blocks), or -1 for the level's boundary.
struct ROUTE_END
{
    POINT at;
    int dx = 0, dy = 0;
    int block = -1;
};
struct ROUTE_LEG { ROUTE_END from, to; };
/// Lays out computed legs (rule F4 as revised for design QA P2-5) against aStored, the paths already drawn, and aBlocks,
/// the level's child blocks. Returns each leg's points in order.
std::vector<std::vector<POINT>> RouteLegs( const std::vector<RECT>& aBlocks, const std::vector<std::vector<POINT>>& aStored,
                                           const std::vector<ROUTE_LEG>& aLegs );
/// An orthogonal path in canvas pixels from aStart to aGoal that passes through none of aObstacles and stays inside aArea,
/// with as few turns as it can and then as short as it can: the Connect preview's way around a block that stands between
/// the connection's ends (design QA round 2, R2-P2-2). aStartDirection is the way the path must leave aStart and
/// aGoalDirection the way it must arrive at aGoal, each (±1, 0) or (0, ±1), or (0, 0) for any way. Returns the path's
/// corners from aStart to aGoal, or nothing when no such path exists (an end inside an obstacle, or no way between).
std::vector<wxPoint> OrthogonalDetour( const std::vector<wxRect>& aObstacles, const wxPoint& aStart, const wxPoint& aStartDirection,
                                       const wxPoint& aGoal, const wxPoint& aGoalDirection, const wxRect& aArea );
/// How often the editor laid out computed paths since it started, and how long the slowest and the latest layout took.
/// A level is laid out once per change of its geometry: an unchanged level reuses its last layout.
struct ROUTE_STATS
{
    uint64_t layouts = 0;
    uint64_t slowestMicros = 0;
    uint64_t latestMicros = 0;
};
ROUTE_STATS RouteStats();

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
    /// Every child block's rectangle, in the order of Nodes().
    const std::vector<RECT>& Rects() const { return m_rects; }
    /// Which way a path leaves aEndpoint attached at aAt (see leaving), and the child block it is on (-1 for the boundary).
    std::pair<int, int> Leaving( const D::DiagramEndpointBindingData& aEndpoint, const POINT& aAt ) const { return leaving( aEndpoint, aAt ); }
    int OwnBlock( const D::DiagramEndpointBindingData& aEndpoint ) const { return ownBlock( aEndpoint ); }
    const PORT* Port( const std::string& aBlockId, const std::string& aInterfaceId ) const;
    /// Where an endpoint that is not yet part of a connection would attach, looking toward a peer at aPeerX (rules F2,
    /// F2a and F3): a port at its anchor, a block at the middle of its edge facing the peer (the Connect preview).
    POINT Anchor( const D::DiagramEndpointBindingData& aEndpoint, int64_t aPeerX ) const;
    /// Where endpoint aEndpoint of aLink attaches on the leg to endpoint aLeg (aLeg is the other end of that leg). An end on
    /// a block itself (no interface) gets its own point on the block's edge facing its peer (rule F2, design QA P2-5).
    POINT EndAnchor( const LINK& aLink, int aEndpoint, int aLeg ) const;
    /// The drawn path from endpoint 0 to endpoint aEndpoint (rule F4; computed paths keep their vertical legs apart, F4a).
    std::vector<POINT> Route( const LINK& aLink, int aEndpoint ) const;
    bool HasRoute( const std::string& aConnectionId, int aEndpoint ) const;
    /// Whether both ends of the leg from endpoint 0 to aEndpoint leave sideways (a left or right edge, or a boundary port
    /// on the frame's left or right side). Only such a leg's unlocked channel route follows its ends' heights (F4b).
    bool Sideways( const LINK& aLink, int aEndpoint ) const;
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
    /// The height the ends facing aEndpoint's peer are sorted by: the peer port's anchor, or the peer block's centre.
    int64_t referenceY( const D::DiagramEndpointBindingData& aEndpoint ) const;
    bool isBlockEnd( const D::DiagramEndpointBindingData& aEndpoint ) const;
    D::DiagramPortSide facing( const D::DiagramEndpointBindingData& aEndpoint, const D::DiagramEndpointBindingData& aPeer ) const;
    /// Which way a path leaves aEndpoint attached at aAt (rules F4c and F4d): out of a child block's side, into the level
    /// from a boundary port, as (1, 0) right, (-1, 0) left, (0, 1) down or (0, -1) up.
    std::pair<int, int> leaving( const D::DiagramEndpointBindingData& aEndpoint, const POINT& aAt ) const;
    /// The index of the child block aEndpoint is on, or -1 for a boundary end.
    int ownBlock( const D::DiagramEndpointBindingData& aEndpoint ) const;
    void placeBlockEnds();
    void placePaths();

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
    /// Where each end on a block itself attaches: (connection, endpoint index, side) to its point (rule F2).
    std::map<std::tuple<std::string, int, int>, POINT> m_blockEnds;
    /// Each leg's drawn path: (connection, endpoint index) to its points (rules F4 and F4a).
    std::map<std::pair<std::string, int>, std::vector<POINT>> m_paths;
};

enum class TOOL { SELECT, ADD_BLOCK, CONNECT, ADD_PORT, NOTE };

/// The observation name of a tool: "select", "add-block", "connect", "add-port" or "note".
const char* ToolName( TOOL aTool );

enum class TOOL_STYLE { STRIP, PALETTE };

/// How a drawing tool looks: its glyph above its label, in the toolbar strip (sketch 1) or in a palette cell (sketch 2).
/// The strip and the palette share it, so both entry points show one icon family and the active tool the same clear way
/// (design QA P2-1 to P2-3): in the strip a pale accent tile with an accent border, glyph and label, in the palette a
/// solid accent cell.
struct TOOL_FACE
{
    wxString label;
    GLYPH glyph;
    TOOL_STYLE style;
    wxSize BestSize( const wxWindow* aWindow ) const;
    /// The glyph's box and the label's text box as drawn in aWindow, in client pixels.
    wxRect GlyphRect( const wxWindow* aWindow ) const;
    wxRect LabelRect( const wxWindow* aWindow ) const;
    void Paint( wxDC& aDC, const wxWindow* aWindow, bool aActive, bool aHover, bool aDown ) const;
};

/// A platform button the editor paints itself.
class BUTTON_PAINTER
{
public:
    virtual ~BUTTON_PAINTER() = default;
    /// Paints the whole button in client pixels; aHover and aDown are the pointer's state over it.
    virtual void PaintButton( wxDC& aDC, bool aHover, bool aDown ) = 0;
};

/// On GTK, hands the drawing of aButton, a platform button, to aPainter. The button stays the platform's own, so it keeps
/// its role, its label as its name, its pressed state, its focus and its keys for assistive technology (wxGTK has no
/// wxAccessible). A press leaves the keyboard focus where it was. Elsewhere the platform draws the button.
void PaintNatively( wxWindow* aButton, BUTTON_PAINTER* aPainter );
/// The colour the toolkit draws behind aControl. On GTK that is the background of the first widget above it whose theme
/// gives it one (on Adwaita the window's own), which can differ from the colour wxWidgets reports for its parent; elsewhere,
/// the parent's background colour.
wxColour DrawnSurface( wxWindow* aControl );
/// Gives aWindow the accessible role aRole (an ATK role name such as "link" or "push button") and, when not empty, the
/// accessible name aName (GTK; elsewhere nothing changes).
void SetAccessibleRole( wxWindow* aWindow, const char* aRole, const wxString& aName );
/// What assistive technology reads from a control: its role, its name and whether it is checked (pressed).
struct ACCESSIBLE { std::string role, name; bool checked = false; };
/// The accessible role, name and checked state of aWindow as the toolkit exposes them (GTK's ATK object); none elsewhere.
std::optional<ACCESSIBLE> AccessibleOf( wxWindow* aWindow );

/** One drawing tool (one of which is active) in the toolbar strip or the canvas-edge palette (Round A1). It is the
 * platform's own toggle button, pressed with the pointer, or Space or Enter when focused, and on GTK the editor paints
 * it as TOOL_FACE describes. */
class TOOL_BUTTON : public wxToggleButton, public BUTTON_PAINTER
{
public:
    TOOL_BUTTON( wxWindow* aParent, const wxString& aLabel, GLYPH aGlyph, TOOL_STYLE aStyle, const char* aName, const wxString& aToolTip );
    const TOOL_FACE& Face() const { return m_face; }
    void PaintButton( wxDC& aDC, bool aHover, bool aDown ) override;

protected:
    wxSize DoGetBestSize() const override;

private:
    TOOL_FACE m_face;
};

/// An action beside the tools (Delete, and Undo in the palette): the platform's own push button, painted as the tools are.
class TOOL_ACTION : public wxButton, public BUTTON_PAINTER
{
public:
    TOOL_ACTION( wxWindow* aParent, const wxString& aLabel, GLYPH aGlyph, TOOL_STYLE aStyle, const char* aName, const wxString& aToolTip );
    void PaintButton( wxDC& aDC, bool aHover, bool aDown ) override;

protected:
    wxSize DoGetBestSize() const override;

private:
    TOOL_FACE m_face;
};

/** A quiet action drawn as an underlined link in the link colour (design QA P2-10: "Back to facet overview" and "Clear
 * facet"). It is the platform's own push button, announced as a link, pressed with the pointer, or Space or Enter when
 * focused. */
class LINK_BUTTON : public wxButton, public BUTTON_PAINTER
{
public:
    LINK_BUTTON( wxWindow* aParent, const wxString& aLabel, const char* aName );
    void SetLabel( const wxString& aLabel ) override;
    void PaintButton( wxDC& aDC, bool aHover, bool aDown ) override;
    /// The link colour of the current theme (#20518D on a light surface, #B8CBE1 on a dark one, or the theme's own).
    static wxColour LinkColour( const wxColour& aSurface );

protected:
    wxSize DoGetBestSize() const override;
};

/** One row of the inspector's facet overview: the facet's name and its value with its state mark. It is the platform's own
 * toggle button, pressed while its facet's detail is open, and on GTK the editor paints it. Pressing it (the pointer, or
 * Space or Enter when focused) opens that facet's detail; assistive technology reads it as a toggle button named by its
 * facet and value, pressed for the open facet (design QA P2-11 and its review). */
class FACET_ROW : public wxToggleButton, public BUTTON_PAINTER
{
public:
    FACET_ROW( wxWindow* aParent, int aFacet, std::function<void( int )> aOpen );
    void SetChoice( const D::DefinitionTextChoiceData& aChoice, bool aOpen );
    int FacetIndex() const { return m_facet; }
    void PaintButton( wxDC& aDC, bool aHover, bool aDown ) override;

protected:
    wxSize DoGetBestSize() const override;

private:
    int m_facet;
    std::function<void( int )> m_open;
    D::DefinitionChoiceStateData m_state = D::DCSD_UNSPECIFIED;
    /// The value as the row shows it ("SOT-23-5 (candidate)") and without its state suffix ("SOT-23-5").
    wxString m_value, m_bareValue;
    bool m_isOpen = false;
};

/** One one-click choice of a small fixed set: a connection's direction, domain or type (Round A3). It is the platform's own
 * toggle button, pressed while its value is the chosen one, so assistive technology reads it as a toggle button named by its
 * label and checked while chosen. On GTK the editor paints it: chosen, with the accent checked style of the drawing tools'
 * strip (a pale accent tile, an accent border at 3:1 or more and an accent label at 4.5:1 or more); idle, as a quiet framed
 * button; under the pointer, with a neutral grey fill, so hover never looks chosen (design QA round 2, R2-P2-4). */
class CHOICE_BUTTON : public wxToggleButton, public BUTTON_PAINTER
{
public:
    CHOICE_BUTTON( wxWindow* aParent, const wxString& aLabel, const wxString& aName );
    void SetLabel( const wxString& aLabel ) override;
    void PaintButton( wxDC& aDC, bool aHover, bool aDown ) override;
    /// The colours a choice is drawn in on aSurface.
    struct LOOK { wxColour fill, border, text; };
    static LOOK Look( const wxColour& aSurface, bool aChosen, bool aHover, bool aEnabled );

protected:
    wxSize DoGetBestSize() const override;
};

/** A row of one-click choices, left to right, that continues on a new line only when the next choice does not fit the
 * width it is given. The editor gives it that width before it lays the inspector out, and the lines it places are the
 * lines its minimum height counts, so whatever follows the row moves down when the row wraps and never lies over a
 * choice. (wxWrapSizer learns its width only during a layout and counts its lines for the next one, so for one layout
 * a wrapped line can lie over what follows it.) */
class CHOICE_FLOW : public wxSizer
{
public:
    explicit CHOICE_FLOW( int aGap ) : m_gap( aGap ) {}
    /// The width the choices wrap within, in pixels; 0 keeps them in one line. Returns whether it changed.
    bool SetWrapWidth( int aWidth );
    wxSize CalcMin() override;
    void RepositionChildren( const wxSize& aMinSize ) override;

private:
    /// Where each shown choice goes, relative to the row's origin, and the extent they take together.
    std::vector<std::pair<wxSizerItem*, wxRect>> arrange( wxSize& aExtent );

    int m_gap;
    int m_width = 0;
};

/** The canvas-edge palette (Round A1 option 2): the same drawing tools as the toolbar strip plus
 * Undo, on one card with one surface and one button style, and a single divider before Undo (design QA P2-4).
 * Both entry points drive one active tool, highlighted in both. */
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
    /// The palette card's surface colour.
    static wxColour Surface();

private:
    void paint();

    ACTIONS m_actions;
    std::vector<std::pair<TOOL, TOOL_BUTTON*>> m_tools;
    TOOL_ACTION* m_delete;
    TOOL_ACTION* m_undo;
};
}

#endif // KICAD_RECURSIVE_DIAGRAM_CANVAS_H
