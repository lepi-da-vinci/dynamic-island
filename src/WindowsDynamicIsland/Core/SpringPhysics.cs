namespace WindowsDynamicIsland.Core;

/// <summary>
/// Second-order damped harmonic oscillator (Spring Physics) for 60-120 FPS fluid water-drop morphing.
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

    public SpringValue(double initialValue, double stiffness = 340.0, double damping = 22.0, double precision = 0.20)
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
    public SpringValue Width { get; } = new(196, stiffness: 320, damping: 21);
    public SpringValue Height { get; } = new(38, stiffness: 310, damping: 19.5);
    public SpringValue CornerRadius { get; } = new(20, stiffness: 360, damping: 26);
    public SpringValue GlowOpacity { get; } = new(0.14, stiffness: 240, damping: 22, precision: 0.01);
    public SpringValue TopOffset { get; } = new(5.0, stiffness: 280, damping: 17.5, precision: 0.10);

    public bool IsAnimating =>
        !Width.IsAtRest || !Height.IsAtRest || !CornerRadius.IsAtRest || !GlowOpacity.IsAtRest || !TopOffset.IsAtRest;

    public void MorphTo(double width, double height, double cornerRadius, double glowOpacity, double topOffset, bool bouncy = true)
    {
        // Underdamped liquid surface-tension physics when expanding (droplet pulling down)
        Width.Stiffness = bouncy ? 310 : 390;
        Width.Damping = bouncy ? 20.0 : 28.0;

        Height.Stiffness = bouncy ? 295 : 380;
        Height.Damping = bouncy ? 18.0 : 27.0;

        bool growingDown = height > Height.Current + 12;
        Width.SetTarget(width);
        Height.SetTarget(height, growingDown ? 90.0 : null);
        CornerRadius.SetTarget(cornerRadius);
        GlowOpacity.SetTarget(glowOpacity);

        // Give a downward gravity pull impulse when the droplet expands so it feels like a heavy droplet about to fall
        TopOffset.SetTarget(topOffset, growingDown ? 75.0 : null);
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
