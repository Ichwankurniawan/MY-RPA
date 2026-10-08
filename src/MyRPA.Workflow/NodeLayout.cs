namespace MyRPA.Workflow;

/// <summary>
/// A node's position on a designer canvas (format 1.1, ADR-0037). Designer data only: the engine ignores it.
/// </summary>
public sealed record NodeLayout
{
    /// <summary>Creates a position.</summary>
    /// <param name="x">Horizontal position in canvas units; a finite number.</param>
    /// <param name="y">Vertical position in canvas units; a finite number.</param>
    public NodeLayout(double x, double y)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "The position must be a finite number.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "The position must be a finite number.");
        }

        X = x;
        Y = y;
    }

    /// <summary>Horizontal position.</summary>
    public double X { get; }

    /// <summary>Vertical position.</summary>
    public double Y { get; }
}
