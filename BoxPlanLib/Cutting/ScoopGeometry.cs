using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Geometric description of a scoop inside a box. All quantities are derived from the
// (Face, Edge, Inset, Rise) tuple and the box dimensions.
//
// Local panel coords for a host face follow FaceLayout: (U, V) with origin at the
// CCW-start corner of the face. The scoop's anchor edge is one of the four edges of
// the host face (indexed in FaceLayout.EdgesCcw order: 0=bottom, 1=right, 2=top, 3=left).
internal sealed record ScoopGeometry(
    Scoop Scoop,
    int EdgeIndex,            // 0..3 in the host face's CCW edge order
    double InsetAxisLength,   // host face's axis perpendicular to the anchor edge
    double EdgeAxisLength,    // host face's axis parallel to the anchor edge
    double WallHeight,        // the rise-axis dimension (height of the anchor wall above host)
    double Slant,             // sqrt(inset² + rise²) — laid-flat width of the scoop panel
    FaceName Anchor,          // the wall the scoop's heel joints into
    FaceName CapLow,          // cap at edge-axis V=0 (or U=0, depending on host)
    FaceName CapHigh)         // cap at edge-axis V=edgeAxisLength
{
    // True if the anchor edge is at the high end of the inset axis (U=panelU or V=panelV),
    // false if at the low end (U=0 or V=0). Determines which side of the host face is
    // narrowed by `inset`.
    public bool EdgeAtHigh => EdgeIndex == 1 || EdgeIndex == 2;

    // True if the inset axis is the host face's U axis (i.e. the anchor edge runs along V).
    public bool InsetAlongU => EdgeIndex == 1 || EdgeIndex == 3;

    public static ScoopGeometry Compute(Scoop scoop, Vec3 dims)
    {
        var host = scoop.Face;
        var edge = scoop.Edge;

        var ccw = FaceLayout.EdgesCcw(host);
        var edgeIndex = -1;
        for (var i = 0; i < ccw.Length; i++)
        {
            if (ccw[i].Neighbor == edge) { edgeIndex = i; break; }
        }
        if (edgeIndex < 0)
            throw new InvalidOperationException(
                $"Edge '{edge}' is not adjacent to face '{host}'.");

        var (panelU, panelV) = FaceLayout.PanelSize(host, dims);
        // edgeIndex 0=bottom (V=0), 1=right (U=panelU), 2=top (V=panelV), 3=left (U=0).
        // The anchor edge runs along U when edgeIndex ∈ {0, 2}, and along V when ∈ {1, 3}.
        var insetAlongU = edgeIndex == 1 || edgeIndex == 3;
        var insetAxisLength = insetAlongU ? panelU : panelV;
        var edgeAxisLength = insetAlongU ? panelV : panelU;

        var wallHeight = host switch
        {
            FaceName.Bottom or FaceName.Top => dims.Y,
            FaceName.Front or FaceName.Back => dims.Z,
            FaceName.Left or FaceName.Right => dims.X,
            _ => throw new InvalidOperationException(),
        };

        // The two caps perpendicular to the edge axis. CapLow sits at edge-axis = 0
        // on the host face (CCW-start corner side), CapHigh at edge-axis = edgeAxisLength.
        var capLow = insetAlongU
            ? ccw[0].Neighbor   // V=0 side
            : ccw[3].Neighbor;  // U=0 side
        var capHigh = insetAlongU
            ? ccw[2].Neighbor   // V=panelV side
            : ccw[1].Neighbor;  // U=panelU side

        var slant = Math.Sqrt(scoop.Inset * scoop.Inset + scoop.Rise * scoop.Rise);

        return new ScoopGeometry(
            Scoop: scoop,
            EdgeIndex: edgeIndex,
            InsetAxisLength: insetAxisLength,
            EdgeAxisLength: edgeAxisLength,
            WallHeight: wallHeight,
            Slant: slant,
            Anchor: edge,
            CapLow: capLow,
            CapHigh: capHigh);
    }
}
