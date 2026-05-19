namespace BoxPlanLib;

public class BoxPlanSettings
{
    public double SheetWidth { get; set; }
    public double SheetHeight { get; set; }
    public double Margin { get; set; }
    public double Kerf { get; set; }
    public double MaterialThickness { get; set; }
    public double FingerJointSize { get; set; } 
    public double Spacing { get; set; }
    public bool Debug { get; set; }
    public bool Labels { get; set; }
    public bool UseAdvancedLayoutOptimizer { get; set; }
    public bool UseOrToolsSequenceOptimization { get; set; }
    public int LayoutSearchIterations { get; set; }
    public int LayoutRandomSeed { get; set; }
    public bool EmbedRasterEngravings { get; set; }
    public bool VectorizeRasterEngravings { get; set; }
    public string RasterEngravingAssetFolder { get; set; } = "assets";

    // Flex cut pattern settings (used for curved prism faces)
    public double FlexLineSpacing { get; set; }
    public double FlexLineLengthFraction { get; set; }
    // Multiplier applied to arc length when sizing a curved face panel.
    // Values < 1.0 compensate for the material shortening caused by flex cuts.
    public double FlexLengthCompensationFactor { get; set; }

    public BoxPlanSettings Default => new BoxPlanSettings
    {
        SheetWidth = 300.0,
        SheetHeight = 300.0,
        Margin = 5.0,
        Kerf = 0.1,
        MaterialThickness = 3.0,
        FingerJointSize = 5.0,
        Spacing = 5.0,
        Debug = false,
        Labels = false,
        UseAdvancedLayoutOptimizer = false,
        UseOrToolsSequenceOptimization = false,
        LayoutSearchIterations = 24,
        LayoutRandomSeed = 1337,
        EmbedRasterEngravings = true,
        RasterEngravingAssetFolder = "assets",
        FlexLineSpacing = 3.0,
        FlexLineLengthFraction = 0.75,
        FlexLengthCompensationFactor = 1.0,
    };
}