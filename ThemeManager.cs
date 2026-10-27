using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace SerialChecker;

/// <summary>A colour theme. Status colours (pass, attention, …) and text greys are shared by every theme.</summary>
public sealed record AppTheme(
    string Key,
    string Name,
    Color Window,
    Color Sidebar,
    Color Surface,
    Color SurfaceHover,
    Color Elevated,
    Color Input,
    Color BorderSubtle,
    Color BorderStrong,
    Color Accent,
    Color AccentHover,
    Color AccentText,
    Color AccentForeground,
    Color BrandStart,
    Color BrandEnd)
{
    public SolidColorBrush AccentBrush => Frozen(new SolidColorBrush(Accent));
    public SolidColorBrush SurfaceBrush => Frozen(new SolidColorBrush(Elevated));

    internal static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// Swaps the theme brushes in the application resources. Windows reference them with
/// DynamicResource, so a switch repaints everything that is open.
/// </summary>
public static class ThemeManager
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static IReadOnlyList<AppTheme> Themes { get; } = new[]
    {
        new AppTheme("midnight", "Midnight",
            Window: C("#0C0D11"), Sidebar: C("#101217"), Surface: C("#14161C"), SurfaceHover: C("#1A1D24"),
            Elevated: C("#1C1F27"), Input: C("#0F1116"), BorderSubtle: C("#20242C"), BorderStrong: C("#2C313C"),
            Accent: C("#7C6CFF"), AccentHover: C("#8E80FF"), AccentText: C("#B7ADFF"), AccentForeground: C("#FFFFFF"),
            BrandStart: C("#8E80FF"), BrandEnd: C("#5B4BE0")),

        new AppTheme("ocean", "Ocean",
            Window: C("#090D14"), Sidebar: C("#0C121B"), Surface: C("#101722"), SurfaceHover: C("#151F2D"),
            Elevated: C("#172131"), Input: C("#0B1119"), BorderSubtle: C("#1A2535"), BorderStrong: C("#26344A"),
            Accent: C("#3B82F6"), AccentHover: C("#5A97F8"), AccentText: C("#93C5FD"), AccentForeground: C("#FFFFFF"),
            BrandStart: C("#60A5FA"), BrandEnd: C("#2563EB")),

        new AppTheme("forest", "Forest",
            Window: C("#090F0C"), Sidebar: C("#0C1410"), Surface: C("#101A15"), SurfaceHover: C("#15221C"),
            Elevated: C("#17251E"), Input: C("#0B130F"), BorderSubtle: C("#1A2A22"), BorderStrong: C("#26392F"),
            Accent: C("#10B981"), AccentHover: C("#2CC793"), AccentText: C("#6EE7B7"), AccentForeground: C("#03130C"),
            BrandStart: C("#34D399"), BrandEnd: C("#059669")),

        new AppTheme("ember", "Ember",
            Window: C("#100C0A"), Sidebar: C("#15100D"), Surface: C("#1A1411"), SurfaceHover: C("#231B16"),
            Elevated: C("#261D18"), Input: C("#130E0B"), BorderSubtle: C("#2C221C"), BorderStrong: C("#3B2E26"),
            Accent: C("#F97316"), AccentHover: C("#FB8A3C"), AccentText: C("#FDBA74"), AccentForeground: C("#1A0C02"),
            BrandStart: C("#FB923C"), BrandEnd: C("#EA580C")),

        new AppTheme("sakura", "Sakura",
            Window: C("#100B0E"), Sidebar: C("#150E13"), Surface: C("#1A1218"), SurfaceHover: C("#231820"),
            Elevated: C("#261A23"), Input: C("#130C11"), BorderSubtle: C("#2C1F29"), BorderStrong: C("#3B2A37"),
            Accent: C("#EC4899"), AccentHover: C("#F062A9"), AccentText: C("#F9A8D4"), AccentForeground: C("#FFFFFF"),
            BrandStart: C("#F472B6"), BrandEnd: C("#DB2777")),
    };

    public static AppTheme Current { get; private set; } = Themes[0];

    public static event EventHandler? ThemeChanged;

    private static string SettingsPath => Path.Combine(BaselineStore.Folder, "settings.json");

    /// <summary>Applies the theme saved last time (or the default) without writing settings.</summary>
    public static void ApplySaved()
    {
        string? key = null;
        try
        {
            if (File.Exists(SettingsPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("Theme", out JsonElement theme) && theme.ValueKind == JsonValueKind.String)
                    key = theme.GetString();
            }
        }
        catch
        {
            // A broken settings file just means the default theme.
        }

        Apply(key ?? Themes[0].Key, save: false);
    }

    public static void Apply(string key, bool save = true)
    {
        AppTheme theme = Themes.FirstOrDefault(t => t.Key == key) ?? Themes[0];
        ResourceDictionary res = Application.Current.Resources;

        void Brush(string name, Color color) => res[name] = AppTheme.Frozen(new SolidColorBrush(color));

        res["WindowColor"] = theme.Window;
        res["AccentColor"] = theme.Accent;
        Brush("Bg.Window", theme.Window);
        Brush("Bg.Sidebar", theme.Sidebar);
        Brush("Bg.Surface", theme.Surface);
        Brush("Bg.SurfaceHover", theme.SurfaceHover);
        Brush("Bg.Elevated", theme.Elevated);
        Brush("Bg.Input", theme.Input);
        Brush("Bg.Tooltip", Color.FromArgb(0xF0, theme.Elevated.R, theme.Elevated.G, theme.Elevated.B));
        Brush("Border.Subtle", theme.BorderSubtle);
        Brush("Border.Strong", theme.BorderStrong);
        Brush("Accent", theme.Accent);
        Brush("Accent.Hover", theme.AccentHover);
        Brush("Accent.Soft", Color.FromArgb(0x1F, theme.Accent.R, theme.Accent.G, theme.Accent.B));
        Brush("Accent.Text", theme.AccentText);
        Brush("Accent.Foreground", theme.AccentForeground);
        res["Brand.Gradient"] = AppTheme.Frozen(new LinearGradientBrush(theme.BrandStart, theme.BrandEnd, new Point(0, 0), new Point(1, 1)));

        Current = theme;
        WindowEffects.RefreshFrames();
        ThemeChanged?.Invoke(null, EventArgs.Empty);

        if (save)
            Save(theme.Key);
    }

    private static void Save(string key)
    {
        try
        {
            Directory.CreateDirectory(BaselineStore.Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Theme = key }));
        }
        catch
        {
            // Not being able to remember the theme is not worth interrupting the user.
        }
    }
}
