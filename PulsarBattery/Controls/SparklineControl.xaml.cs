using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Models;
using PulsarBattery.Tools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using Windows.Foundation;

namespace PulsarBattery.Controls;

/// <summary>
/// Trend-shape sparkline over the last 24 hours of battery readings.
/// Mixed-model history draws as one line and disconnected gaps are not
/// broken — it shows the shape, the History page has the detail.
/// </summary>
public sealed partial class SparklineControl : UserControl
{
    private const int MaxPlotPoints = 240;
    private static readonly TimeSpan PlotWindow = TimeSpan.FromHours(24);

    // Deliberately non-generic: a BatteryReading-typed DP would make the XAML
    // compiler emit XamlTypeInfo setters for the record's init-only properties
    // (CS8852). The dashboard binds MainViewModel.History here.
    public static readonly DependencyProperty ReadingsProperty =
        DependencyProperty.Register(nameof(Readings), typeof(IEnumerable), typeof(SparklineControl), new PropertyMetadata(null, OnReadingsChanged));

    public IEnumerable? Readings
    {
        get => (IEnumerable?)GetValue(ReadingsProperty);
        set => SetValue(ReadingsProperty, value);
    }

    private INotifyCollectionChanged? _subscribed;
    private bool _redrawQueued;

    public SparklineControl()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.T("Battery trend"));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnReadingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SparklineControl)d;
        if (control.IsLoaded)
        {
            control.Subscribe();
            control.Redraw();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        Redraw();
    }

    // The bound collection outlives this control (pages are rebuilt on every
    // navigation), so unhooking here is what prevents handler leaks.
    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        Unsubscribe();
        if (Readings is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnReadingsCollectionChanged;
            _subscribed = observable;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is { } observable)
        {
            observable.CollectionChanged -= OnReadingsCollectionChanged;
            _subscribed = null;
        }
    }

    private void OnReadingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // History seeding adds up to 500 items one by one — coalesce to a single redraw.
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _redrawQueued = false;
            Redraw();
        });
    }

    private void PlotHost_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        double width = PlotHost.ActualWidth;
        double height = PlotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var windowed = CollectWindowedReadings();
        if (windowed.Count < 2)
        {
            EmptyText.Visibility = Visibility.Visible;
            TrendLine.Points = new PointCollection();
            FillPath.Data = null;
            StartLabel.Text = string.Empty;
            EndLabel.Text = string.Empty;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        // Labels use the raw endpoints; downsampled bucket means are close enough
        // that the axis scale below stays visually identical.
        StartLabel.Text = windowed[0].Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        EndLabel.Text = windowed[^1].Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        var points = Downsample(windowed, MaxPlotPoints);

        var t0 = points[0].Timestamp;
        var t1 = points[^1].Timestamp;
        double span = Math.Max((t1 - t0).TotalSeconds, 1.0);

        var line = new PointCollection();
        foreach (var (timestamp, percentage) in points)
        {
            double x = (timestamp - t0).TotalSeconds / span * width;
            // Fixed 0-100 scale keeps the sparkline honest across redraws.
            double y = height - percentage / 100.0 * height;
            line.Add(new Point(x, y));
        }

        TrendLine.Points = line;
        FillPath.Data = BuildFillGeometry(line, height);
    }

    /// <summary>Readings newest-first in, oldest-first within the plot window out.</summary>
    private List<(DateTimeOffset Timestamp, double Percentage)> CollectWindowedReadings()
    {
        var result = new List<(DateTimeOffset, double)>();
        if (Readings is not { } readings)
        {
            return result;
        }

        var cutoff = DateTimeOffset.Now - PlotWindow;
        foreach (var item in readings)
        {
            if (item is not BatteryReading reading)
            {
                continue;
            }

            if (reading.Timestamp < cutoff)
            {
                break;
            }

            result.Add((reading.Timestamp, reading.Percentage));
        }

        result.Reverse();
        return result;
    }

    private static List<(DateTimeOffset Timestamp, double Percentage)> Downsample(List<(DateTimeOffset Timestamp, double Percentage)> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points;
        }

        var result = new List<(DateTimeOffset, double)>(maxPoints);
        double bucketSize = (double)points.Count / maxPoints;
        for (int i = 0; i < maxPoints; i++)
        {
            int start = (int)(i * bucketSize);
            int end = Math.Max(Math.Min((int)((i + 1) * bucketSize), points.Count), start + 1);

            double percentageSum = 0;
            double tickSum = 0;
            for (int j = start; j < end; j++)
            {
                percentageSum += points[j].Percentage;
                tickSum += points[j].Timestamp.UtcTicks;
            }

            int count = end - start;
            result.Add((new DateTimeOffset((long)(tickSum / count), TimeSpan.Zero), percentageSum / count));
        }

        return result;
    }

    private static PathGeometry BuildFillGeometry(PointCollection line, double height)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(line[0].X, height),
            IsClosed = true,
        };

        foreach (var point in line)
        {
            figure.Segments.Add(new LineSegment { Point = point });
        }

        figure.Segments.Add(new LineSegment { Point = new Point(line[^1].X, height) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
