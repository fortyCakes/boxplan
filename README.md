# BoxPlan

BoxPlan is a .NET tool for designing laser-cut boxes. You describe 3D shapes in YAML — boxes with dividers, drawers, engravings, and joinery — and BoxPlan produces sheet-laid-out SVG files ready to send to a laser cutter.

```
boxplan my-box.yml output/
```

## Projects

| Project | Description |
|---|---|
| `BoxPlanLib` | Core library: parse plans, compute cuttable shapes, generate SVGs |
| `BoxPlanLib.Cli` | Command-line tool wrapping the library |
| `BoxPlanLib.Tests` | Unit tests |

## Building

```sh
dotnet build
dotnet run --project BoxPlanLib.Cli -- my-plan.yml
```

---

## CLI

### Quick start

```sh
# Single plan file → SVG pages in output/
boxplan my-box.yml output/

# Whole directory of plans → combined SVG pages
boxplan plans/my-project/ output/

# No arguments → process sample-plans/ into sample-output/
boxplan
```

### Subcommands

The default (`plan`) runs the full pipeline in one pass. The other subcommands expose intermediate steps for debugging or scripting.

| Subcommand | Input | Output | Description |
|---|---|---|---|
| `plan` (default) | `.yml` or directory | `.svg` files | Full pipeline: parse → cut → layout → SVG |
| `parse` | `.yml` | `.bpl` | Parse YAML to validated plan |
| `cut` | `.bpl` | `.cut.json` | Compute laser-cuttable shapes |
| `layout` | `.cut.json` | `.svg` files | Lay out pieces on sheets, generate SVG |
| `optimise` | `.yml` or directory | `.svg` files | Full pipeline with sheet-count optimisation |

```sh
boxplan parse my-box.yml my-box.bpl
boxplan cut my-box.bpl my-box.cut.json
boxplan layout my-box.cut.json output/

boxplan optimise plans/ output/ --max-rounds 100
```

### Settings

Material and joinery settings can come from three places (later sources override earlier ones):

1. Built-in defaults
2. `.boxplansettings` file in the working directory
3. CLI flags

**Settings file** (`.boxplansettings`, YAML):

```yaml
sheet-width: 400          # mm
sheet-height: 300         # mm
margin: 5.0               # mm, border around each page
kerf: 0.1                 # mm, laser beam width (used for compensation)
material-thickness: 3.0   # mm
finger-joint-size: 5.0    # mm, tab width
spacing: 1.0              # mm, gap between pieces on sheet
use-advanced-layout-optimizer: true
embed-raster-engravings: false
vectorize-raster-engravings: false
debug: false
labels: false
```

**CLI flags** (override settings file):

```sh
boxplan my-box.yml --sheet-width 500 --sheet-height 400 --kerf 0.05
boxplan my-box.yml --material-thickness 3 --finger-joint-size 6
boxplan my-box.yml --debug --labels         # show debug outlines and shape IDs
boxplan my-box.yml --no-debug               # disable boolean flag
```

**Other flags:**

```
--settings <path>       Load settings from a specific file
--input-dir <path>      Process all .yml/.yaml files in a directory
--no-settings-file      Ignore .boxplansettings
--save-settings         Write the resolved settings to .boxplansettings
--help, -h              Show help
```

**Optimise-specific flags:**

```
--patience <n>          Stop after n rounds with no improvement (default: 5)
--batch-size <n>        Settings candidates per round (default: 20)
--max-rounds <n>        Maximum rounds (default: 50)
--scrap-value <f>       Score penalty for leftover sheet area (default: 0.5)
--min-scrap-size <mm>   Minimum offcut size to penalise (default: 50)
```

---

## Plan file format

Plans are YAML files describing one or more 3D shapes. All measurements are in millimetres. Field names use `hyphenated-case`.

```yaml
shapes:
  - id: "my-box"
    type: "box"
    dimensions: [150, 100, 80]   # [width, depth, height]
```

For the full schema reference see [docs/plan-syntax.md](docs/plan-syntax.md).

### Shape types

| Type | Description |
|---|---|
| `box` | Rectangular cuboid — the most common type |
| `prism` | Arbitrary polygon profile extruded on the Y axis |
| `panel` | Single 2D face (no joinery) |
| `circle`, `semicircle`, `rectangle`, `quarter-circle` | Flat primitives with a depth |
| `triangle`, `pentagon`, `hexagon`, `regular-polygon` | Regular polygon prisms |

### Box with dividers and a drawer

```yaml
shapes:
  - id: "tray"
    type: "box"
    dimensions: [200, 150, 60]
    dividers:
      - split: { x: 3, y: 2 }     # 3×2 grid of cells

  - id: "drawer"
    type: "box"
    dimensions: [60, 140, 50]
    fit:
      mode: "cell"
      clearance: 0.3               # 0.3 mm gap
      width: auto
      height: auto
      depth: auto - 5              # 5 mm shorter than cell

  - id: "frame"
    type: "box"
    dimensions: [200, 150, 60]
    faces: { front: open }
    inserts:
      - ref: "drawer"
        fill: "all-cells"
```

### Features (cutouts and engravings)

```yaml
- id: "lid"
  type: "box"
  dimensions: [200, 150, 20]
  features:
    - face: "top"
      type: "raster-engraving"
      source: "logo.png"
      width: 120
      position:
        anchor: "center"

    - face: "front"
      type: "cutout"
      shape: "edge-dip"            # finger-lift cutout
      width: 40
      inner-radius: 8

    - face: "bottom"
      type: "engraving-grid"
      cell-size: 10

    - face: "front"
      type: "engraving"
      text: "My Box"
      size: 10
      position:
        anchor: "top-center"
        offset: [0, -5]
```

### Scoops (interior sloped panels)

```yaml
- id: "token-tray"
  type: "box"
  dimensions: [80, 60, 30]
  scoops:
    - face: "front"
      height: 20                   # scoop base height from bottom
```

### Split cuts (lid/base separation)

```yaml
- id: "box-with-lid"
  type: "box"
  dimensions: [150, 100, 80]
  features:
    - face: "front"
      type: "split-cut"
      height: 40                   # baseline height of the cut
      amplitude: 8                 # optional vertical variation
```

---

## SVG output

Each page is an SVG file sized to the sheet dimensions. Colours follow a convention that maps to typical laser cutter operation order:

| Colour | Meaning | Operation |
|---|---|---|
| Black | Line engravings, text | Engrave first |
| Red | Perimeter outline | Cut last |
| Blue | Interior cuts (finger joints, dividers) | Cut before outline |
| Purple | Text / vector engravings rendered as paths | Engrave |

When `--labels` is set, shape IDs are rendered in green for debugging.

---

## Library API

Reference `BoxPlanLib` from your .NET project and use `BoxPlanLib.BoxPlanLib`:

```csharp
using BoxPlanLib;

var lib = new BoxPlanLib.BoxPlanLib();

// Parse YAML
var result = lib.ParsePlan(yamlString);
if (!result.Success)
{
    foreach (var err in result.Errors) Console.Error.WriteLine(err);
    return;
}

// Configure material settings
var settings = new BoxPlanSettings
{
    MaterialThickness = 3.0,
    Kerf = 0.1,
    FingerJointSize = 6.0,
    SheetWidth = 400,
    SheetHeight = 300,
};

// Compute cuttable pieces
var shapes = lib.GetCuttableShapes(result.Value!, settings);

// Generate SVG
string svg = lib.GenerateSimpleSVG(shapes, settings);

// Or generate paginated SVG (one string per page)
IReadOnlyList<string> pages = lib.GeneratePagedSVGPages(shapes, settings);

// Measure layout efficiency (useful for optimisation loops)
var (pageCount, score) = lib.MeasureLayout(shapes, settings);
```

### `BoxPlanCuttableShape`

Each shape returned by `GetCuttableShapes` represents one laser-cuttable piece:

```csharp
public sealed class BoxPlanCuttableShape
{
    public string Id { get; }
    public Vec2 BoundingBoxMin { get; }
    public Vec2 BoundingBoxMax { get; }
    public CuttablePath Outline { get; }                      // perimeter (red)
    public IReadOnlyList<CuttablePath> InteriorCuts { get; }  // finger joints etc. (blue)
    public IReadOnlyList<CuttablePath> Engravings { get; }    // line engravings (black)
    public IReadOnlyList<TextEngraving> TextEngravings { get; }
    public IReadOnlyList<RasterEngraving> RasterEngravings { get; }
    public IReadOnlyList<SvgEngraving> SvgEngravings { get; }
}
```

Each `CuttablePath` has a `Start` point and a list of `PathSegment` values (line or arc), forming either an open or closed path.
