using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Core.Scope;

/// <summary>View settings per sample domain, because ADC counts and volts need different scales.</summary>
public sealed record ScopeSettingsProfile
{
    public ScopeViewSettings AdcCounts { get; init; } = DefaultsFor(SampleDomain.AdcCounts);
    public ScopeViewSettings Volts { get; init; } = DefaultsFor(SampleDomain.Volts);

    public ScopeViewSettings For(SampleDomain domain) =>
        (domain == SampleDomain.Volts ? Volts : AdcCounts).Normalize();

    public ScopeSettingsProfile With(SampleDomain domain, ScopeViewSettings settings) =>
        domain == SampleDomain.Volts ? this with { Volts = settings } : this with { AdcCounts = settings };

    public static ScopeViewSettings DefaultsFor(SampleDomain domain) => domain == SampleDomain.Volts
        ? new ScopeViewSettings
        {
            TriggerLevel = 1.0,
            Channel1 = new ChannelView { UnitsPerDivision = 0.5, CenterValue = 1.0 },
            Channel2 = new ChannelView { UnitsPerDivision = 0.5, CenterValue = 0 }
        }
        : new ScopeViewSettings
        {
            // Nominal family mapping: 256 ADC codes over 8 vertical divisions.
            TriggerLevel = 128,
            Channel1 = new ChannelView { UnitsPerDivision = 32, CenterValue = 128 },
            Channel2 = new ChannelView { UnitsPerDivision = 32, CenterValue = 128 }
        };
}

/// <summary>Loads and saves the settings file; a damaged file falls back to defaults.</summary>
public static class ScopeSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DSO1102_Controller",
        "settings.json");

    public static ScopeSettingsProfile Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new();

            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
                return new();

            // Merge stored values over the domain defaults, so a partial or older file
            // keeps the matching per-domain defaults for everything it does not contain.
            ScopeViewSettings Merge(SampleDomain domain, string name)
            {
                var defaults = JsonSerializer.SerializeToNode(ScopeSettingsProfile.DefaultsFor(domain), Options)!.AsObject();
                if (root[name] is JsonObject stored)
                    DeepMerge(defaults, stored);
                return (defaults.Deserialize<ScopeViewSettings>(Options) ?? ScopeSettingsProfile.DefaultsFor(domain)).Normalize();
            }

            return new ScopeSettingsProfile
            {
                AdcCounts = Merge(SampleDomain.AdcCounts, nameof(ScopeSettingsProfile.AdcCounts)),
                Volts = Merge(SampleDomain.Volts, nameof(ScopeSettingsProfile.Volts))
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return new();
        }
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
                DeepMerge(existing, child);
            else
                target[key] = value?.DeepClone();
        }
    }

    public static void Save(string path, ScopeSettingsProfile profile)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Options));
        File.Move(temp, path, overwrite: true);
    }
}
