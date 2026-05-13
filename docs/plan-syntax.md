# Plan file syntax

Plan files are YAML documents describing one or more 3D shapes that the BoxPlan
pipeline will turn into laser-cuttable parts. All measurements are in millimetres.

> **Maintainers:** this file documents the user-facing YAML schema. The
> authoritative source is [BoxPlanLib/Parsing/RawPlan.cs](../BoxPlanLib/Parsing/RawPlan.cs)
> and [BoxPlanLib/Parsing/PlanParser.cs](../BoxPlanLib/Parsing/PlanParser.cs).
> **Update this document whenever you add, rename, or remove a field, shape
> type, feature type, or enum value.**

Field names use `hyphenated-case` (the YAML deserializer applies
`HyphenatedNamingConvention`). String enum values are case-insensitive.

---

## Top level

```yaml
shapes:
  - id: "..."
    type: "..."
    # ... shape body ...
```

| Field    | Type           | Required | Description                          |
| -------- | -------------- | -------- | ------------------------------------ |
| `shapes` | list of shapes | yes      | One entry per top-level 3D shape.    |

Each shape entry must have a unique `id` and a `type` discriminator.

---

## Shape: common fields

These fields are accepted on every shape type.

| Field      | Type                                | Default              | Description                                                                                  |
| ---------- | ----------------------------------- | -------------------- | -------------------------------------------------------------------------------------------- |
| `id`       | string                              | **required**         | Unique identifier. Other shapes reference it via `inserts[].ref`.                            |
| `type`     | string                              | **required**         | Shape kind. See [Shape types](#shape-types).                                                 |
| `origin`   | `bottom-left-front` \| `center`     | `bottom-left-front`  | Interpretation of `location`.                                                                |
| `location` | `[x, y, z]` numbers                 | `[0, 0, 0]`          | Position in world coordinates.                                                               |
| `disjoint` | boolean                             | `true` if `location` is omitted, else `false` | When `true`, the shape never merges with adjacent shapes — its faces are emitted independently even if its bounding box touches another's. |
| `faces`    | list of face overrides              | all closed           | See [Faces](#faces).                                                                         |
| `dividers` | list of divider sets                | none                 | Internal partitions (box only — see [Dividers](#dividers)).                                  |
| `inserts`  | list of inserts                     | none                 | Nest other shapes inside this one. See [Inserts](#inserts).                                  |
| `features` | list of features                    | none                 | Cutouts, engravings, grids on faces. See [Features](#features). Box and panel shapes only.   |
| `fit`      | fit object                          | none                 | Auto-sized to a parent cell. See [Fit](#fit). Mutually exclusive with explicit sizing.       |

---

## Shape types

The `type` field selects which extra fields the shape accepts.

### `box`

A rectangular cuboid.

| Field        | Type                | Description                                                                             |
| ------------ | ------------------- | --------------------------------------------------------------------------------------- |
| `dimensions` | `[x, y, z]` numbers | Width, depth, height. All values must be positive.                                      |

Either `dimensions` or `fit` must be specified, but not both.

### `prism`

A prism with an arbitrary closed polygonal or curved profile, extruded along Y
(the depth axis).

| Field           | Type                       | Description                                                                            |
| --------------- | -------------------------- | -------------------------------------------------------------------------------------- |
| `depth`         | number                     | Extrusion depth (must be positive). Mutually exclusive with `fit`.                     |
| `points`        | list of `[x, z]` pairs     | Simple polygon. At least 3 points. Mutually exclusive with `segments`.                 |
| `segments`      | list of profile segments   | Polygon with optional curves. See [Profile segments](#profile-segments). At least 3.   |
| `lateral-faces` | list of lateral overrides  | Per-edge face open/closed override. See [Lateral faces](#lateral-faces).               |
| `back-size`     | number                     | Optional uniform scale factor for the back profile, centroid-aligned. `1.0` (default)  |
|                 |                            | = symmetric. `<1` = back smaller than front (frustum). `>1` = back larger.             |

Exactly one of `points` or `segments` must be provided.

### `triangle`, `pentagon`, `hexagon`

Regular 3/5/6-sided polygon fitted into a bounding box. Pointy-top for odd-sided
polygons, flat-top for even.

| Field    | Type   | Description                                                                                       |
| -------- | ------ | ------------------------------------------------------------------------------------------------- |
| `width`  | number | **Required.** X extent of the bounding box.                                                       |
| `height` | number | Z extent. Defaults to whatever preserves the regular aspect ratio for the given width.            |

Plus all `prism` extras except `points`/`segments` (`depth`, `lateral-faces`,
`back-size`, `fit`, etc.).

### `regular-polygon`

N-sided regular polygon.

| Field    | Type    | Description                                       |
| -------- | ------- | ------------------------------------------------- |
| `sides`  | integer | **Required.** Must be ≥ 3.                        |
| `width`  | number  | **Required.** X extent.                           |
| `height` | number  | Optional. Defaults to the natural aspect ratio.   |

Plus the common prism extras.

### `rectangle`

Axis-aligned rectangle.

| Field    | Type   | Description                       |
| -------- | ------ | --------------------------------- |
| `width`  | number | **Required.** Must be positive.   |
| `height` | number | **Required.** Must be positive.   |

Plus the common prism extras.

### `circle`

| Field      | Type   | Description                       |
| ---------- | ------ | --------------------------------- |
| `diameter` | number | **Required.** Must be positive.   |

Plus the common prism extras.

### `semicircle`

Flat base at the bottom, arc on top.

| Field      | Type   | Description                       |
| ---------- | ------ | --------------------------------- |
| `diameter` | number | **Required.** Must be positive.   |

Plus the common prism extras.

### `panel`

A single flat face with no connected edges — a 2D piece (sign, label, decorative
panel) that's emitted as one cuttable shape with no finger joints.

| Field     | Type            | Description                                                                                                |
| --------- | --------------- | ---------------------------------------------------------------------------------------------------------- |
| `profile` | profile object  | **Required.** A nested block using the same `type` discriminator and fields as the prism-family shapes.    |

The `profile.type` can be any of `prism`, `triangle`, `pentagon`, `hexagon`,
`regular-polygon`, `rectangle`, `circle`, `semicircle`, or `quarter-circle`, with the same
fields each of those shapes accepts (minus `depth`, `lateral-faces`, `back-size`,
`fit`). Example:

```yaml
shapes:
  - id: "name-plate"
    type: "panel"
    profile:
      type: "hexagon"
      width: 80.0
    features:
      - type: "engraving"
        text: "Alex"
        size: 12
```

Panel shapes accept `features` (cutouts, engravings, line-engravings, engraving-
grids). Features default to `face: front` and may omit the field entirely. The
only supported face name is `front`; marking it open suppresses the panel
entirely. Panels do not support `dividers`, `inserts`, or `fit`.

Panels lie in the world X-Z plane. `location` is interpreted as `[X, Y, Z]`
where the profile's width axis is X, its height axis is Z, and `Y` is the
out-of-plane coordinate. Two or more panels at the **same Y** whose world
polygons share an actual edge segment (not just a corner point) are unioned
into a single cuttable outline; features from each member panel are carried
through to the merged shape. Set `disjoint: true` to opt a panel out of merging.

### `quarter-circle`

Flat base, flat left side, arc from `(r, 0)` to `(0, r)`.

| Field    | Type   | Description                       |
| -------- | ------ | --------------------------------- |
| `radius` | number | **Required.** Must be positive.   |

Plus the common prism extras.

---

## Profile segments

Used in `prism` with `segments:`. Each entry must contain exactly **one** of
`line-to`, `arc-to`, or `bezier-to`.

```yaml
segments:
  - line-to: [100, 0]
  - arc-to: [100, 50]
    radius: 25
    clockwise: false
  - bezier-to: [0, 50]
    control-1: [80, 80]
    control-2: [20, 80]
  - line-to: [0, 0]
```

| Field       | Used by                       | Description                                                       |
| ----------- | ----------------------------- | ----------------------------------------------------------------- |
| `line-to`   | line segment                  | `[x, z]` endpoint.                                                |
| `arc-to`    | arc segment                   | `[x, z]` endpoint.                                                |
| `radius`    | arc segment                   | **Required for arcs.** Must be positive.                          |
| `clockwise` | arc segment                   | Optional, defaults to `false` (counter-clockwise).                |
| `bezier-to` | cubic Bezier segment          | `[x, z]` endpoint.                                                |
| `control-1` | cubic Bezier segment          | **Required for beziers.** First control point `[x, z]`.           |
| `control-2` | cubic Bezier segment          | **Required for beziers.** Second control point `[x, z]`.          |

The polygon is implicitly closed; the final segment should end at the start of
the first segment.

---

## Faces

Override the open/closed state of a face. Unspecified faces are `closed`.

```yaml
faces:
  - name: "top"
    type: "open"
```

| Field  | Values                                                   |
| ------ | -------------------------------------------------------- |
| `name` | `top`, `bottom`, `left`, `right`, `front`, `back`        |
| `type` | `closed` (default), `open`                               |

Prism shapes only accept `front` and `back` as cap-face names; their side
faces are configured via [`lateral-faces`](#lateral-faces) instead. Panel
shapes only accept `front`.

### Lateral faces

For prisms, the side faces are addressed by edge index (0-based, matching
`segments` order, with the implicit closing edge as the last index for
`points` polygons).

```yaml
lateral-faces:
  - index: 0
    type: "open"
```

| Field   | Description                                                 |
| ------- | ----------------------------------------------------------- |
| `index` | Edge index. Must be `0 .. (number of segments - 1)`.        |
| `type`  | `open` or `closed` (default `closed`).                      |

---

## Scoops

Internal sloped panels inside a `box`. Each scoop adds one rectangular interior
panel that sits on a host face (e.g. the bottom), rising to meet one of the four
faces adjacent to the host. Used to create trays, troughs, or hoppers where the
floor slopes inward at the sides while the external silhouette stays rectangular.

```yaml
scoops:
  - face: bottom        # host face the slope sits on
    edge: left          # which of the host face's four edges the slope's heel attaches to
    inset: 25           # how far in along the host face the slope's toe sits (mm)
    rise: 30            # how far up the heel wall the slope's top sits (mm)

  # Sugar — `edge` accepts a list for symmetric pairs
  - face: bottom
    edge: [left, right]
    inset: 25
    rise: 30

  # Sugar — `edge: all-edges` expands to all four edges of the host face
  - face: bottom
    edge: all-edges
    inset: 15
    rise: 20
```

| Field   | Type                                                                                  | Description                                                                          |
| ------- | ------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| `face`  | `top`, `bottom`, `left`, `right`, `front`, `back`                                     | **Required.** Host face the scoop sits on.                                            |
| `edge`  | edge name, list of edge names, or the keyword `all-edges`                             | **Required.** Which edge(s) of the host face the scoop's heel attaches to.            |
| `inset` | number                                                                                | **Required.** Positive distance in mm from the anchor edge along the host face.       |
| `rise`  | number                                                                                | **Required.** Positive distance in mm up the anchor wall (perpendicular to host).     |

The anchor edge must be one of the four faces adjacent to the host. `edge`'s
list and `all-edges` forms desugar at parse time into one scoop record per edge
that shares the same `inset` and `rise`.

**Validation:** `inset` and `rise` must be positive and within the host face's
inset-axis length and the anchor wall's height respectively. Two scoops on the
same `(face, edge)` pair are rejected as a duplicate. Opposing scoops on the
same axis (e.g. `left` + `right` of `bottom`) must have combined insets ≤ the
inset-axis length. At cutting time the remaining flat strip on the host face
must be ≥ material thickness.

**Current implementation status (Phase 1):** the cutting pipeline supports
scoops on the `bottom` face only; other hosts are accepted by the parser but
throw `NotImplementedException` during cutting. Scoop panels are currently
emitted as plain rectangles — joinery (toe joint to host, heel slot on the
anchor wall, oblique slots on the perpendicular caps) is on the roadmap and
should be hand-finished in CAD for now. Opposing scoops whose toes meet exactly
also throw `NotImplementedException`.

---

## Dividers

Internal partitions inside a `box`. Each entry is either an axis+positions
specification, or a `split` shortcut that fills evenly.

```yaml
dividers:
  - axis: "x"
    positions: [50, 100]
    facing: "front"
  - split:
      x: 3
      y: 2
```

| Field       | Description                                                                                |
| ----------- | ------------------------------------------------------------------------------------------ |
| `axis`      | `x`, `y`, or `z`. Required unless `split` is used.                                         |
| `positions` | List of positions along the axis, in the parent's interior (0 < p < axis length).          |
| `split`     | Object with optional `x`, `y`, `z` integers — number of equally-sized cells along that axis (≥ 2). Requires explicit parent `dimensions`. |
| `facing`    | Optional face name. Indicates which face the divider's "front" points toward.              |

A divider entry must specify either `split` or `axis`+`positions`, not both.

---

## Inserts

Place another shape inside this shape's grid cells (created by dividers) or
across an open face.

```yaml
inserts:
  - ref: "drawer"
    fill: "all-cells"
  - ref: "label"
    cell: [0, 0, 0]
  - ref: "lid"
    fill: "entire-face"
```

| Field    | Description                                                                                                                                                                                |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `ref`    | **Required.** ID of a top-level shape to insert.                                                                                                                                           |
| `cell`   | `[col, row]` or `[col, row, layer]`. Address inside the divider grid. Mutually exclusive with `fill`.                                                                                      |
| `fill`   | `all-cells` (one insert per cell) or `entire-face` (single insert sized to the parent's open face). Mutually exclusive with `cell`. `entire-face` requires the parent to have an open face. |
| `inline` | *Not yet supported.* Defining a shape inline rather than referencing one by ID.                                                                                                            |

---

## Fit

Auto-size a shape to a parent cell instead of specifying explicit dimensions.

```yaml
fit:
  mode: "cell"
  clearance: 0.2
  width: auto
  height: auto - 1
  depth: 25
```

| Field       | Description                                                                                                                       |
| ----------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `mode`      | **Required.** Currently only `cell` is supported.                                                                                  |
| `clearance` | Number. Optional gap subtracted from the auto-sized dimensions. Defaults to `0`.                                                   |
| `width`     | Dimension expression — see below. Defaults to `auto`.                                                                              |
| `height`    | Dimension expression. Defaults to `auto`.                                                                                          |
| `depth`     | Dimension expression. Defaults to `auto`.                                                                                          |

A dimension expression is one of:

- `auto` — derive from the parent cell.
- `auto + N` or `auto - N` — derived size plus or minus a fixed offset.
- A bare number — fixed size, overriding the parent cell entirely.

Either explicit sizing (`dimensions` / `depth`) or `fit` must be specified, but
not both.

---

## Features

Cutouts, engravings, and grids applied to a specific face. Supported on `box`
and `panel` shapes. For panels, `face:` defaults to `front` if omitted.

Every feature has these common fields:

| Field      | Description                                                              |
| ---------- | ------------------------------------------------------------------------ |
| `type`     | **Required.** Discriminator — see types below.                            |
| `face`     | **Required.** Face name (must be a valid face for the shape).             |
| `position` | Optional. Anchor + offset on the face. See [Position](#position).         |

### `type: cutout`

A hole cut all the way through the face.

| Field      | Description                                                                                       |
| ---------- | ------------------------------------------------------------------------------------------------- |
| `shape`    | **Required.** `circle`, `semicircle`, or `rectangle`.                                              |
| `diameter` | Sets width = height = diameter. Mutually exclusive with `width`+`height`.                          |
| `width`    | Required together with `height` (unless `diameter` is used).                                       |
| `height`   | Required together with `width`.                                                                    |
| `repeat`   | Optional array block. See [Repeat](#repeat).                                                       |

#### Repeat

Replicate a cutout into a 1D array along the face.

```yaml
- type: "cutout"
  face: "left"
  shape: "rectangle"
  width: 3.1
  height: 6
  position:
    anchor: "top-center"
    offset: [0, -3]
  repeat:
    spacing: [6, 0]
```

| Field     | Description                                                                                                  |
| --------- | ------------------------------------------------------------------------------------------------------------ |
| `spacing` | **Required.** `[u, v]` step between consecutive cutout placements in face-local coordinates. Must be non-zero.  |

When a `position` is present, its anchor refers to the matching point on the cutout's bounding box: for example, `top-right` aligns the cutout's top-right to the face's top-right before applying `offset`. The pipeline then walks in both `+spacing` and `-spacing` directions, emitting copies until the next copy's bounding box would cross the safe inner zone — the panel inset by one material thickness on all sides, which keeps cutouts clear of finger-joint tabs on adjacent edges.

### `type: engraving`

Text engraved on the face.

If `position` is omitted, text is centered on the face. If `position` is provided,
the resolved point uses the stated face anchor and offset, and the text aligns to
that anchor (`left-center` starts at the point, `right-center` ends at it,
`top-center` and `bottom-center` align vertically to it).

| Field   | Description                                                                                  |
| ------- | -------------------------------------------------------------------------------------------- |
| `text`  | **Required.** Non-empty string.                                                              |
| `size`  | **Required.** Font height in mm.                                                             |
| `font`  | Optional font family. Defaults to `sans-serif`.                                              |
| `style` | Optional space-separated styles. Recognised values: `bold`, `italic` (combinable).            |

### `type: line-engraving`

A shape engraved as line work (no fill).

If `position` is provided, its anchor refers to the matching point on the engraving shape's bounding box, the same way cutout anchors do.

| Field      | Description                                                                  |
| ---------- | ---------------------------------------------------------------------------- |
| `shape`    | **Required.** `circle`, `semicircle`, or `rectangle`.                         |
| `diameter` | Sets width = height = diameter. Mutually exclusive with `width`+`height`.    |
| `width`    | Required together with `height` (unless `diameter` is used).                  |
| `height`   | Required together with `width`.                                               |

### `type: engraving-grid`

A square grid engraved across the entire face.

| Field       | Description                                                                                  |
| ----------- | -------------------------------------------------------------------------------------------- |
| `cell-size` | **Required.** Grid spacing in mm. Must be positive.                                          |
| `center`    | `space` (default) — center the gridlines, `corner` — start in the corner, `maximize` — fit as many cells as possible. |

### Position

```yaml
position:
  anchor: "center"
  offset: [10, -5]
```

| Field    | Values                                                                          |
| -------- | ------------------------------------------------------------------------------- |
| `anchor` | `top-left`, `top-center`, `top-right`, `left-center`, `center`, `right-center`, `bottom-left`, `bottom-center`, `bottom-right` |
| `offset` | `[u, v]` in face-local coordinates. Defaults to `[0, 0]`.                       |

---

## Worked examples

A hexagonal tray open at the front:

```yaml
shapes:
  - id: "hex-tray"
    type: "hexagon"
    width: 150.0
    depth: 33.0
    faces:
      - name: "front"
        type: "open"
```

A frustum-style tray (back smaller than front):

```yaml
shapes:
  - id: "frustum-tray"
    type: "hexagon"
    width: 150.0
    depth: 30.0
    back-size: 0.7
    faces:
      - name: "front"
        type: "open"
```

A box with a 3×2 grid of drawers fitted to each cell:

```yaml
shapes:
  - id: "frame"
    type: "box"
    dimensions: [300, 200, 100]
    dividers:
      - split:
          x: 3
          y: 2
    inserts:
      - ref: "drawer"
        fill: "all-cells"

  - id: "drawer"
    type: "box"
    fit:
      mode: "cell"
      clearance: 0.5
      depth: auto - 2
    faces:
      - name: "front"
        type: "open"
```
