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

public sealed partial class SparklineControl : UserControl
{
    private const int MaxPlotPoints = 240;
    private const double PlotInset = 3;
    private static readonly TimeSpan PlotWindow = TimeSpan.FromHours(24);

    // A BatteryReading-typed DP makes XamlTypeInfo emit setters for the record's init-only properties.
    public static readonly DependencyProperty ReadingsProperty =
        DependencyProperty.Register(nameof(Readings), typeof(IEnumerable), typeof(SparklineControl), new PropertyMetadata(null, OnReadingsChanged));

    public static readonly DependencyProperty ModelNameProperty =
        DependencyProperty.Register(nameof(ModelName), typeof(string), typeof(SparklineControl), new PropertyMetadata(string.Empty, OnPlotOptionsChanged));

    public static readonly DependencyProperty LogIntervalMinutesProperty =
        DependencyProperty.Register(nameof(LogIntervalMinutes), typeof(double), typeof(SparklineControl), new PropertyMetadata(5.0, OnPlotOptionsChanged));

    public static readonly DependencyProperty PollIntervalMinutesProperty =
        DependencyProperty.Register(nameof(PollIntervalMinutes), typeof(double), typeof(SparklineControl), new PropertyMetadata(1.0, OnPlotOptionsChanged));

    public IEnumerable? Readings
    {
        get => (IEnumerable?)GetValue(ReadingsProperty);
        set => SetValue(ReadingsProperty, value);
    }

    public string ModelName
    {
        get => (string)GetValue(ModelNameProperty);
        set => SetValue(ModelNameProperty, value);
    }

    public double LogIntervalMinutes
    {
        get => (double)GetValue(LogIntervalMinutesProperty);
        set => SetValue(LogIntervalMinutesProperty, value);
    }

    public double PollIntervalMinutes
    {
        get => (double)GetValue(PollIntervalMinutesProperty);
        set => SetValue(PollIntervalMinutesProperty, value);
    }

    private readonly DispatcherTimer _refreshTimer;
    private INotifyCollectionChanged? _subscribed;
    private bool _redrawQueued;

    public SparklineControl()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += (_, _) => Redraw();
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

    private static void OnPlotOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SparklineControl)d).Redraw();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        _refreshTimer.Start();
        Redraw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // History outlives pages, so detach both sources of updates when navigation removes the control.
        Unsubscribe();
        _refreshTimer.Stop();
        _redrawQueued = false;
    }

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
        // History seeding adds up to 500 items individually; render the completed batch once.
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _redrawQueued = false;
            Redraw();
        }))
        {
            _redrawQueued = false;
        }
    }

    private void PlotHost_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (!IsLoaded)
        {
            return;
        }

        double width = PlotHost.ActualWidth;
        double height = PlotHost.ActualHeight;
        if (width <= PlotInset * 2 || height <= PlotInset * 2)
        {
            return;
        }

        var windowed = CollectWindowedReadings(DateTimeOffset.Now);
        if (windowed.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            ScaleLabels.Visibility = Visibility.Collapsed;
            ReferenceLine.Visibility = Visibility.Collapsed;
            TrendLine.Data = null;
            FillPath.Data = null;
            ReadingMarkers.Data = null;
            StartLabel.Text = string.Empty;
            EndLabel.Text = string.Empty;
            SetAccessibleSummary(Loc.T("No readings in the last 24 hours"));
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        ScaleLabels.Visibility = Visibility.Visible;
        ReferenceLine.Visibility = Visibility.Visible;

        var first = windowed[0];
        var last = windowed[^1];
        string timeFormat = first.Timestamp.ToLocalTime().Date == last.Timestamp.ToLocalTime().Date ? "t" : "g";
        StartLabel.Text = first.Timestamp.ToLocalTime().ToString(timeFormat, CultureInfo.CurrentCulture);
        EndLabel.Text = windowed.Count > 1 ? last.Timestamp.ToLocalTime().ToString(timeFormat, CultureInfo.CurrentCulture) : string.Empty;

        double span = (last.Timestamp - first.Timestamp).TotalSeconds;
        Point ToPoint(BatteryReading reading) => new(
            span > 0 ? PlotInset + (reading.Timestamp - first.Timestamp).TotalSeconds / span * (width - PlotInset * 2) : width / 2,
            PlotInset + (1 - reading.Percentage / 100.0) * (height - PlotInset * 2));

        var lines = new PathGeometry();
        var fills = new PathGeometry();
        var markers = new GeometryGroup();
        var segments = SplitSegments(windowed);
        int minimum = 100;
        int maximum = 0;
        foreach (var reading in windowed)
        {
            minimum = Math.Min(minimum, reading.Percentage);
            maximum = Math.Max(maximum, reading.Percentage);
        }

        foreach (var segment in segments)
        {
            var points = Downsample(segment, MaxPlotPoints);
            var start = ToPoint(points[0]);
            if (points.Count == 1)
            {
                markers.Children.Add(new EllipseGeometry { Center = start, RadiusX = PlotInset, RadiusY = PlotInset });
                continue;
            }

            var line = new PathFigure { StartPoint = start, IsFilled = false };
            var fill = new PathFigure { StartPoint = new Point(start.X, height - PlotInset), IsClosed = true };
            fill.Segments.Add(new LineSegment { Point = start });
            for (int i = 1; i < points.Count; i++)
            {
                var point = ToPoint(points[i]);
                line.Segments.Add(new LineSegment { Point = point });
                fill.Segments.Add(new LineSegment { Point = point });
            }

            fill.Segments.Add(new LineSegment { Point = new Point(ToPoint(points[^1]).X, height - PlotInset) });
            lines.Figures.Add(line);
            fills.Figures.Add(fill);
        }

        if (segments[^1].Count > 1)
        {
            markers.Children.Add(new EllipseGeometry { Center = ToPoint(last), RadiusX = PlotInset, RadiusY = PlotInset });
        }

        TrendLine.Data = lines;
        FillPath.Data = fills;
        ReadingMarkers.Data = markers;
        SetAccessibleSummary(string.Format(CultureInfo.CurrentCulture,
            Loc.T("{0} readings. Latest {1}% at {2}. Lowest {3}%, highest {4}%."),
            windowed.Count, last.Percentage, last.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), minimum, maximum));
    }

    private void SetAccessibleSummary(string summary)
    {
        AutomationProperties.SetName(this, $"{Loc.T("Battery trend")}. {summary}");
        ToolTipService.SetToolTip(this, summary);
    }

    private List<BatteryReading> CollectWindowedReadings(DateTimeOffset now)
    {
        var ordered = new SortedDictionary<DateTimeOffset, BatteryReading>();
        if (Readings is not { } readings)
        {
            return new List<BatteryReading>();
        }

        var cutoff = now - PlotWindow;
        foreach (var item in readings)
        {
            if (item is not BatteryReading reading
                || reading.Timestamp < cutoff || reading.Timestamp > now
                || reading.Percentage is < 0 or > 100
                || (!string.IsNullOrWhiteSpace(ModelName) && !string.Equals(reading.Model, ModelName, StringComparison.Ordinal)))
            {
                continue;
            }

            // Keep the first observation for equal instants; collection order is newest first.
            ordered.TryAdd(reading.Timestamp, reading);
        }

        return new List<BatteryReading>(ordered.Values);
    }

    private List<List<BatteryReading>> SplitSegments(List<BatteryReading> readings)
    {
        double poll = double.IsFinite(PollIntervalMinutes) ? Math.Clamp(PollIntervalMinutes, 1, 120) : 1;
        double log = double.IsFinite(LogIntervalMinutes) ? Math.Clamp(LogIntervalMinutes, 1, 240) : 5;
        var maximumGap = TimeSpan.FromMinutes(Math.Max(15, Math.Ceiling(log / poll) * poll * 3));
        var segments = new List<List<BatteryReading>> { new() { readings[0] } };
        for (int i = 1; i < readings.Count; i++)
        {
            var previous = readings[i - 1];
            var current = readings[i];
            if (current.Timestamp - previous.Timestamp > maximumGap
                || !string.Equals(current.Model, previous.Model, StringComparison.Ordinal))
            {
                segments.Add(new List<BatteryReading>());
            }

            segments[^1].Add(current);
        }

        return segments;
    }

    private static List<BatteryReading> Downsample(List<BatteryReading> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points;
        }

        // Preserve endpoints and each bucket's extrema so brief charge spikes are not averaged away.
        int bucketCount = (maxPoints - 2) / 2;
        var result = new List<BatteryReading>(maxPoints) { points[0] };
        for (int i = 0; i < bucketCount; i++)
        {
            int start = 1 + (int)((long)i * (points.Count - 2) / bucketCount);
            int end = 1 + (int)((long)(i + 1) * (points.Count - 2) / bucketCount);
            int minimumIndex = start;
            int maximumIndex = start;
            for (int j = start + 1; j < end; j++)
            {
                if (points[j].Percentage < points[minimumIndex].Percentage)
                {
                    minimumIndex = j;
                }
                if (points[j].Percentage > points[maximumIndex].Percentage)
                {
                    maximumIndex = j;
                }
            }

            result.Add(points[Math.Min(minimumIndex, maximumIndex)]);
            if (minimumIndex != maximumIndex)
            {
                result.Add(points[Math.Max(minimumIndex, maximumIndex)]);
            }
        }

        result.Add(points[^1]);
        return result;
    }
}
