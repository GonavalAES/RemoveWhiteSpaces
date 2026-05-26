using System.IO;
using System.Text.Json;

namespace RemoveWhiteSpaces;

/// <summary>
/// All user preferences persisted across sessions.
/// </summary>
public class AppSettings
{
    // ── Window geometry ───────────────────────────────────────────────
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public double WindowWidth { get; set; } = 1150;
    public double WindowHeight { get; set; } = 680;

    // ── Behaviour toggles ─────────────────────────────────────────────
    public bool AlwaysOnTop { get; set; } = false;
    public bool AutoConvertOnPaste { get; set; } = false;

    // ── Strip options ─────────────────────────────────────────────────
    public bool StripLineComments { get; set; } = true;
    public bool StripBlockComments { get; set; } = true;
    public bool StripXmlDocComments { get; set; } = true;
    public bool StripBlankLines { get; set; } = true;
    public bool CollapseToOneLine { get; set; } = true;

    // ── Chunk splitting ───────────────────────────────────────────────
    // 0 means no chunking.
    public int MaxTokensPerChunk { get; set; } = 0;

    // ── Custom regex patterns ─────────────────────────────────────────
    public List<string> CustomPatterns { get; set; } = new();

    // ─────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                       "RemoveWhiteSpaces", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath))
            {
                string json = System.IO.File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                return loaded ?? new AppSettings();
            }
        }
        catch { /* Corrupt or missing — fall through to defaults. */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(SettingsPath, json);
        }
        catch { /* Silently ignore — don't crash the app over settings. */ }
    }
}
