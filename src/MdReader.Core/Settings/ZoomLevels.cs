namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
public static class ZoomLevels
{
    public const double Min = 0.5, Max = 3.0, Default = 1.0;

    private const double Tolerance = 0.001;

    public static IReadOnlyList<double> Steps { get; } = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public static double Clamp(double zoom) => Math.Clamp(zoom, Min, Max);

    public static double StepUp(double current)
    {
        foreach (var step in Steps)
        {
            if (step > current + Tolerance)
            {
                return step;
            }
        }

        return Max;
    }

    public static double StepDown(double current)
    {
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < current - Tolerance)
            {
                return Steps[i];
            }
        }

        return Min;
    }
}
