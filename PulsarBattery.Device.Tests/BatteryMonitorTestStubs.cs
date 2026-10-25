namespace PulsarBattery.Services;

// The source-linked monitor tests inject notifications and never initialize the Windows notification service.
internal static class NotificationHelper
{
    public static bool NotifyLowBattery(int percentage, int threshold, string? model)
        => throw new InvalidOperationException("Inject a notification callback in monitor tests.");

    public static void StopLowBatterySound()
        => throw new InvalidOperationException("Inject a sound callback in monitor tests.");
}
