using BoxPlanLib.Model;
using BoxPlanLib.Parsing;

namespace BoxPlanLib;

public class BoxPlanLib
{
    private readonly PlanParser _parser = new();
    private readonly PlanResolver _resolver = new();

    public ParseResult<Plan> ParsePlan(string yaml)
    {
        var raw = _parser.Parse(yaml);
        if (!raw.Success || raw.Value is null)
        {
            return ParseResult<Plan>.Fail(raw.Errors);
        }
        return _resolver.Resolve(raw.Value);
    }

    // Downstream stages — re-enable once their underlying types exist.
    //
    // public BoxPlanCuttableShape[] GetCuttableShapes(BoxPlan3DShape[] shapes, BoxPlanMaterialSettings materialSettings)
    // {
    //     return Array.Empty<BoxPlanCuttableShape>();
    // }
    //
    // public string GenerateSimpleSVG(BoxPlanCuttableShape[] cuttableShapes)
    // {
    //     return "<svg></svg>";
    // }
    //
    // public string GeneratePagedSVG(BoxPlanCuttableShape[] cuttableShapes, BoxPlanMaterialSettings materialSettings)
    // {
    //     return "<svg></svg>";
    // }
    //
    // public string GenerateFullPagedSvg(string plan, BoxPlanMaterialSettings materialSettings)
    // {
    //     var shapes = ParsePlan(plan);
    //     var cuttableShapes = GetCuttableShapes(shapes, materialSettings);
    //     return GeneratePagedSVG(cuttableShapes, materialSettings);
    // }
}
