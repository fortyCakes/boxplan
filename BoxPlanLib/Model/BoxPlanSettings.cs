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
    };
}