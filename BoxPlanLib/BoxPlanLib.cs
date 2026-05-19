using BoxPlanLib.Cutting;
using BoxPlanLib.Model;
using BoxPlanLib.Parsing;
using BoxPlanLib.Svg;

namespace BoxPlanLib;

public class BoxPlanLib
{
    private readonly PlanParser _parser = new();
    private readonly PlanResolver _resolver = new();
    private readonly FitResolver _fitResolver = new();
    private readonly CuttingPipeline _cuttingPipeline = new();
    private readonly SvgGenerator _svgGenerator = new();

    public ParseResult<BoxPlan> ParsePlan(string yaml, BoxPlanSettings? settings = null)
    {
        var raw = _parser.Parse(yaml);
        if (!raw.Success || raw.Value is null)
        {
            return ParseResult<BoxPlan>.Fail(raw.Errors);
        }
        var resolved = _resolver.Resolve(raw.Value);
        if (!resolved.Success || resolved.Value is null)
        {
            return resolved;
        }
        return _fitResolver.Resolve(resolved.Value, settings?.MaterialThickness ?? 0);
    }

    /// <param name="sourcePath">
    /// Absolute path to the source YAML file. When provided, engraving hrefs are resolved
    /// relative to this file's directory, and image dimensions and vectorization are applied.
    /// </param>
    public BoxPlanCuttableShape[] GetCuttableShapes(
        BoxPlan plan, BoxPlanSettings settings, string? sourcePath = null)
    {
        var resolved = _fitResolver.Resolve(plan, settings.MaterialThickness);
        if (!resolved.Success || resolved.Value is null)
        {
            throw new InvalidOperationException(string.Join("; ", resolved.Errors.Select(e => e.ToString())));
        }

        var shapes = _cuttingPipeline.Run(plan, settings);

        if (sourcePath is not null)
        {
            shapes = EngravingPipeline.NormalizeEngravingSources(shapes, sourcePath);
            shapes = EngravingPipeline.ResolveEngravingDimensions(shapes);
            if (settings.VectorizeRasterEngravings)
                shapes = EngravingPipeline.VectorizeRasterEngravings(shapes);
            shapes = EngravingPipeline.InlineSvgEngravings(shapes);
        }

        return shapes;
    }

    public string GenerateSimpleSVG(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
        => _svgGenerator.GenerateSimpleSvg(shapes, settings);

    public string GeneratePagedSVG(BoxPlanCuttableShape[] cuttableShapes, BoxPlanSettings materialSettings)
        => _svgGenerator.GeneratePagedSvg(cuttableShapes, materialSettings);

    /// <param name="outputDirectory">
    /// When provided, engraving assets are copied or embedded relative to this directory
    /// before SVG generation. Must already exist.
    /// </param>
    public (int PageCount, double DensityScore) MeasureLayout(
        BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
        => _svgGenerator.MeasureLayout(shapes, settings);

    public IReadOnlyList<string> GeneratePagedSVGPages(
        BoxPlanCuttableShape[] cuttableShapes,
        BoxPlanSettings materialSettings,
        string? outputDirectory = null)
    {
        var shapes = cuttableShapes;
        if (outputDirectory is not null)
            shapes = EngravingPipeline.PrepareEngravingAssets(shapes, materialSettings, outputDirectory);

        return _svgGenerator.GeneratePagedSvgPages(shapes, materialSettings);
    }
}
