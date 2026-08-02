using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PulsarBattery.Services;

/// <summary>
/// Everything the tray icon needs to render; value equality drives redraw
/// deduplication in <see cref="TrayIconRenderer.RenderIfChanged"/>.
/// </summary>
public readonly record struct TrayIconState(
    bool ShowBattery,
    bool HasData,
    int Percentage,
    bool IsCharging,
    bool IsLow);

/// <summary>
/// Renders the tray icon in code at the exact physical tray icon size
/// (system small-icon metric at the primary monitor's DPI), so the shell
/// never rescales it. Digits are rasterized as GraphicsPath outlines at 4x
/// and downscaled with bicubic — smoother than grid-fit hinting at native
/// size, and the halo becomes a true geometric stroke instead of a blocky
/// offset union. The produced Icons are NON-owning wrappers; this class owns
/// the HICONs and destroys a superseded handle only after the caller
/// confirms the new one was assigned (the shell and H.NotifyIcon keep the
/// current handle for explorer-restart re-adds).
/// </summary>
internal sealed class TrayIconRenderer : IDisposable
{
    private const int SmCxSmIcon = 49;
    private const uint MonitorDefaultToPrimary = 1;

    // 1px contrast halo (black in dark theme, white in light) keeps the
    // digits readable over translucent/wallpaper-tinted taskbars. The em
    // factors below leave room for it so it never clips at the canvas edge.
    private const int OutlinePx = 1;

    // Supersampling factor for text rendering; the halo stroke and glyph
    // curves are rasterized at this scale and bicubic-downscaled.
    private const int TextScale = 4;

    private static readonly Color ChargingColor = Color.FromArgb(76, 201, 76);
    private static readonly Color LowColor = Color.FromArgb(232, 17, 35);

    private (TrayIconState State, int SizePx, bool LightTheme)? _lastKey;
    private (TrayIconState State, int SizePx, bool LightTheme)? _pendingKey;

    // Handle lifetime is delayed by one generation: Shell_NotifyIcon failures
    // are swallowed inside H.NotifyIcon, so after any assignment either the
    // new OR the previous handle may be the one the library has stored for
    // explorer-restart re-adds. Destroying only the grandparent keeps every
    // possibly-stored handle alive.
    private nint _currentHicon;
    private nint _retiredHicon;
    private nint _pendingHicon;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    /// <summary>
    /// Returns a fresh Icon when the rendered result would differ from the
    /// last committed one (or when forced), else null. Callers must assign
    /// the Icon and then call <see cref="CommitAssignment"/>, or call
    /// <see cref="AbandonAssignment"/> if the assignment failed. Never
    /// returns a cached Icon instance: H.NotifyIcon's Icon setter disposes
    /// the outgoing instance, so a reused Icon would be use-after-dispose.
    /// </summary>
    public Icon? RenderIfChanged(TrayIconState state, bool force = false)
    {
        if (_disposed)
        {
            return null;
        }

        var sizePx = GetTrayIconSizePx();
        var key = (state, sizePx, IsSystemLightTheme());
        if (!force && _lastKey is { } last && last == key)
        {
            return null;
        }

        using var bitmap = state.ShowBattery
            ? RenderBatteryText(state, sizePx, key.Item3)
            : RenderGearIcon(sizePx);

        var hicon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hicon); // non-owning wrapper

        // A previous pending handle means the last assignment was never
        // confirmed; treat it as abandoned.
        if (_pendingHicon != 0)
        {
            DestroyIcon(_pendingHicon);
        }

        _pendingHicon = hicon;
        _pendingKey = key;
        return icon;
    }

    /// <summary>
    /// The rendered icon was assigned. The superseded handle is only retired
    /// (a silently failed NIM_MODIFY would leave it as the library's stored
    /// handle); the previously retired one is destroyed.
    /// </summary>
    public void CommitAssignment()
    {
        if (_retiredHicon != 0)
        {
            DestroyIcon(_retiredHicon);
        }

        _retiredHicon = _currentHicon;
        _currentHicon = _pendingHicon;
        _pendingHicon = 0;
        _lastKey = _pendingKey;
        _pendingKey = null;
    }

    /// <summary>
    /// The assignment threw. The library may still have stored the new handle
    /// before failing, so it is retired rather than destroyed; the dedupe key
    /// stays unchanged so a later (forced) tick retries.
    /// </summary>
    public void AbandonAssignment()
    {
        if (_pendingHicon != 0)
        {
            if (_retiredHicon != 0)
            {
                DestroyIcon(_retiredHicon);
            }

            _retiredHicon = _pendingHicon;
            _pendingHicon = 0;
        }

        _pendingKey = null;
    }

    private static Bitmap RenderBatteryText(TrayIconState state, int sizePx, bool lightTheme)
    {
        var text = state.HasData ? state.Percentage.ToString(CultureInfo.InvariantCulture) : "–";
        var color = state switch
        {
            { HasData: true, IsCharging: true } => ChargingColor,
            { HasData: true, IsLow: true } => LowColor,
            _ => lightTheme ? Color.Black : Color.White,
        };

        // Fixed em per digit count (tuned offline with HidProbe icon-preview);
        // a shrink-to-fit loop would make the glyphs pulse at 99 <-> 100.
        var em = (float)Math.Round(sizePx * (text.Length >= 3 ? 0.56f : 0.86f), 1);

        var big = sizePx * TextScale;
        using var supersampled = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(supersampled))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(text, family, (int)FontStyle.Bold, em * TextScale, PointF.Empty, StringFormat.GenericTypographic);

            // Center the ink box (digits have no descenders, so vertical ink
            // centering equals cap-height centering; margins verified
            // symmetric with the icon-preview tool).
            var bounds = path.GetBounds();
            using var move = new Matrix();
            move.Translate((big - bounds.Width) / 2f - bounds.X, (big - bounds.Height) / 2f - bounds.Y);
            path.Transform(move);

            // Stroke first, fill on top: the pen straddles the glyph edge, so
            // the fill covers its inner half and OutlinePx remains outside.
            using var pen = new Pen(lightTheme ? Color.White : Color.Black, 2f * OutlinePx * TextScale)
            {
                LineJoin = LineJoin.Round,
            };
            graphics.DrawPath(pen, path);

            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
        }

        var bitmap = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // TileFlipXY keeps the sampler from blending transparent
            // out-of-bounds pixels into the border (visible edge ring).
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(supersampled, new Rectangle(0, 0, sizePx, sizePx), 0, 0, big, big, GraphicsUnit.Pixel, attrs);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap RenderGearIcon(int sizePx)
    {
        // Embedded resource only: single-file publish ships no loose Assets,
        // so a BaseDirectory file path would be Debug-only dead code.
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("PulsarBattery.Assets.icon.ico")
            ?? throw new InvalidOperationException("embedded icon.ico missing");

        int frame;
        try
        {
            frame = PickFrameSize(stream, sizePx);
        }
        catch
        {
            // Malformed ICONDIR: let GDI+ pick a frame instead of failing
            // every render while gear mode is active.
            frame = sizePx;
        }

        stream.Position = 0;
        using var frameIcon = new Icon(stream, frame, frame);
        using var frameBitmap = frameIcon.ToBitmap();

        // Same non-owning HICON pipeline as text mode: draw the frame onto our
        // own canvas (1:1 for exact frames; high-quality downscale otherwise).
        var bitmap = new Bitmap(sizePx, sizePx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(frameBitmap, new Rectangle(0, 0, sizePx, sizePx));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses the ICONDIR and returns the smallest embedded frame size that is
    /// at least <paramref name="sizePx"/> (falls back to the largest frame).
    /// The Icon(stream, size) constructor's closest-match can pick a smaller
    /// frame and force a shell upscale, so the choice is made explicitly.
    /// </summary>
    private static int PickFrameSize(Stream stream, int sizePx)
    {
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        if (header[0] != 0 || header[1] != 0 || header[2] != 1 || header[3] != 0)
        {
            throw new InvalidDataException("not an ICO stream");
        }

        var count = header[4] | (header[5] << 8);

        var best = int.MaxValue;
        var largest = 0;
        Span<byte> entry = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(entry);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            if (width != height)
            {
                continue; // non-square frames would render stretched
            }

            largest = Math.Max(largest, width);
            if (width >= sizePx && width < best)
            {
                best = width;
            }
        }

        if (largest == 0)
        {
            throw new InvalidDataException("no square frames");
        }

        return best == int.MaxValue ? largest : best;
    }

    private static int GetTrayIconSizePx()
    {
        uint dpi = 96;
        try
        {
            var monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0)
            {
                dpi = dpiX;
            }
        }
        catch
        {
            // shcore unavailable: keep 96.
        }

        var size = 0;
        try
        {
            size = GetSystemMetricsForDpi(SmCxSmIcon, dpi);
        }
        catch
        {
            // fall through to the computed fallback
        }

        if (size <= 0)
        {
            size = (int)Math.Round(16.0 * dpi / 96.0);
        }

        return size > 0 ? size : 16;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // The taskbar follows the *system* theme, not the app theme.
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pendingHicon != 0)
        {
            DestroyIcon(_pendingHicon);
            _pendingHicon = 0;
        }

        if (_currentHicon != 0)
        {
            DestroyIcon(_currentHicon);
            _currentHicon = 0;
        }

        if (_retiredHicon != 0)
        {
            DestroyIcon(_retiredHicon);
            _retiredHicon = 0;
        }
    }
}
