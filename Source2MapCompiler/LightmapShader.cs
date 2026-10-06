using System.Globalization;
using SkiaSharp;

namespace Source2MapCompiler;

// Exposes and tonemaps the lightmap's half floats as they're drawn, so changing the exposure only changes a number the GPU
// is given. It's Source 2 Viewer's post process tonemap, see Tonemap. A texel no chart covers has no alpha and is drawn as a
// dark checkerboard. Sampling a zoomed out view blends covered and uncovered texels, so the colour is divided back out of
// the alpha
internal static class LightmapShader
{
    private static readonly string Source = $$"""
        uniform shader lightmap;
        uniform float exposure;

        float3 curve(float3 x)
        {
            return (x * (x * {{F(Tonemap.ShoulderStrength)}} + {{F(Tonemap.LinearAngle * Tonemap.LinearStrength)}}) + {{F(Tonemap.ToeStrength * Tonemap.ToeNum)}})
                / (x * (x * {{F(Tonemap.ShoulderStrength)}} + {{F(Tonemap.LinearStrength)}}) + {{F(Tonemap.ToeStrength * Tonemap.ToeDenom)}})
                - {{F(Tonemap.ToeNum / Tonemap.ToeDenom)}};
        }

        half4 main(float2 texel)
        {
            float4 colour = lightmap.eval(texel);

            if (colour.a < 0.5)
            {
                float shade = mod(floor(texel.x / 8) + floor(texel.y / 8), 2) == 0 ? 0.11 : 0.14;
                return half4(shade, shade, shade, 1);
            }

            float3 exposed = min(colour.rgb / colour.a * exposure * {{F(Tonemap.PreCurveScale)}}, {{F(Tonemap.WhitePoint)}});
            float3 shown = saturate(curve(exposed) * {{F(Tonemap.WhiteScale)}});
            float3 srgb = mix(shown * 12.92, 1.055 * pow(shown, float3(1 / 2.4)) - 0.055, step(0.0031308, shown));
            return half4(half3(srgb), 1);
        }
        """;

    private static readonly SKRuntimeEffect Effect = SKRuntimeEffect.CreateShader(Source, out var errors) ?? throw new InvalidOperationException(errors);

    // The image's texels at its own coordinates, nearest when they're shown bigger than a pixel so they stay crisp
    public static SKShader Create(SKImage lightmap, float exposure, bool nearest)
    {
        var sampling = nearest ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using var builder = new SKRuntimeShaderBuilder(Effect);
        builder.Uniforms["exposure"] = exposure;
        builder.Children["lightmap"] = lightmap.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        return builder.Build();
    }

    private static string F(float value)
    {
        return value.ToString("0.0########", CultureInfo.InvariantCulture);
    }
}
