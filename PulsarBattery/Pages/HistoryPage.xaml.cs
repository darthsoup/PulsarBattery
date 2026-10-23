using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Models;
using PulsarBattery.Tools;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PulsarBattery.Pages;

public sealed partial class HistoryPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;
    private ViewModels.MainViewModel? _subscribedViewModel;
    private bool _isPerformingAction;

    public HistoryPage()
    {
        InitializeComponent();
        PreviousButton.Content = Loc.T("Previous");
        AutomationProperties.SetName(PreviousButton, Loc.T("Previous history page"));
        NextButton.Content = Loc.T("Next");
        AutomationProperties.SetName(NextButton, Loc.T("Next history page"));
        ClearButton.Content = Loc.T("Clear");
        AutomationProperties.SetName(ClearButton, Loc.T("Clear history"));
        AutomationProperties.SetName(ExportButton, Loc.T("Export all battery readings as CSV"));
        ToolTipService.SetToolTip(ExportButton, Loc.T("Export all battery readings as CSV"));
        AutomationProperties.SetName(HistoryList, Loc.T("Battery history"));
        Loaded += HistoryPage_Loaded;
        Unloaded += HistoryPage_Unloaded;
    }

    private void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.History.CollectionChanged -= History_CollectionChanged;
        }

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.History.CollectionChanged += History_CollectionChanged;
        }

        UpdateActionAvailability();
    }

    private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.History.CollectionChanged -= History_CollectionChanged;
            _subscribedViewModel = null;
        }
    }

    private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateActionAvailability();

    private void UpdateActionAvailability()
    {
        var hasHistory = (_subscribedViewModel?.History.Count ?? 0) > 0;
        var canAct = !_isPerformingAction && hasHistory;
        HistoryList.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = canAct;
        ClearButton.IsEnabled = canAct;
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.PreviousHistoryPage();
        HistoryScrollViewer.ChangeView(null, 0, null, true);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.NextHistoryPage();
        HistoryScrollViewer.ChangeView(null, 0, null, true);
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_isPerformingAction || ViewModel is not { } viewModel || App.MainWindow is not { } window)
        {
            return;
        }

        var readings = viewModel.History.ToArray();
        if (readings.Length == 0)
        {
            return;
        }

        _isPerformingAction = true;
        UpdateActionAvailability();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"PulsarBattery-history-{DateTime.Now:yyyy-MM-dd-HHmmss}"
            };
            picker.FileTypeChoices.Add(Loc.T("CSV file"), new[] { ".csv" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, CreateCsv(readings), new UTF8Encoding(true));
            ShowActionMessage(InfoBarSeverity.Success,
                string.Format(CultureInfo.CurrentCulture, Loc.T("Exported {0} readings to {1}."), readings.Length, file.Name));
        }
        catch (Exception ex)
        {
            Log.Error(nameof(HistoryPage), ex);
            ShowActionMessage(InfoBarSeverity.Error, Loc.T("History could not be exported. Try a different location."));
        }
        finally
        {
            _isPerformingAction = false;
            UpdateActionAvailability();
        }
    }

    private static string CreateCsv(BatteryReading[] readings)
    {
        var csv = new StringBuilder("timestamp,battery_percent,is_charging,model\r\n");
        foreach (var reading in readings)
        {
            csv.Append(reading.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(reading.Percentage.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(reading.IsCharging ? "true" : "false").Append(',')
                .Append(EscapeCsvText(reading.Model)).Append("\r\n");
        }

        return csv.ToString();
    }

    private static string EscapeCsvText(string? value)
    {
        value ??= string.Empty;
        // Spreadsheet applications interpret formula prefixes even inside quoted CSV cells.
        var trimmed = value.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_isPerformingAction || ViewModel is not { } viewModel || viewModel.History.Count == 0)
        {
            return;
        }

        _isPerformingAction = true;
        UpdateActionAvailability();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.T("Clear history?"),
                Content = Loc.T("All recorded battery readings will be permanently deleted."),
                PrimaryButtonText = Loc.T("Clear"),
                CloseButtonText = Loc.T("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await viewModel.ClearHistoryAsync();
            ShowActionMessage(InfoBarSeverity.Success, Loc.T("History cleared."));
        }
        catch (Exception ex)
        {
            Log.Error(nameof(HistoryPage), ex);
            ShowActionMessage(InfoBarSeverity.Error, Loc.T("History could not be cleared. Please try again."));
        }
        finally
        {
            _isPerformingAction = false;
            UpdateActionAvailability();
        }
    }

    private void ShowActionMessage(InfoBarSeverity severity, string message)
    {
        if (!IsLoaded)
        {
            return;
        }

        ActionInfoBar.Severity = severity;
        ActionInfoBar.Message = message;
        ActionInfoBar.Visibility = Visibility.Visible;
        ActionInfoBar.IsOpen = true;
    }

    private void ActionInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        => ActionInfoBar.Visibility = Visibility.Collapsed;
}
