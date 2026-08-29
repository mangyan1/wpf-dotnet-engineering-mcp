namespace EngineeringMcp.ControlCenter;

internal readonly record struct GearSpec(
    double R,
    int Teeth,
    double X,
    double Y,
    bool Ccw,
    bool Centerlines,
    double PhaseDegrees = 0)
{
    public double ToothHeight => Math.Max(6, R * 0.13);
    public double PitchRadius => R + ToothHeight * 0.45;
    public double Seconds => Teeth * GearTrainLayout.SecondsPerTooth;
}

internal readonly record struct GearMesh(int First, int Second);

internal static class GearTrainLayout
{
    // One shared tooth-passage interval keeps every connected pair meshed for the full animation.
    public const double SecondsPerTooth = 3.375;

    public static IReadOnlyList<GearSpec> Specs { get; } = BuildSpecs();

    public static IReadOnlyList<GearMesh> Meshes { get; } =
    [
        new(0, 1),
        new(1, 2),
        new(2, 3),
        new(2, 4),
    ];

    public static double PitchCircleError(GearMesh mesh)
    {
        var first = Specs[mesh.First];
        var second = Specs[mesh.Second];
        var centerDistance = Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));
        return Math.Abs(centerDistance - first.PitchRadius - second.PitchRadius);
    }

    public static double ToothPassesPerSecond(GearSpec gear) => gear.Teeth / gear.Seconds;

    public static double PhaseError(GearMesh mesh)
    {
        var first = Specs[mesh.First];
        var second = Specs[mesh.Second];
        var contactDegrees = Math.Atan2(second.Y - first.Y, second.X - first.X) * 180 / Math.PI;
        var firstPhase = PositiveModulo((contactDegrees - first.PhaseDegrees) / (360d / first.Teeth), 1);
        var secondPhase = PositiveModulo((contactDegrees + 180 - second.PhaseDegrees) / (360d / second.Teeth), 1);
        var combined = PositiveModulo(firstPhase + secondPhase, 1);
        return Math.Min(Math.Abs(combined - 0.5), 1 - Math.Abs(combined - 0.5));
    }

    private static IReadOnlyList<GearSpec> BuildSpecs()
    {
        var first = new GearSpec(86, 16, 23, 173, false, true);
        var second = MeshFrom(first, 56, 11, 0, true, false);
        var third = MeshFrom(second, 35, 7, 0, false, true);
        var fourth = MeshFrom(third, 20, 4, -90, true, false);
        var fifth = MeshFrom(third, 26, 6, 117, true, false);
        return [first, second, third, fourth, fifth];
    }

    private static GearSpec MeshFrom(
        GearSpec parent,
        double radius,
        int teeth,
        double contactDegrees,
        bool counterClockwise,
        bool centerlines)
    {
        // Derive placement and initial rotation from the parent gear. Hand-tuned centers or
        // independent animation periods create pitch-circle gaps and progressive tooth drift.
        var pitchRadius = radius + Math.Max(6, radius * 0.13) * 0.45;
        var centerDistance = parent.PitchRadius + pitchRadius;
        var contactRadians = contactDegrees * Math.PI / 180;
        var x = parent.X + centerDistance * Math.Cos(contactRadians);
        var y = parent.Y + centerDistance * Math.Sin(contactRadians);

        var parentStep = 360d / parent.Teeth;
        var parentContactPhase = PositiveModulo((contactDegrees - parent.PhaseDegrees) / parentStep, 1);
        var childContactPhase = PositiveModulo(0.5 - parentContactPhase, 1);
        var childStep = 360d / teeth;
        var childPhase = PositiveModulo(contactDegrees + 180 - childContactPhase * childStep, childStep);

        return new GearSpec(radius, teeth, x, y, counterClockwise, centerlines, childPhase);
    }

    private static double PositiveModulo(double value, double modulus) => (value % modulus + modulus) % modulus;
}
