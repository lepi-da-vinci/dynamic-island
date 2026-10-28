namespace WindowsDynamicIsland.Core;

/// <summary>
/// Second-order damped harmonic oscillator (Spring Physics) for 60-120 FPS fluid NotchNook morphing.
/// Automatically reports when at rest so the compositor loop can throttle to 0 FPS during idle.
/// </summary>
public sealed class SpringValue
{
    public double Current { get; private set; }
    public double Target { get; private set; }
    public double Velocity { get; private set; }

    public double Stiffness { get; set; }
    public double Damping { get; set; }
    public double Precision { get; set; }

    public bool IsAtRest =>
        Math.Abs(Target - Current) < Precision &&
        Math.Abs(Velocity) < Precision * 5.0;

    public SpringValue(double initialValue, double stiffness = 350.0, double damping = 23.0, double precision = 0.20)
    {
        Current = initialValue;
        Target = initialValue;
        Stiffness = stiffness;
        Damping = damping;
        Precision = precision;
    }

    public void SetTarget(double target, double? impulseVelocity = null)
    {
        Target = target;
        if (impulseVelocity.HasValue)
        {
            Velocity += impulseVelocity.Value;
        }
    }

    public void SnapTo(double value)
    {
        Current = value;
        Target = value;
        Velocity = 0;
    }

    public bool Step(double dtSeconds)
    {
        if (IsAtRest)
        {
            Current = Target;
            Velocity = 0;
            return false;
        }

        double dt = Math.Clamp(dtSeconds, 0.001, 0.033);
        const int subSteps = 2;
        double subDt = dt / subSteps;

        for (int i = 0; i < subSteps; i++)
        {
            double displacement = Current - Target;
            double springForce = -Stiffness * displacement;
            double dampingForce = -Damping * Velocity;
            double acceleration = springForce + dampingForce;

            Velocity += acceleration * subDt;
            Current += Velocity * subDt;
        }

        if (IsAtRest)
        {
            Current = Target;
            Velocity = 0;
            return false;
        }

        return true;
    }
}

public sealed class IslandMorphController
{
    public SpringValue Width { get; } = new(196, stiffness: 340, damping: 23);
    public SpringValue Height { get; } = new(36, stiffness: 330, damping: 21.5);
    public SpringValue BottomCornerRadius { get; } = new(16, stiffness: 380, damping: 28);
    public SpringValue TopEarRadius { get; } = new(13, stiffness: 380, damping: 28);
    public SpringValue GlowOpacity { get; } = new(0.12, stiffness: 240, damping: 22, precision: 0.01);

    public bool IsAnimating =>
        !Width.IsAtRest || !Height.IsAtRest || !BottomCornerRadius.IsAtRest || !TopEarRadius.IsAtRest || !GlowOpacity.IsAtRest;

    public void MorphTo(double width, double height, double bottomCornerRadius, double topEarRadius, double glowOpacity, bool bouncy = true)
    {
        Width.Stiffness = bouncy ? 330 : 410;
        Width.Damping = bouncy ? 21.5 : 29.0;

        Height.Stiffness = bouncy ? 320 : 400;
        Height.Damping = bouncy ? 20.0 : 28.0;

        bool growingDown = height > Height.Current + 15;
        Width.SetTarget(width);
        Height.SetTarget(height, growingDown ? 65.0 : null);
        BottomCornerRadius.SetTarget(bottomCornerRadius);
        TopEarRadius.SetTarget(topEarRadius);
        GlowOpacity.SetTarget(glowOpacity);
    }

    public void SnapTo(double width, double height, double bottomCornerRadius, double topEarRadius, double glowOpacity)
    {
        Width.SnapTo(width);
        Height.SnapTo(height);
        BottomCornerRadius.SnapTo(bottomCornerRadius);
        TopEarRadius.SnapTo(topEarRadius);
        GlowOpacity.SnapTo(glowOpacity);
    }

    public bool Update(double dtSeconds)
    {
        bool w = Width.Step(dtSeconds);
        bool h = Height.Step(dtSeconds);
        bool c = BottomCornerRadius.Step(dtSeconds);
        bool e = TopEarRadius.Step(dtSeconds);
        bool g = GlowOpacity.Step(dtSeconds);
        return w || h || c || e || g;
    }
}
