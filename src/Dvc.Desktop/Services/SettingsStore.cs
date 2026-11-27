using System.Text.Json;
using System.Text.Json.Nodes;
using Dvc.Core.Definitions;
using Microsoft.Extensions.Logging;

namespace Dvc.Desktop.Services;

/// <summary>
/// Holds the user's compression settings and keeps them in a JSON file, so
/// they're remembered between runs. The file is read once, when the store
/// is created, and written whenever the settings change.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger<SettingsStore> _logger;

    /// <param name="filePath">
    /// The settings file. Null uses the default file (<c>DefaultFilePath</c>).
    /// </param>
    public SettingsStore(ILogger<SettingsStore> logger, string? filePath = null)
    {
        _logger = logger;
        FilePath = filePath ?? DefaultFilePath;
        Current = Load();
    }

    /// <summary>
    /// The file settings are kept in unless another is given:
    /// <c>Dynamic Video Compressor\settings.json</c> in the user's roaming
    /// application data folder (<c>%AppData%</c> on Windows).
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dynamic Video Compressor", "settings.json");

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>
    /// The settings in effect: what the file held when the store was
    /// created, or the latest update since.
    /// </summary>
    public CompressionSettings Current { get; private set; }

    /// <summary>
    /// Makes <c>settings</c> the settings in effect and writes them to the
    /// file. A failed write is logged, and the settings still apply until
    /// the app closes.
    /// </summary>
    public void Update(CompressionSettings settings)
    {
        Current = settings;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Written beside the file and then moved over it, so a crash
            // mid-write can't leave a half-written file behind.
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, WriteOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the settings to {FilePath}", FilePath);
        }
    }

    /// <summary>
    /// The settings in a settings file's text. Each setting that's missing
    /// or no longer valid, such as a preset that's been removed, gets its
    /// default and the rest are kept. Text that isn't a JSON object gives
    /// the defaults.
    /// </summary>
    public static CompressionSettings Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        var defaults = CompressionSettings.Default;
        if (root is null)
        {
            return defaults;
        }

        return new CompressionSettings
        {
            Quality = ReadName(root, "quality", QualityPreferenceDefinitions.Tiers.Keys) ?? defaults.Quality,
            MaxShortSide = ReadCap(root, "maxShortSide", defaults.MaxShortSide,
                (int value) => CompressionSettings.IsValidMaxShortSide(value)),
            MaxFps = ReadCap(root, "maxFps", defaults.MaxFps,
                (double value) => CompressionSettings.IsValidMaxFps(value)),
            EncoderPreset = ReadName(root, "encoderPreset", EncoderPresetDefinitions.Presets.Keys) ?? defaults.EncoderPreset,
            AimForFileSize = Read<bool>(root, "aimForFileSize") ?? defaults.AimForFileSize,
            TargetSizeMb = Read<decimal>(root, "targetSizeMb") is { } size && CompressionSettings.IsValidTargetSizeMb(size)
                ? size
                : defaults.TargetSizeMb,
        };
    }

    private CompressionSettings Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A first run: nothing saved yet.
            return CompressionSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the settings from {FilePath}; using the defaults", FilePath);
            return CompressionSettings.Default;
        }

        return Parse(json);
    }

    // A value of the given type, or null if it's missing or of another type.
    private static T? Read<T>(JsonObject root, string name) where T : struct =>
        root[name] is JsonValue value && value.TryGetValue<T>(out var result) ? result : null;

    // One of the accepted names, as the definitions spell it, or null if
    // it's missing or not accepted. Names are compared ignoring case.
    private static string? ReadName(JsonObject root, string name, IEnumerable<string> accepted) =>
        root[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? accepted.FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
            : null;

    // A cap: an explicit null keeps the source's value, a valid number caps
    // it, and anything else, including no value at all, gets the default.
    private static T? ReadCap<T>(JsonObject root, string name, T? fallback, Func<T, bool> isValid) where T : struct
    {
        if (!root.TryGetPropertyValue(name, out var node))
        {
            return fallback;
        }

        if (node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<T>(out var result) && isValid(result) ? result : fallback;
    }
}
