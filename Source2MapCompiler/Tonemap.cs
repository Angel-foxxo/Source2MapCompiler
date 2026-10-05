namespace Source2MapCompiler;

// The exposure and tonemap Source 2 Viewer applies, with its default filmic curve, the Uncharted 2 curve with Valve's
// parameters. Exposure scales the lightmap before the curve, and auto exposure picks the scale that shows its average
// luminance as middle grey, which the user's stops then move up or down from. The curve runs in LightmapShader on the GPU,
// this is the part worked out up front
internal static class Tonemap
{
    public const float ShoulderStrength = 0.15f;
    public const float LinearStrength = 0.5f;
    public const float LinearAngle = 0.1f;
    public const float ToeStrength = 0.2f;
    public const float ToeNum = 0.02f;
    public const float ToeDenom = 0.3f;

    // the exposed lightmap is scaled by this before the curve, and clamped to the white point
    public const float PreCurveScale = 2.8f;
    public const float WhitePoint = 4.0f * PreCurveScale;

    private const float MiddleGrey = 0.18f;

    // what the curve shows the white point as, which the shader divides by so white comes out as 1
    public static float WhiteScale { get; } = 1 / Curve(WhitePoint);

    // the luminance before the curve that comes out as middle grey
    private static readonly float MiddleGreyInput = Invert(MiddleGrey);

    // The scale for a lightmap with this average luminance, moved by the user's stops
    public static float Exposure(float averageLuminance, float stops)
    {
        return MiddleGreyInput / Math.Max(averageLuminance, 1e-4f) * MathF.Pow(2, stops);
    }

    private static float Curve(float x)
    {
        return (x * (x * ShoulderStrength + LinearAngle * LinearStrength) + ToeStrength * ToeNum) / (x * (x * ShoulderStrength + LinearStrength) + ToeStrength * ToeDenom) - ToeNum / ToeDenom;
    }

    // The input the curve shows as this, from solving the curve for it, which leaves a quadratic once its denominator is
    // cleared. The result is taken back to before the pre-curve scale
    private static float Invert(float shown)
    {
        var g = shown / WhiteScale + ToeNum / ToeDenom;
        var a = ShoulderStrength * (1 - g);
        var b = LinearStrength * (LinearAngle - g);
        var c = ToeStrength * (ToeNum - g * ToeDenom);
        var root = MathF.Sqrt(b * b - 4 * a * c);
        var x = (-b + root) / (2 * a);

        if (x <= 0)
        {
            x = (-b - root) / (2 * a);
        }

        return float.IsFinite(x) && x > 0 ? x / PreCurveScale : MiddleGrey;
    }
}
