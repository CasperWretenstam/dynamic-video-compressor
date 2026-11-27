using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dvc.Core.Definitions;
using Dvc.Desktop.Services;

namespace Dvc.Desktop.ViewModels;

/// <summary>One choice in a settings list: the value it sets, and how it's shown.</summary>
/// <param name="Value">The setting's value when this choice is picked.</param>
/// <param name="Label">The choice's name as shown in the list.</param>
/// <param name="Description">What picking this choice means, shown under the list. Empty when the label says it all.</param>
public sealed record SettingOption<T>(T Value, string Label, string Description = "")
{
    /// <summary>The label, which a list shows when it has no template for the choice.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The settings panel: the choices on offer for each compression setting,
/// the ones picked, and saving them to the settings store
/// (<c>SettingsStore</c>) as soon as one changes.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    // Set while the choices are being set from saved settings, so doing
    // that doesn't save them straight back.
    private bool _applying;

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        Apply(store.Current);
    }

    /// <summary>The quality tiers, each with its description.</summary>
    public IReadOnlyList<SettingOption<string>> QualityOptions { get; } =
    [
        .. QualityPreferenceDefinitions.Tiers.Select(tier =>
            new SettingOption<string>(tier.Key, SplitWords(tier.Key), tier.Value.Description)),
    ];

    /// <summary>The source's own resolution, then each cap on offer, largest first.</summary>
    public IReadOnlyList<SettingOption<int?>> ResolutionOptions { get; } =
    [
        new(null, "Original", "Keeps the source's resolution, however large."),
        .. CompressionSettings.ShortSideCaps.Select(shortSide =>
            new SettingOption<int?>(shortSide, $"Up to {ResolutionLabel(shortSide)}")),
    ];

    /// <summary>The source's own framerate, then each cap on offer, highest first.</summary>
    public IReadOnlyList<SettingOption<double?>> FramerateOptions { get; } =
    [
        new(null, "Original", "Keeps the source's framerate, however high."),
        .. CompressionSettings.FpsCaps.Select(fps =>
            new SettingOption<double?>(fps, $"Up to {fps.ToString(CultureInfo.InvariantCulture)} fps")),
    ];

    /// <summary>The encoder presets, fastest first, each with its description.</summary>
    public IReadOnlyList<SettingOption<string>> PresetOptions { get; } =
    [
        .. EncoderPresetDefinitions.Presets.Select(preset =>
            new SettingOption<string>(preset.Key, preset.Key, preset.Value)),
    ];

    /// <summary>The quality tier picked. Not used while aiming for a file size (<c>AimForFileSize</c>).</summary>
    [ObservableProperty]
    public partial SettingOption<string>? SelectedQuality { get; set; }

    /// <summary>The resolution cap picked, or the choice to keep the source's resolution.</summary>
    [ObservableProperty]
    public partial SettingOption<int?>? SelectedResolution { get; set; }

    /// <summary>The framerate cap picked, or the choice to keep the source's framerate.</summary>
    [ObservableProperty]
    public partial SettingOption<double?>? SelectedFramerate { get; set; }

    /// <summary>The encoder preset picked.</summary>
    [ObservableProperty]
    public partial SettingOption<string>? SelectedPreset { get; set; }

    /// <summary>
    /// Whether to aim for a file size (<c>TargetSizeMb</c>) instead of a
    /// quality tier (<c>SelectedQuality</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQualityEnabled))]
    public partial bool AimForFileSize { get; set; }

    /// <summary>
    /// The file size to aim for, in MB. Null while the box is empty; the
    /// last valid size stays in effect until a new one is entered.
    /// </summary>
    [ObservableProperty]
    public partial decimal? TargetSizeMb { get; set; }

    /// <summary>Whether the quality tier applies, which it doesn't while aiming for a file size.</summary>
    public bool IsQualityEnabled => !AimForFileSize;

    private bool CanResetToDefaults() => _store.Current != CompressionSettings.Default;

    [RelayCommand(CanExecute = nameof(CanResetToDefaults))]
    private void ResetToDefaults()
    {
        _store.Update(CompressionSettings.Default);
        Apply(CompressionSettings.Default);
        ResetToDefaultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedQualityChanged(SettingOption<string>? value) => Save();

    partial void OnSelectedResolutionChanged(SettingOption<int?>? value) => Save();

    partial void OnSelectedFramerateChanged(SettingOption<double?>? value) => Save();

    partial void OnSelectedPresetChanged(SettingOption<string>? value) => Save();

    partial void OnAimForFileSizeChanged(bool value) => Save();

    partial void OnTargetSizeMbChanged(decimal? value) => Save();

    // Picks the choices matching the given settings.
    private void Apply(CompressionSettings settings)
    {
        _applying = true;
        try
        {
            var defaults = CompressionSettings.Default;
            SelectedQuality = Find(QualityOptions, settings.Quality, defaults.Quality);
            SelectedResolution = Find(ResolutionOptions, settings.MaxShortSide, defaults.MaxShortSide);
            SelectedFramerate = Find(FramerateOptions, settings.MaxFps, defaults.MaxFps);
            SelectedPreset = Find(PresetOptions, settings.EncoderPreset, defaults.EncoderPreset);
            AimForFileSize = settings.AimForFileSize;
            TargetSizeMb = settings.TargetSizeMb;
        }
        finally
        {
            _applying = false;
        }
    }

    // Saves the choices as they stand. A list with nothing picked, which a
    // list can briefly report while it's being rebuilt, or a file size
    // that's empty or out of range keeps that setting as it was.
    private void Save()
    {
        if (_applying)
        {
            return;
        }

        var current = _store.Current;
        _store.Update(current with
        {
            Quality = SelectedQuality?.Value ?? current.Quality,
            MaxShortSide = SelectedResolution is { } resolution ? resolution.Value : current.MaxShortSide,
            MaxFps = SelectedFramerate is { } framerate ? framerate.Value : current.MaxFps,
            EncoderPreset = SelectedPreset?.Value ?? current.EncoderPreset,
            AimForFileSize = AimForFileSize,
            TargetSizeMb = TargetSizeMb is { } size && CompressionSettings.IsValidTargetSizeMb(size)
                ? size
                : current.TargetSizeMb,
        });
        ResetToDefaultsCommand.NotifyCanExecuteChanged();
    }

    // The choice for a value, or for the default if no choice has that
    // value. Names are compared ignoring case, as the definitions do.
    private static SettingOption<T> Find<T>(IReadOnlyList<SettingOption<T>> options, T value, T fallback)
    {
        var comparer = typeof(T) == typeof(string)
            ? (IEqualityComparer<T>)StringComparer.OrdinalIgnoreCase
            : EqualityComparer<T>.Default;
        return options.FirstOrDefault(option => comparer.Equals(option.Value, value))
            ?? options.First(option => comparer.Equals(option.Value, fallback));
    }

    // "SmallerSize" becomes "Smaller size".
    private static string SplitWords(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString()));

    // The resolution's own label, such as "1080p" or "4K".
    private static string ResolutionLabel(int shortSide) =>
        ResolutionDefinitions.ShortSides.First(entry => entry.Value == shortSide).Key;
}
