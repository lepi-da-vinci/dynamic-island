namespace WindowsDynamicIsland.Core;

/// <summary>
/// Second-order damped harmonic oscillator (Spring Physics) for 60-120 FPS fluid pill morphing.
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

    public SpringValue(double initialValue, double stiffness = 360.0, double damping = 24.0, double precision = 0.25)
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
    public SpringValue Width { get; } = new(168, stiffness: 390, damping: 25);
    public SpringValue Height { get; } = new(34, stiffness: 410, damping: 27);
    public SpringValue CornerRadius { get; } = new(17, stiffness: 420, damping: 30);
    public SpringValue GlowOpacity { get; } = new(0.0, stiffness: 250, damping: 24, precision: 0.01);
    public SpringValue TopOffset { get; } = new(8.0, stiffness: 420, damping: 28, precision: 0.15);

    public bool IsAnimating =>
        !Width.IsAtRest || !Height.IsAtRest || !CornerRadius.IsAtRest || !GlowOpacity.IsAtRest || !TopOffset.IsAtRest;

    public void MorphTo(double width, double height, double cornerRadius, double glowOpacity, double topOffset, bool bouncy = true)
    {
        Width.Stiffness = bouncy ? 380 : 440;
        Width.Damping = bouncy ? 23.5 : 31.0;

        Height.Stiffness = bouncy ? 400 : 450;
        Height.Damping = bouncy ? 25.0 : 32.0;

        Width.SetTarget(width);
        Height.SetTarget(height);
        CornerRadius.SetTarget(cornerRadius);
        GlowOpacity.SetTarget(glowOpacity);
        TopOffset.SetTarget(topOffset);
    }

    public bool Update(double dtSeconds)
    {
        bool w = Width.Step(dtSeconds);
        bool h = Height.Step(dtSeconds);
        bool c = CornerRadius.Step(dtSeconds);
        bool g = GlowOpacity.Step(dtSeconds);
        bool t = TopOffset.Step(dtSeconds);
        return w || h || c || g || t;
    }
}
