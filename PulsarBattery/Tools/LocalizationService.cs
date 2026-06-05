using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PulsarBattery.Tools;

internal static class LocalizationService
{
    private const string DefaultLocale = "en-US";

    private static Dictionary<string, string> _strings = [];

    private static readonly string[] SupportedLocales = ["de-DE", DefaultLocale];

    public static void Initialize()
    {
        string locale = ResolveLocale(CultureInfo.CurrentUICulture);
        _strings = LoadEmbedded(locale);
    }

    public static string GetString(string key)
    {
        if (_strings.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
            return value;

        return key;
    }

    private static string ResolveLocale(CultureInfo culture)
    {
        // Exact match (e.g. "de-DE")
        if (Array.Exists(SupportedLocales, l => string.Equals(l, culture.Name, StringComparison.OrdinalIgnoreCase)))
            return culture.Name;

        // Parent match (e.g. "de" → "de-DE")
        foreach (string locale in SupportedLocales)
        {
            if (locale.StartsWith(culture.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase))
                return locale;
        }

        return DefaultLocale;
    }

    private static Dictionary<string, string> LoadEmbedded(string locale)
    {
        string resourceName = $"PulsarBattery.Strings.{locale}.json";

        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Debug.WriteLine($"[Localization] Embedded resource not found: {resourceName}");
                return [];
            }

            var result = JsonSerializer.Deserialize(stream, CompactJsonContext.Default.DictionaryStringString);
            return result ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Localization] Failed to load {resourceName}: {ex.Message}");
            return [];
        }
    }
}
