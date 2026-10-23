using System.Text.Json;
using System.Text.Json.Serialization;
using Quickey.Core.Operations;

namespace Quickey.Core.Settings;

public enum OutputLocation
{
    SameFolder,
    QuickeySubfolder,
}

public enum ModifierKey
{
    Shift,
    Ctrl,
    Alt,
}

public sealed class AppSettings
{
    public ModifierKey ConvertKey { get; set; } = ModifierKey.Shift;
    public ModifierKey ToolsKey { get; set; } = ModifierKey.Ctrl;
    public ModifierKey ActionsKey { get; set; } = ModifierKey.Alt;

    /// <summary>Only open the wheel for drags that start in File Explorer or on the desktop.</summary>
    public bool ExplorerOnly { get; set; } = true;

    public OutputLocation OutputLocation { get; set; } = OutputLocation.SameFolder;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Base URL of the Velopack release feed. Empty disables update checks.</summary>
    public string UpdateUrl { get; set; } = "";

    public OperationCategory? CategoryFor(ModifierKey key) =>
        key == ConvertKey ? OperationCategory.Convert
        : key == ToolsKey ? OperationCategory.Tools
        : key == ActionsKey ? OperationCategory.Actions
        : null;

    /// <summary>Each wheel needs its own key; duplicates would make one wheel unreachable.</summary>
    public bool HasDistinctKeys => new[] { ConvertKey, ToolsKey, ActionsKey }.Distinct().Count() == 3;
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
                if (settings is not null && settings.HasDistinctKeys)
                    return settings;
            }
        }
        catch (JsonException)
        {
            // A corrupt settings file falls back to defaults rather than blocking startup.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
