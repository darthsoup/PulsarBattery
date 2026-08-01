using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Tools;
using System.ComponentModel;

namespace PulsarBattery.Pages;

public sealed partial class MouseSettingsPage : Page
{
    private static readonly int[] PollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly (int Mm10, string Label)[] LodOptions = [(7, "0.7 mm"), (10, "1.0 mm"), (20, "2.0 mm")];

    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    private bool _isUpdatingSelection;

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
            SyncComboSelections();
            _ = viewModel.RefreshDeviceSettingsAsync();
        }
    }

    private void MouseSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MainViewModel.PollingRateHz) or nameof(ViewModels.MainViewModel.LodMm10))
        {
            SyncComboSelections();
        }

        if (e.PropertyName is nameof(ViewModels.MainViewModel.DpiStageText))
        {
            UpdateDpiDescription();
        }
    }

    private void InitializeComboBoxes()
    {
        _isUpdatingSelection = true;
        try
        {
            PollingRateComboBox.Items.Clear();
            foreach (var rate in PollingRates)
            {
                PollingRateComboBox.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate });
            }

            LodComboBox.Items.Clear();
            foreach (var (mm10, label) in LodOptions)
            {
                LodComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = mm10 });
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void SyncComboSelections()
    {
        _isUpdatingSelection = true;
        try
        {
            SelectByTag(PollingRateComboBox, ViewModel?.PollingRateHz);
            SelectByTag(LodComboBox, ViewModel?.LodMm10);
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
        UpdateDpiDescription();

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
    }

    private void UpdateDpiDescription()
    {
        var stage = ViewModel?.DpiStageText;
        DpiCard.Description = string.IsNullOrEmpty(stage)
            ? Loc.T("Sensor resolution of the active profile")
            : $"{Loc.T("Sensor resolution of the active profile")} ({stage})";
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
            // ignore
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
