using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Source2MapCompiler;

/// <summary>
/// A window's accent colour, from which the rest of its colouring follows: the centre of its gradient is the main colour a quarter of the way to the accent,
/// its highlighted buttons wear the accent toned down towards the main colour and brighten under the pointer, the panels of its popups and the outlines of its fields lean towards it,
/// and every control colour the theme takes from the accent takes it from this one instead.
/// Set it on the window in XAML, or bind it from code when the colour is only known then.
/// </summary>
public static class WindowAccent
{
    public static readonly AttachedProperty<Color> ColorProperty = AvaloniaProperty.RegisterAttached<Window, Color>("Color", typeof(WindowAccent));

    /// <summary>How far from the main colour towards the accent the gradient's centre sits.</summary>
    private const double GradientMix = 0.25;

    /// <summary>How far from the main colour towards the accent a highlighted button's face sits, short of the accent so its text stays readable.</summary>
    private const double ButtonMix = 0.65;

    /// <summary>How much lighter, or darker on the light theme, a highlighted button gets under the pointer, and twice that when pressed.</summary>
    private const double HoverStep = 0.06;

    /// <summary>How much the stars of a rating are boosted from the accent, lighter on the dark theme and darker on the light one, as Qt's percentage.</summary>
    private const double StarBoost = 130;

    /// <summary>How far from the main colour towards the accent a popup's panel sits on the dark theme, where the panel is the window colour lifted towards the accent.</summary>
    private const double PopupMix = 0.2;

    /// <summary>How far towards the accent a popup's panel leans on the light theme, from the window colour lifted halfway to white.</summary>
    private const double LightPopupMix = 0.06;

    /// <summary>How far from the main colour towards the accent the outline of fields and check boxes sits, before it is greyed a little.</summary>
    private const double OutlineMix = 0.45;

    /// <summary>The Fluent keys for text on a surface painted in the accent.</summary>
    private static readonly string[] OnAccentKeys =
    [
        "ButtonForegroundPressed",
        "ToggleButtonForegroundPressed",
        "ToggleButtonForegroundChecked",
        "ToggleButtonForegroundCheckedPointerOver",
        "ToggleButtonForegroundCheckedPressed",
        "MenuFlyoutItemForegroundPressed",
    ];

    /// <summary>The theme's brushes a window paints in its own colours, by the keys they hold in the theme.</summary>
    private static readonly string[] PaintedBrushes = ["AccentBrush", "PopupBrush", "OutlineBrush"];

    // Fluent's accent and the shades it makes of it, lighter and darker, which come from the system rather than the app
    private static readonly string[] FluentAccents =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    // how far each of those shades is from the accent in lightness
    private static readonly double[] FluentShadeSteps = [0, 0.1, 0.2, 0.3, -0.1, -0.2, -0.3];

    /// <summary>What each window has: the dictionary its colouring lives in, swapped whole so the window is told once, and whether a repaint is already on its way.</summary>
    private sealed class State
    {
        public ResourceDictionary? Own { get; set; }

        public bool Pending { get; set; }
    }

    private static readonly ConditionalWeakTable<Window, State> States = [];

    /// <summary>The theme's keys that hold one of the painted brushes, found once per theme variant since the theme does not change.</summary>
    private static readonly Dictionary<ThemeVariant, List<(object Key, SolidColorBrush Brush)>> ThemeKeys = [];

    // Fluent's keys coloured with one of its accent shades, by the shade's place in FluentAccents, and whether the key holds
    // the colour itself rather than a brush, found once per theme variant
    private static readonly Dictionary<ThemeVariant, List<(object Key, int Shade, bool IsColor)>> FluentKeys = [];

    static WindowAccent()
    {
        ColorProperty.Changed.AddClassHandler<Window>((window, _) => ApplyLater(window));

        // the colouring is derived from the theme's colours, so it is derived again when the theme switches
        ThemeVariantScope.ActualThemeVariantProperty.Changed.AddClassHandler<Window>((window, _) => ApplyLater(window));
    }

    /// <summary>
    /// Derives the window's colouring once the change asking for it has gone through, since the theme's brushes it looks for take on a new accent in that same change,
    /// and once for however many changes ask in the meantime, as a theme switch changes both the theme and the accent.
    /// </summary>
    private static void ApplyLater(Window window)
    {
        var state = States.GetValue(window, _ => new State());

        // a window's first colouring goes in straight away, as it's set up, so the window is never drawn without it and then
        // changes colour in front of the user. Only a change to colouring it already has needs to wait
        if (state.Own == null)
        {
            if (window.IsSet(ColorProperty))
            {
                Apply(window, state, GetColor(window));
            }

            return;
        }

        if (state.Pending)
        {
            return;
        }

        state.Pending = true;

        Dispatcher.UIThread.Post(() =>
        {
            state.Pending = false;

            if (window.IsSet(ColorProperty))
            {
                Apply(window, state, GetColor(window));
            }
        });
    }

    /// <summary>The centre of the gradient a window with <paramref name="accent"/> is painted with, over <paramref name="main"/>.</summary>
    public static Color GradientCenter(Color main, Color accent)
    {
        return Mix(main, accent, GradientMix);
    }

    public static Color GetColor(Window window)
    {
        return window.GetValue(ColorProperty);
    }

    public static void SetColor(Window window, Color value)
    {
        window.SetValue(ColorProperty, value);
    }

    /// <summary>Puts the colours derived from <paramref name="accent"/> into the window's resources, ahead of the theme's for everything in the window.</summary>
    private static void Apply(Window window, State state, Color accent)
    {
        var variant = window.ActualThemeVariant;
        var application = Application.Current!;

        if (!application.TryFindResource("AppColor", variant, out var found) || found is not Color main
            || !application.TryFindResource("ShadeColor", variant, out found) || found is not Color shade
            || !application.TryFindResource("ContrastSoftColor", variant, out found) || found is not Color contrastSoft)
        {
            return;
        }

        var light = variant == ThemeVariant.Light;
        var step = light ? -HoverStep : HoverStep;
        var face = Mix(main, accent, ButtonMix);
        var hover = new SolidColorBrush(Shift(face, step));
        var pressed = new SolidColorBrush(Shift(face, 2 * step));

        // everything goes into a dictionary of its own first, so the window is told of one change rather than one per key
        var own = new ResourceDictionary
        {
            ["AppAccentColor"] = Mix(main, accent, GradientMix),
            ["AccentButtonBackground"] = new SolidColorBrush(face),
            ["AccentButtonBorderBrush"] = new SolidColorBrush(face),
            ["AccentButtonBackgroundPointerOver"] = hover,
            ["AccentButtonBorderBrushPointerOver"] = hover,
            ["AccentButtonBackgroundPressed"] = pressed,
            ["AccentButtonBorderBrushPressed"] = pressed,
        };

        // Fluent paints check boxes, switches, sliders, radio buttons and selections in its own accent, which is the system's,
        // so they're painted in shades of this one. These go in first, so the keys set below have the last word
        var shades = FluentShadeSteps.Select(step => Shift(accent, light ? -step : step)).ToArray();
        var shadeBrushes = shades.Select(colour => new SolidColorBrush(colour)).ToArray();

        for (var i = 0; i < FluentAccents.Length; i++)
        {
            own[FluentAccents[i]] = shades[i];
        }

        foreach (var (key, index, isColor) in FluentKeysOf(application, variant))
        {
            if (!own.ContainsKey(key))
            {
                own[key] = isColor ? shades[index] : shadeBrushes[index];
            }
        }

        var onFace = new SolidColorBrush(Contrast.TextOn(face));

        own["AccentButtonForeground"] = own["AccentButtonForegroundPointerOver"] = own["AccentButtonForegroundPressed"] = onFace;

        // text on the accent itself: checked toggles, pressed buttons, selected rows and items
        var onAccent = new SolidColorBrush(Contrast.TextOn(accent));

        own["OnAccentBrush"] = onAccent;

        // the stars of a rating are thin, so they wear the accent boosted off the window
        own["StarBrush"] = new SolidColorBrush(light ? Contrast.Darker(accent, StarBoost) : Contrast.Lighter(accent, StarBoost));

        foreach (var key in OnAccentKeys)
        {
            own[key] = onAccent;
        }

        // the panels of popups and the outlines of fields lean towards the accent: a popup is the window colour lifted towards it, on the light theme lifted towards white first,
        // and an outline sits between the two, greyed a little so it reads as an edge
        var popup = light ? Mix(Mix(main, shade, 0.5), accent, LightPopupMix) : Mix(main, accent, PopupMix);
        var outline = Mix(Mix(main, accent, OutlineMix), contrastSoft, light ? 0.3 : 0.12);

        // whatever the theme paints with the shared accent, popup and outline brushes, the brushes themselves and the Fluent keys pointed at them, this window paints in its own
        var replacements = new Dictionary<string, SolidColorBrush>
        {
            ["AccentBrush"] = new SolidColorBrush(accent),
            ["PopupBrush"] = new SolidColorBrush(popup),
            ["OutlineBrush"] = new SolidColorBrush(outline),
        };

        var painted = new Dictionary<SolidColorBrush, SolidColorBrush>();

        foreach (var name in PaintedBrushes)
        {
            if (application.TryFindResource(name, variant, out var themeBrush) && themeBrush is SolidColorBrush brush)
            {
                painted[brush] = replacements[name];
            }
        }

        foreach (var (key, brush) in ThemeKeysOf(application, variant))
        {
            if (painted.TryGetValue(brush, out var replacement))
            {
                own[key] = replacement;
            }
        }

        // the last colouring goes and this one comes in its place, which is what the window and its popups take up
        var merged = window.Resources.MergedDictionaries;

        if (state.Own != null)
        {
            merged.Remove(state.Own);
        }

        state.Own = own;
        merged.Add(own);
    }

    /// <summary>The keys of the theme that hold one of the painted brushes, with the brush each holds.</summary>
    private static List<(object Key, SolidColorBrush Brush)> ThemeKeysOf(Application application, ThemeVariant variant)
    {
        if (ThemeKeys.TryGetValue(variant, out var keys))
        {
            return keys;
        }

        var painted = new HashSet<SolidColorBrush>();

        foreach (var name in PaintedBrushes)
        {
            if (application.TryFindResource(name, variant, out var found) && found is SolidColorBrush brush)
            {
                painted.Add(brush);
            }
        }

        keys = [];

        foreach (var provider in application.Resources.MergedDictionaries)
        {
            if ((provider is ResourceInclude include ? include.Loaded : provider) is not IResourceDictionary dictionary)
            {
                continue;
            }

            foreach (var key in dictionary.Keys)
            {
                if (dictionary.TryGetResource(key, variant, out var value) && value is SolidColorBrush brush && painted.Contains(brush))
                {
                    keys.Add((key, brush));
                }
            }
        }

        ThemeKeys[variant] = keys;

        return keys;
    }

    // The keys Fluent colours with one of its accent shades, found by their colour, so there's no list of them to keep up with
    // Fluent
    private static List<(object Key, int Shade, bool IsColor)> FluentKeysOf(Application application, ThemeVariant variant)
    {
        if (FluentKeys.TryGetValue(variant, out var keys))
        {
            return keys;
        }

        var shades = FluentAccents.Select(name => application.TryFindResource(name, variant, out var found) && found is Color color ? color : (Color?)null).ToArray();
        var names = new HashSet<object>();

        foreach (var fluent in application.Styles.OfType<FluentTheme>())
        {
            Collect(fluent.Resources);
        }

        keys = [];

        foreach (var key in names.Where(key => key is not string name || !FluentAccents.Contains(name)))
        {
            if (!application.TryFindResource(key, variant, out var value))
            {
                continue;
            }

            var color = value switch
            {
                ISolidColorBrush brush => brush.Color,
                Color plain => plain,
                _ => (Color?)null,
            };

            if (color != null && Array.IndexOf(shades, color) is var shade and >= 0)
            {
                keys.Add((key, shade, value is Color));
            }
        }

        FluentKeys[variant] = keys;

        return keys;

        void Collect(IResourceDictionary dictionary)
        {
            names.UnionWith(dictionary.Keys);

            foreach (var merged in dictionary.MergedDictionaries.OfType<IResourceDictionary>())
            {
                Collect(merged);
            }

            foreach (var themed in new[] { variant, ThemeVariant.Default })
            {
                if (dictionary.ThemeDictionaries.TryGetValue(themed, out var provider) && provider is IResourceDictionary inner)
                {
                    Collect(inner);
                }
            }
        }
    }

    /// <summary>The colour <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        return Color.FromRgb(Step(from.R, to.R), Step(from.G, to.G), Step(from.B, to.B));

        byte Step(byte start, byte end)
        {
            return (byte)Math.Round(start + (end - start) * amount);
        }
    }

    /// <summary>The colour with its lightness moved by <paramref name="amount"/>.</summary>
    private static Color Shift(Color color, double amount)
    {
        var hsl = color.ToHsl();

        return new HslColor(hsl.A, hsl.H, hsl.S, Math.Clamp(hsl.L + amount, 0, 1)).ToRgb();
    }
}
