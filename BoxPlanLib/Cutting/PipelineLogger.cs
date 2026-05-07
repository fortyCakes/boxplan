namespace BoxPlanLib.Cutting;

/// <summary>
/// Writes structured debug messages for the cutting pipeline.
/// Output is suppressed unless the debug flag is enabled.
/// </summary>
internal sealed class PipelineLogger
{
    private readonly bool _enabled;

    public PipelineLogger(bool enabled)
    {
        _enabled = enabled;
    }

    public void Log(string message)
    {
        if (_enabled)
            Console.WriteLine(message);
    }
}
