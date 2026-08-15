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

    private bool _isUpdatingSelection;

    /// <summary>
    /// Used by x:Bind so an InfoBar's Visibility tracks the same flag as its IsOpen. Derived
    /// rather than a separate VM property so the two can never drift out of sync.
    /// </summary>
    public static Visibility BoolToVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    public MouseSettingsPage()
    {
        InitializeComponent();
        ApplyLocalization();
        InitializeComboBoxes();

        Loaded += MouseSettingsPage_Loaded;
        Unloaded += MouseSettingsPage_Unloaded;
    }

    private void MouseSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.SetMouseSettingsPageActive(true);
            InitializeComboBoxes();
            SyncCapabilityState();
            SyncComboSelections();
            _ = viewModel.RefreshDeviceSettingsAsync();
        }
    }

    private void MouseSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.SetMouseSettingsPageActive(false);
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MainViewModel.PollingRateHz)
            or nameof(ViewModels.MainViewModel.LodMm10)
            or nameof(ViewModels.MainViewModel.DpiStage)
            or nameof(ViewModels.MainViewModel.SleepSeconds))
        {
            SyncComboSelections();
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.SettingsCapabilities))
        {
            InitializeComboBoxes();
            SyncCapabilityState();
            SyncComboSelections();
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.IsApplyingDeviceSetting)
            or nameof(ViewModels.MainViewModel.IsReadingDeviceSettings)
            or nameof(ViewModels.MainViewModel.IsLoading))
        {
            SyncCapabilityState();
        }
    }

    private void InitializeComboBoxes()
    {
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
            _isUpdatingSelection = false;
        }
    }

    private void SyncCapabilityState()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

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
    }

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private void SyncComboSelections()
    {
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
            _isUpdatingSelection = false;
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

        DpiStageCard.Header = Loc.T("DPI stage");
        DpiStageCard.Description = Loc.T("Switches the active DPI preset");
        AutomationProperties.SetName(DpiStageComboBox, Loc.T("DPI stage"));

        DebounceCard.Header = Loc.T("Debounce");
        DebounceCard.Description = Loc.T("Click debounce time in milliseconds");

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
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // Don't steal focus from inputs. Most controls (buttons, toggles, etc.) will naturally take focus on click.
        if (FindAncestor<NumberBox>(source) is not null || FindAncestor<TextBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ButtonBase>(source) is not null || FindAncestor<ToggleSwitch>(source) is not null)
        {
            return;
        }

        try
        {
            FocusSink.Focus(FocusState.Programmatic);
            e.Handled = true;
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
