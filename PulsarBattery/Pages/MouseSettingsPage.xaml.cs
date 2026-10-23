using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Tools;
using System;
using System.ComponentModel;

namespace PulsarBattery.Pages;

public sealed partial class MouseSettingsPage : Page
{
    private static readonly int[] DefaultPollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly int[] DefaultLodValues = [7, 10, 20];
    private static readonly int[] DefaultSleepValues = [10, 30, 60, 300, 600, 1800];
    private const int DefaultDpiStageCount = 8;

    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    // Templating, range updates and teardown can raise input events without a user edit.
    private bool _isUpdatingSelection = true;
    private ViewModels.MainViewModel? _subscribedViewModel;

    /// <summary>Keeps a closed InfoBar from consuming section spacing.</summary>
    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    public MouseSettingsPage()
    {
        InitializeComponent();
        ApplyLocalization();

        Loaded += MouseSettingsPage_Loaded;
        Unloaded += MouseSettingsPage_Unloaded;
    }

    private void MouseSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isUpdatingSelection = true;
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        _subscribedViewModel = viewModel;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.SetMouseSettingsPageActive(true);
        InitializeComboBoxes();
        SyncCapabilityState();
        SyncComboSelections();
        SyncEditableValues();
        _isUpdatingSelection = false;
        _ = viewModel.RefreshDeviceSettingsAsync();
    }

    private void MouseSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isUpdatingSelection = true;
        SettingsProgressRing.IsActive = false;
        if (_subscribedViewModel is { } viewModel)
        {
            viewModel.SetMouseSettingsPageActive(false);
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _subscribedViewModel))
        {
            return;
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.PollingRateHz)
            or nameof(ViewModels.MainViewModel.LodMm10)
            or nameof(ViewModels.MainViewModel.DpiStage)
            or nameof(ViewModels.MainViewModel.SleepSeconds))
        {
            SyncComboSelections();
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.DpiValue)
            or nameof(ViewModels.MainViewModel.DebounceMsValue)
            or nameof(ViewModels.MainViewModel.MotionSyncIsOn)
            or nameof(ViewModels.MainViewModel.AngleSnapIsOn)
            or nameof(ViewModels.MainViewModel.RippleControlIsOn))
        {
            SyncEditableValues(e.PropertyName);
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.SettingsCapabilities))
        {
            InitializeComboBoxes();
            SyncCapabilityState();
            SyncComboSelections();
            SyncEditableValues();
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.IsApplyingDeviceSetting)
            or nameof(ViewModels.MainViewModel.IsReadingDeviceSettings)
            or nameof(ViewModels.MainViewModel.IsLoading)
            or nameof(ViewModels.MainViewModel.MouseSettingsUnsupportedVisibility)
            || e.PropertyName?.StartsWith("CanWrite", StringComparison.Ordinal) == true)
        {
            SyncCapabilityState();
            if ((e.PropertyName is nameof(ViewModels.MainViewModel.IsApplyingDeviceSetting)
                    && _subscribedViewModel?.IsApplyingDeviceSetting == false)
                || (e.PropertyName is nameof(ViewModels.MainViewModel.IsReadingDeviceSettings)
                    && _subscribedViewModel?.IsReadingDeviceSettings == false))
            {
                // Unchanged snapshots raise no value notifications; restore confirmed values explicitly.
                SyncComboSelections();
                SyncEditableValues();
            }
        }
    }

    private void InitializeComboBoxes()
    {
        var wasUpdating = _isUpdatingSelection;
        _isUpdatingSelection = true;
        try
        {
            PollingRateComboBox.Items.Clear();
            var pollingRates = ViewModel?.SupportedPollingRates.Count > 0
                ? ViewModel.SupportedPollingRates
                : DefaultPollingRates;
            foreach (var rate in pollingRates)
            {
                PollingRateComboBox.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate });
            }

            LodComboBox.Items.Clear();
            var lodValues = ViewModel?.SupportedLodValues.Count > 0
                ? ViewModel.SupportedLodValues
                : DefaultLodValues;
            foreach (var mm10 in lodValues)
            {
                LodComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{mm10 / 10.0:0.0} mm",
                    Tag = mm10,
                });
            }

            DpiStageComboBox.Items.Clear();
            var stageCount = ViewModel?.SupportedDpiStageCount > 0
                ? ViewModel.SupportedDpiStageCount
                : DefaultDpiStageCount;
            for (var stage = 1; stage <= stageCount; stage++)
            {
                DpiStageComboBox.Items.Add(new ComboBoxItem
                {
                    Content = string.Format(Loc.T("Stage {0}"), stage),
                    Tag = stage,
                });
            }

            SleepComboBox.Items.Clear();
            var sleepValues = ViewModel?.SupportedSleepValues.Count > 0
                ? ViewModel.SupportedSleepValues
                : DefaultSleepValues;
            foreach (var seconds in sleepValues)
            {
                SleepComboBox.Items.Add(new ComboBoxItem
                {
                    Content = seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min",
                    Tag = seconds,
                });
            }
        }
        finally
        {
            _isUpdatingSelection = wasUpdating;
        }
    }

    private void SyncCapabilityState()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var wasUpdating = _isUpdatingSelection;
        _isUpdatingSelection = true;
        try
        {
            PollingRateComboBox.IsEnabled = viewModel.CanWritePollingRate;
            DpiNumberBox.IsEnabled = viewModel.CanWriteDpi;
            DpiNumberBox.Minimum = viewModel.DpiMinimum;
            DpiNumberBox.Maximum = viewModel.DpiMaximum;
            DpiNumberBox.SmallChange = viewModel.DpiSmallChange;
            DpiStageComboBox.IsEnabled = viewModel.CanWriteDpiStage;
            DebounceNumberBox.IsEnabled = viewModel.CanWriteDebounce;
            DebounceNumberBox.Minimum = viewModel.DebounceMinimum;
            DebounceNumberBox.Maximum = viewModel.DebounceMaximum;
            LodComboBox.IsEnabled = viewModel.CanWriteLod;
            MotionSyncToggle.IsEnabled = viewModel.CanWriteMotionSync;
            AngleSnapToggle.IsEnabled = viewModel.CanWriteAngleSnap;
            RippleControlToggle.IsEnabled = viewModel.CanWriteRippleControl;
            SleepComboBox.IsEnabled = viewModel.CanWriteSleep;

            PollingRateCard.Visibility = ToVisibility(viewModel.CanReadPollingRate);
            DpiCard.Visibility = ToVisibility(viewModel.CanReadDpi);
            DpiStageCard.Visibility = ToVisibility(viewModel.CanReadDpiStage);
            DebounceCard.Visibility = ToVisibility(viewModel.CanReadDebounce);
            LodCard.Visibility = ToVisibility(viewModel.CanReadLod);
            MotionSyncCard.Visibility = ToVisibility(viewModel.CanReadMotionSync);
            AngleSnapCard.Visibility = ToVisibility(viewModel.CanReadAngleSnap);
            RippleControlCard.Visibility = ToVisibility(viewModel.CanReadRippleControl);
            SleepCard.Visibility = ToVisibility(viewModel.CanReadSleep);

            PerformanceSection.Visibility = ToVisibility(
                viewModel.CanReadPollingRate
                || viewModel.CanReadDpi
                || viewModel.CanReadDpiStage
                || viewModel.CanReadDebounce);
            SensorSection.Visibility = ToVisibility(
                viewModel.CanReadLod
                || viewModel.CanReadMotionSync
                || viewModel.CanReadAngleSnap
                || viewModel.CanReadRippleControl);
            PowerSection.Visibility = ToVisibility(viewModel.CanReadSleep);

            var busy = viewModel.IsApplyingDeviceSetting || viewModel.IsReadingDeviceSettings || viewModel.IsLoading;
            RefreshSettingsButton.IsEnabled = !busy;
            SettingsProgressRing.IsActive = busy;
            RefreshSettingsIcon.Visibility = ToVisibility(!busy);
            var refreshLabel = busy
                ? viewModel.IsApplyingDeviceSetting ? "Applying mouse setting..." : "Reading mouse settings..."
                : "Refresh settings";
            SettingsProgressText.Text = refreshLabel;
            AutomationProperties.SetName(RefreshSettingsButton, Loc.T(refreshLabel));
            ToolTipService.SetToolTip(RefreshSettingsButton, Loc.T(refreshLabel));
            UnsupportedSettingsPanel.Visibility = busy
                ? Visibility.Collapsed
                : viewModel.MouseSettingsUnsupportedVisibility;
        }
        finally
        {
            _isUpdatingSelection = wasUpdating;
        }
    }

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private void SyncComboSelections()
    {
        var wasUpdating = _isUpdatingSelection;
        _isUpdatingSelection = true;
        try
        {
            SelectByTag(PollingRateComboBox, ViewModel?.PollingRateHz);
            SelectByTag(LodComboBox, ViewModel?.LodMm10);
            SelectByTag(DpiStageComboBox, ViewModel?.DpiStage);
            SelectByTag(SleepComboBox, ViewModel?.SleepSeconds);
        }
        finally
        {
            _isUpdatingSelection = wasUpdating;
        }
    }

    private void SyncEditableValues(string? propertyName = null)
    {
        if (_subscribedViewModel is not { } viewModel)
        {
            return;
        }

        var wasUpdating = _isUpdatingSelection;
        _isUpdatingSelection = true;
        try
        {
            if (propertyName is null or nameof(ViewModels.MainViewModel.DpiValue))
            {
                DpiNumberBox.Value = viewModel.DpiValue;
            }

            if (propertyName is null or nameof(ViewModels.MainViewModel.DebounceMsValue))
            {
                DebounceNumberBox.Value = viewModel.DebounceMsValue;
            }

            if (propertyName is null or nameof(ViewModels.MainViewModel.MotionSyncIsOn))
            {
                MotionSyncToggle.IsOn = viewModel.MotionSyncIsOn;
            }

            if (propertyName is null or nameof(ViewModels.MainViewModel.AngleSnapIsOn))
            {
                AngleSnapToggle.IsOn = viewModel.AngleSnapIsOn;
            }

            if (propertyName is null or nameof(ViewModels.MainViewModel.RippleControlIsOn))
            {
                RippleControlToggle.IsOn = viewModel.RippleControlIsOn;
            }
        }
        finally
        {
            _isUpdatingSelection = wasUpdating;
        }
    }

    private void DpiNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingSelection || _subscribedViewModel is not { CanWriteDpi: true } viewModel)
        {
            return;
        }

        if (double.IsFinite(args.NewValue))
        {
            viewModel.DpiValue = args.NewValue;
        }

        if (!viewModel.IsApplyingDeviceSetting)
        {
            SyncEditableValues(nameof(ViewModels.MainViewModel.DpiValue));
        }
    }

    private void DebounceNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingSelection || _subscribedViewModel is not { CanWriteDebounce: true } viewModel)
        {
            return;
        }

        if (double.IsFinite(args.NewValue))
        {
            viewModel.DebounceMsValue = args.NewValue;
        }

        if (!viewModel.IsApplyingDeviceSetting)
        {
            SyncEditableValues(nameof(ViewModels.MainViewModel.DebounceMsValue));
        }
    }

    private void MotionSyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingSelection && _subscribedViewModel is { CanWriteMotionSync: true } viewModel)
        {
            viewModel.MotionSyncIsOn = MotionSyncToggle.IsOn;
        }
    }

    private void AngleSnapToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingSelection && _subscribedViewModel is { CanWriteAngleSnap: true } viewModel)
        {
            viewModel.AngleSnapIsOn = AngleSnapToggle.IsOn;
        }
    }

    private void RippleControlToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingSelection && _subscribedViewModel is { CanWriteRippleControl: true } viewModel)
        {
            viewModel.RippleControlIsOn = RippleControlToggle.IsOn;
        }
    }

    private async void RefreshSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingSelection && _subscribedViewModel is { } viewModel)
        {
            await viewModel.RefreshDeviceSettingsAsync();
        }
    }

    private static void SelectByTag(ComboBox comboBox, int? value)
    {
        var index = -1;
        if (value is int target)
        {
            for (var i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem { Tag: int tag } && tag == target)
                {
                    index = i;
                    break;
                }
            }
        }

        if (comboBox.SelectedIndex != index)
        {
            comboBox.SelectedIndex = index;
        }
    }

    private void PollingRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
        {
            return;
        }

        if ((PollingRateComboBox.SelectedItem as ComboBoxItem)?.Tag is int hz)
        {
            ViewModel?.ApplyPollingRate(hz);
        }
    }

    private void LodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
        {
            return;
        }

        if ((LodComboBox.SelectedItem as ComboBoxItem)?.Tag is int mm10)
        {
            ViewModel?.ApplyLod(mm10);
        }
    }

    private void DpiStageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
        {
            return;
        }

        if ((DpiStageComboBox.SelectedItem as ComboBoxItem)?.Tag is int stage)
        {
            ViewModel?.ApplyDpiStage(stage);
        }
    }

    private void SleepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
        {
            return;
        }

        if ((SleepComboBox.SelectedItem as ComboBoxItem)?.Tag is int seconds)
        {
            ViewModel?.ApplySleep(seconds);
        }
    }

    private void ApplyInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel?.ClearMouseSettingsError();
    }

    private void ApplyLocalization()
    {
        PollingRateCard.Header = Loc.T("Polling rate");
        PollingRateCard.Description = Loc.T("Higher rates may only apply in wired or 8K dongle mode");
        AutomationProperties.SetName(PollingRateComboBox, Loc.T("Polling rate"));

        DpiCard.Header = Loc.T("DPI");
        DpiCard.Description = Loc.T("Sensor resolution of the active profile");
        AutomationProperties.SetName(DpiNumberBox, Loc.T("DPI"));
        AutomationProperties.SetHelpText(DpiNumberBox, Loc.T("Sensor resolution of the active profile"));

        DpiStageCard.Header = Loc.T("DPI stage");
        DpiStageCard.Description = Loc.T("Switches the active DPI preset");
        AutomationProperties.SetName(DpiStageComboBox, Loc.T("DPI stage"));

        DebounceCard.Header = Loc.T("Debounce");
        DebounceCard.Description = Loc.T("Click debounce time in milliseconds");
        AutomationProperties.SetName(DebounceNumberBox, Loc.T("Debounce"));
        AutomationProperties.SetHelpText(DebounceNumberBox, Loc.T("Click debounce time in milliseconds"));

        LodCard.Header = Loc.T("Lift-off distance");
        LodCard.Description = Loc.T("Height at which the sensor stops tracking when the mouse is lifted");
        AutomationProperties.SetName(LodComboBox, Loc.T("Lift-off distance"));

        MotionSyncCard.Header = Loc.T("Motion sync");
        MotionSyncCard.Description = Loc.T("Aligns sensor readings with the polling interval for smoother tracking");
        MotionSyncToggle.OnContent = Loc.T("On");
        MotionSyncToggle.OffContent = Loc.T("Off");
        AutomationProperties.SetName(MotionSyncToggle, Loc.T("Motion sync"));

        AngleSnapCard.Header = Loc.T("Angle snapping");
        AngleSnapCard.Description = Loc.T("Straightens small hand movements into smooth lines");
        AngleSnapToggle.OnContent = Loc.T("On");
        AngleSnapToggle.OffContent = Loc.T("Off");
        AutomationProperties.SetName(AngleSnapToggle, Loc.T("Angle snapping"));

        RippleControlCard.Header = Loc.T("Ripple control");
        RippleControlCard.Description = Loc.T("Smooths cursor jitter at high DPI values");
        RippleControlToggle.OnContent = Loc.T("On");
        RippleControlToggle.OffContent = Loc.T("Off");
        AutomationProperties.SetName(RippleControlToggle, Loc.T("Ripple control"));

        SleepCard.Header = Loc.T("Sleep timer");
        SleepCard.Description = Loc.T("Idle time before the mouse sleeps to save battery");
        AutomationProperties.SetName(SleepComboBox, Loc.T("Sleep timer"));
        RefreshSettingsIdleMeasure.Text = Loc.T("Refresh settings");
        RefreshSettingsReadMeasure.Text = Loc.T("Reading mouse settings...");
        RefreshSettingsApplyMeasure.Text = Loc.T("Applying mouse setting...");
        AutomationProperties.SetName(RefreshSettingsButton, Loc.T("Refresh settings"));
        ToolTipService.SetToolTip(RefreshSettingsButton, Loc.T("Refresh settings"));
    }

    private void PageHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshSettingsLabels.Visibility = ToVisibility(e.NewSize.Width >= 520);
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (FindAncestor<NumberBox>(source) is not null
            || FindAncestor<TextBox>(source) is not null
            || FindAncestor<ComboBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ButtonBase>(source) is not null || FindAncestor<ToggleSwitch>(source) is not null)
        {
            return;
        }

        try
        {
            // A visible focus target commits NumberBox edits without an invisible keyboard tab stop.
            e.Handled = RefreshSettingsButton.Focus(FocusState.Programmatic);
        }
        catch
        {
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
