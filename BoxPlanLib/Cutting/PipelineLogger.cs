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

    public void Warn(string message)
    {
        Console.WriteLine($"[warn] {message}");
    }

    public void Progress(string message)
    {
        Console.Write($"\r{message,-80}");
    }

    public void EndProgress()
    {
        Console.Write($"\r{new string(' ', 80)}\r");
    }
}
