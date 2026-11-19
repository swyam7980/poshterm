using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoshTerm.Config;

// Everything that can differ per profile. Null = inherit from Defaults.
public class Look
{
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? FontWeight { get; set; }          // normal | bold | 100..900
    public double? LineHeight { get; set; }
    public double? LetterSpacing { get; set; }
    public string? ColorScheme { get; set; }
    public string? CursorStyle { get; set; }         // bar | block | underline
    public bool? CursorBlink { get; set; }
    public string? BackgroundImage { get; set; }     // path or URL
    public string? BackgroundImageStretch { get; set; } // uniformToFill | uniform | fill | none | tile
    public string? BackgroundImageAlignment { get; set; }
    public double? BackgroundImageOpacity { get; set; }
    public double? BackgroundImageBlur { get; set; }
    public double? BackgroundOpacity { get; set; }   // scheme background alpha, 0..1
    public double? Padding { get; set; }
    public int? Scrollback { get; set; }
    public bool? CopyOnSelect { get; set; }
    public bool? RightClickPaste { get; set; }
    public string? CloseOnExit { get; set; }         // graceful | always | never
    public string? BellStyle { get; set; }           // visual | audible | all | none

    public static Look Defaults() => new()
    {
        FontFamily = "Cascadia Mono, Cascadia Code, Consolas, Courier New",
        FontSize = 14, FontWeight = "normal", LineHeight = 1.0, LetterSpacing = 0,
        ColorScheme = "PoshTerm Night", CursorStyle = "bar", CursorBlink = true,
        BackgroundImage = "", BackgroundImageStretch = "uniformToFill", BackgroundImageAlignment = "center",
        BackgroundImageOpacity = 0.25, BackgroundImageBlur = 0, BackgroundOpacity = 0.88, Padding = 8,
        Scrollback = 10000, CopyOnSelect = false, RightClickPaste = true, CloseOnExit = "graceful", BellStyle = "visual",
    };

    // `this` overrides `baseLook` where set.
    public Look Over(Look baseLook)
    {
        var r = new Look();
        foreach (var p in typeof(Look).GetProperties())
            p.SetValue(r, p.GetValue(this) ?? p.GetValue(baseLook));
        return r;
    }

    public Look Clone() => Over(new Look());
}

public sealed class Profile : Look
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Args { get; set; }
    public string? Cwd { get; set; }
    public string? Icon { get; set; }
    public string? StartupScript { get; set; }       // run inside the shell at startup
    public string? StartupCommands { get; set; }     // typed after the script, one per line
    public Dictionary<string, string>? Env { get; set; }
    [JsonIgnore] public string? Source { get; set; } // built-in | detected | custom (not persisted)
}

public sealed class WindowSettings
{
    public double Opacity { get; set; } = 1.0;
    public string Material { get; set; } = "acrylic";  // acrylic | mica | tabbed | none
    public string QuakeHotkey { get; set; } = "Ctrl+`";
    public bool StartMaximized { get; set; }
    public bool ConfirmCloseMultiple { get; set; } = true;
}

public sealed class ColorScheme
{
    public string Name { get; set; } = "";
    public string Background { get; set; } = "#000000";
    public string Foreground { get; set; } = "#CCCCCC";
    public string? CursorColor { get; set; }
    public string? SelectionBackground { get; set; }
    public string Black { get; set; } = "#0C0C0C";
    public string Red { get; set; } = "#C50F1F";
    public string Green { get; set; } = "#13A10E";
    public string Yellow { get; set; } = "#C19C00";
    public string Blue { get; set; } = "#0037DA";
    public string Purple { get; set; } = "#881798";
    public string Cyan { get; set; } = "#3A96DD";
    public string White { get; set; } = "#CCCCCC";
    public string BrightBlack { get; set; } = "#767676";
    public string BrightRed { get; set; } = "#E74856";
    public string BrightGreen { get; set; } = "#16C60C";
    public string BrightYellow { get; set; } = "#F9F1A5";
    public string BrightBlue { get; set; } = "#3B78FF";
    public string BrightPurple { get; set; } = "#B4009E";
    public string BrightCyan { get; set; } = "#61D6D6";
    public string BrightWhite { get; set; } = "#F2F2F2";

    public string[] Palette16() => new[]
    {
        Black, Red, Green, Yellow, Blue, Purple, Cyan, White,
        BrightBlack, BrightRed, BrightGreen, BrightYellow, BrightBlue, BrightPurple, BrightCyan, BrightWhite,
    };
}

public sealed class AppSettings
{
    public string DefaultProfile { get; set; } = "posh";
    public bool AutoDetectProfiles { get; set; } = true;
    public List<string> HiddenProfiles { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public Look Defaults { get; set; } = Look.Defaults();
    public List<Profile> Profiles { get; set; } = new();
    public List<ColorScheme> Schemes { get; set; } = new();
    public Dictionary<string, string> Keybindings { get; set; } = new();
}

public static class SettingsStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoshTerm");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // settings.json may contain // and /* */ comments
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LenientNumberConverter() },
    };

    private static string? _lastWritten;

    // Returns settings plus an error message when the file exists but could not be parsed.
    public static (AppSettings settings, string? error) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new AppSettings();
                Save(fresh);
                return (fresh, null);
            }
            var text = File.ReadAllText(FilePath);
            _lastWritten = text;
            var s = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
            s.Defaults = (s.Defaults ?? new Look()).Over(Look.Defaults());
            s.Window ??= new WindowSettings();
            s.Profiles ??= new(); s.Schemes ??= new(); s.Keybindings ??= new(); s.HiddenProfiles ??= new();
            return (s, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), $"settings.json: {e.Message}");
        }
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        var text = JsonSerializer.Serialize(s, Json);
        _lastWritten = text;
        // write-then-replace so a crash can't leave a half-written file
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, FilePath, overwrite: true);
    }

    // True when the file on disk differs from what we last wrote (i.e. the user edited it).
    public static bool ChangedExternally()
    {
        try { return File.ReadAllText(FilePath) != _lastWritten; } catch { return false; }
    }
}

// Accepts numbers written as strings ("14") so hand-edited files are forgiving.
public sealed class LenientNumberConverter : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t == typeof(double?) || t == typeof(int?);
    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
        t == typeof(double?) ? new D() : new I();

    private sealed class D : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.Null ? null
            : r.TokenType == JsonTokenType.String ? (double.TryParse(r.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null)
            : r.GetDouble();
        public override void Write(Utf8JsonWriter w, double? v, JsonSerializerOptions o) { if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
    }
    private sealed class I : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.Null ? null
            : r.TokenType == JsonTokenType.String ? (int.TryParse(r.GetString(), out var v) ? v : null)
            : (int)Math.Round(r.GetDouble());
        public override void Write(Utf8JsonWriter w, int? v, JsonSerializerOptions o) { if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue(); }
    }
}
