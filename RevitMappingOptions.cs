namespace BeamSchedulePlugin;

internal sealed record RevitMappingOptions(
    double EndpointTolerance = 10,
    double Angle = .5,
    double JoinLimit = 50,
    double SegmentCoverage = .9,
    double SpanCoverage = .85,
    double ReviewLateral = 850,
    double SplitVertical = 100,
    double ReviewVertical = 750,
    double RunnerUpMargin = 25,
    double EndLimit = 1500,
    double SplitOverhang = 350,
    double ChainLinkOverlap = 1,
    double MinLongCoverage = .85,
    double MinShortCoverage = .85)
{
    internal static RevitMappingOptions Default { get; } = new();

    internal void Validate()
    {
        void NonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0)
                throw new InvalidOperationException($"{name} must be finite and non-negative.");
        }
        void Fraction(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new InvalidOperationException($"{name} must be between 0 and 1.");
        }
        NonNegative(EndpointTolerance, "Endpoint / strict axis tolerance");
        if (EndpointTolerance == 0) throw new InvalidOperationException("Endpoint / strict axis tolerance must be greater than zero.");
        NonNegative(Angle, "Shared angle");
        if (Angle > 90) throw new InvalidOperationException("Shared angle must be at most 90 degrees.");
        NonNegative(JoinLimit, "Span / internal join limit");
        Fraction(SegmentCoverage, "Minimum ETABS segment containment");
        Fraction(SpanCoverage, "Minimum shared span / group coverage");
        Fraction(MinLongCoverage, "Minimum longer-span offset coverage");
        Fraction(MinShortCoverage, "Minimum shorter-span extension coverage");
        NonNegative(ReviewLateral, "Review lateral offset");
        NonNegative(SplitVertical, "Review split vertical offset");
        NonNegative(ReviewVertical, "Offset / chain vertical offset");
        NonNegative(RunnerUpMargin, "Runner-up margin");
        NonNegative(EndLimit, "End-extension / chain outer-end limit");
        NonNegative(SplitOverhang, "Extended split outer overhang");
        NonNegative(ChainLinkOverlap, "Chain segment link overlap");
    }
}
