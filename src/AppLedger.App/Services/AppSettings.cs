using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppLedger.Infrastructure.Storage;

namespace AppLedger.App.Services;

/// <summary>
/// The settings the UI owns (docs/12_PRIVACY_AND_RETENTION.md §Defaults, docs/06_DATA_MODEL.md §Ownership).
/// </summary>
/// <remarks>
/// Every default here is a <b>product decision</b> rather than a value chosen for convenience and tuned
/// later (CLAUDE.md §Non-negotiables 3). Retention is six months because that is the promise; host logging
/// for browsers is not in this file at all, because it is not something onboarding asks — it is a per-app
/// policy the Agent applies, and the Privacy Gate's job is to say so.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Retention in days. docs/12: 180 by default, 30-365 allowed.</summary>
    public int RetentionDays { get; init; } = 180;

    /// <summary>The lowest retention the slider offers.</summary>
    public const int MinRetentionDays = 30;

    /// <summary>The highest retention the slider offers.</summary>
    public const int MaxRetentionDays = 365;

    /// <summary><c>en</c>, <c>vi</c>, <c>ja</c> or <c>system</c>.</summary>
    public string Language { get; init; } = "system";

    /// <summary><c>Light</c>, <c>Dark</c> or <c>System</c>.</summary>
    public string Theme { get; init; } = "System";

    /// <summary>
    /// True once the user has been through the Privacy Gate.
    /// </summary>
    /// <remarks>
    /// A first run must reach the Gate, and only the Gate marks it done. Defaulting this to true "because
    /// the file exists" would let a partially written settings file skip the one screen the product is
    /// obliged to show.
    /// </remarks>
    public bool OnboardingCompleted { get; init; }
}

/// <summary>Reads and writes <c>settings.json</c> under the data root.</summary>
/// <remarks>
/// The UI is the only writer (docs/06 §Ownership); the Agent reads it. A missing or unreadable file is not
/// an error — it is a first run, and the defaults above are what a first run should get.
/// </remarks>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string _path;

    /// <summary>Creates the store over a data root.</summary>
    public AppSettingsStore(DataRoot? dataRoot = null)
    {
        var root = dataRoot ?? DataRoot.Default;
        root.EnsureCreated();
        _path = root.SettingsPath;
    }

    /// <summary>Loads the settings, or the defaults when there are none.</summary>
    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file means a first run, not a crash. The worst outcome is that the user
            // sees the Privacy Gate again, which is never the wrong thing to show.
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Written whole rather than merged: this file is small, the UI is its only writer, and a partial
        // write is how a settings file ends up half in one state and half in another.
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
    }
}
