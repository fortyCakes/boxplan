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

    // Downstream stages — re-enable once their underlying types exist.
    //
    public BoxPlanCuttableShape[] GetCuttableShapes(BoxPlan plan, BoxPlanSettings settings)
    {
        var resolved = _fitResolver.Resolve(plan, settings.MaterialThickness);
        if (!resolved.Success || resolved.Value is null)
        {
            throw new InvalidOperationException(string.Join("; ", resolved.Errors.Select(e => e.ToString())));
        }
        return _cuttingPipeline.Run(plan, settings);
    }

    public string GenerateSimpleSVG(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
        => _svgGenerator.GenerateSimpleSvg(shapes, settings);

    public string GeneratePagedSVG(BoxPlanCuttableShape[] cuttableShapes, BoxPlanSettings materialSettings)
        => _svgGenerator.GeneratePagedSvg(cuttableShapes, materialSettings);

    public IReadOnlyList<string> GeneratePagedSVGPages(BoxPlanCuttableShape[] cuttableShapes, BoxPlanSettings materialSettings)
        => _svgGenerator.GeneratePagedSvgPages(cuttableShapes, materialSettings);

    //
    // public string GenerateFullPagedSvg(string plan, BoxPlanMaterialSettings materialSettings)
    // {
    //     var shapes = ParsePlan(plan);
    //     var cuttableShapes = GetCuttableShapes(shapes, materialSettings);
    //     return GeneratePagedSVG(cuttableShapes, materialSettings);
    // }
}
