using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PulsarBattery.Tools;

internal static class LocalizationService
{
    private static Dictionary<string, string> _strings = [];

    private static readonly string[] SupportedLocales = ["de-DE"];

    public static void Initialize()
    {
        string locale = ResolveLocale(CultureInfo.CurrentUICulture);
        _strings = LoadFile(locale);
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

        return "en-US";
    }

    private static Dictionary<string, string> LoadFile(string locale)
    {
        string exeDir = AppContext.BaseDirectory;
        string path = Path.Combine(exeDir, "Strings", $"{locale}.json");

        if (!File.Exists(path))
        {
            Debug.WriteLine($"[Localization] File not found: {path}");
            return [];
        }

        try
        {
            string json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize(json, CompactJsonContext.Default.DictionaryStringString);
            return result ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Localization] Failed to load {path}: {ex.Message}");
            return [];
        }
    }
}
