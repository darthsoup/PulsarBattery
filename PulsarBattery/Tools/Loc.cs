namespace PulsarBattery.Tools;

internal static class Loc
{
    /// <summary>Returns the localized string for the given English key, or the key itself as fallback.</summary>
    public static string T(string key) => LocalizationService.GetString(key);
}
