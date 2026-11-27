using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace L_Mechrevo.WpfP0;

/// <summary>风扇曲线画布：16 个温度点，拖动占空比。</summary>
public sealed class FanCurveCanvas : FrameworkElement
{
    internal const int PointCount = 16;
    private readonly int[] _duties = new int[PointCount];
    private byte[] _temps = new byte[PointCount];
    private int _dragIndex = -1;
    private bool _dragChanged;
    private int _valid = PointCount;
    private int _prefix;
    private byte[] _prefixTemps = Array.Empty<byte>();
    private int[] _prefixDuties = Array.Empty<int>();

    internal event Action? Changed;

    internal bool IsDragging => _dragIndex >= 0;

    private int _count = PointCount;

    internal int[] Duties
    {
        get
        {
            int[] all = RestoreDuties();
            if (_count >= all.Length)
            {
                return all;
            }
            int[] sliced = new int[_count];
            Array.Copy(all, sliced, _count);
            return sliced;
        }
    }

    internal byte[] Temperatures
    {
        get
        {
            byte[] all = RestoreTemperatures();
            if (_count >= all.Length)
            {
                return all;
            }
            byte[] sliced = new byte[_count];
            Array.Copy(all, sliced, _count);
            return sliced;
        }
    }

    /// <summary>只画 n 个点（温度轴由固件固定，不显示温度数字）。</summary>
    internal void LoadPoints(int count, int[] duties)
    {
        _prefix = 0;
        _prefixTemps = Array.Empty<byte>();
        _prefixDuties = Array.Empty<int>();
        _count = Math.Clamp(count, 1, PointCount);
        for (int i = 0; i < PointCount; i++)
        {
            _temps[i] = 0;
            _duties[i] = i < _count && i < duties.Length ? Math.Clamp(duties[i], 0, 100) : 0;
        }
        _valid = _count;
        InvalidateVisual();
    }

    public FanCurveCanvas()
    {
        Height = 190;
        MinHeight = 150;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        Focusable = true;
    }

    internal void Load(byte[] temps, byte[] duties)
    {
        _count = PointCount;
        // 跳过开头的空温度点（0/255），避免界面出现一个没有温度标签的点。
        int start = 0;
        while (start < temps.Length && (temps[start] == 0 || temps[start] == byte.MaxValue))
        {
            start++;
        }
        if (start >= temps.Length)
        {
            start = 0;
        }
        _prefix = start;
        _prefixTemps = new byte[start];
        _prefixDuties = new int[start];
        Array.Copy(temps, 0, _prefixTemps, 0, start);
        Array.Copy(duties, 0, _prefixDuties, 0, start);
        int valid = PointCount;
        for (int i = 0; i < PointCount; i++)
        {
            int source = i + start;
            _temps[i] = source < temps.Length ? temps[source] : (byte)0;
            int duty = source < duties.Length ? duties[source] : 0;
            _duties[i] = duty == 255 ? 0 : Math.Clamp(duty, 0, 100);
            if (i > 0 && valid == PointCount && (_temps[i] == byte.MaxValue || _temps[i] == 0))
            {
                valid = i;
            }
        }
        _valid = valid == PointCount ? PointCount : Math.Max(valid, 1);
        InvalidateVisual();
    }

    // 保存/应用时把显示时跳过的空头点补回，保持其它后端的 16 点表布局不变。
    private byte[] RestoreTemperatures()
    {
        if (_prefix <= 0)
        {
            return _temps;
        }
        byte[] result = new byte[PointCount];
        Array.Copy(_prefixTemps, 0, result, 0, _prefix);
        Array.Copy(_temps, 0, result, _prefix, PointCount - _prefix);
        return result;
    }

    private int[] RestoreDuties()
    {
        if (_prefix <= 0)
        {
            return _duties;
        }
        int[] result = new int[PointCount];
        Array.Copy(_prefixDuties, 0, result, 0, _prefix);
        Array.Copy(_duties, 0, result, _prefix, PointCount - _prefix);
        return result;
    }

    private (double left, double top, double width, double height) Plot()
    {
        return (44, 16, Math.Max(10, ActualWidth - 60), Math.Max(10, ActualHeight - 48));
    }

    private Point PointAt(int index, (double left, double top, double width, double height) plot)
    {
        double x = plot.left + (_valid <= 1 ? 0 : plot.width * index / (_valid - 1.0));
        double y = plot.top + plot.height * (100 - _duties[index]) / 100.0;
        return new Point(x, y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var plot = Plot();
        bool dark = AppTheme.IsDark;
        Color backColor = dark ? Color.FromArgb(0x88, 0x16, 0x1B, 0x23) : Color.FromArgb(0xF2, 0xF5, 0xF7, 0xFA);
        Color gridColor = dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);
        Color borderColor = dark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x44, 0x00, 0x00, 0x00);
        Color labelColor = dark ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xAA, 0x1B, 0x20, 0x29);
        Color pointFill = dark ? Color.FromArgb(0xCC, 0x16, 0x1B, 0x23) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        dc.DrawRectangle(new SolidColorBrush(backColor), null, new Rect(0, 0, ActualWidth, ActualHeight));
        Pen grid = new(new SolidColorBrush(gridColor), 1);
        Pen border = new(new SolidColorBrush(borderColor), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.top + plot.height * i / 4.0;
            dc.DrawLine(grid, new Point(plot.left, y), new Point(plot.left + plot.width, y));
            FormattedText text = new(
                (100 - i * 25).ToString() + "%",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                new SolidColorBrush(labelColor),
                1.0);
            dc.DrawText(text, new Point(4, y - 7));
        }
        dc.DrawRectangle(null, border, new Rect(plot.left, plot.top, plot.width, plot.height));

        StreamGeometry geo = new();
        using (StreamGeometryContext ctx = geo.Open())
        {
            for (int i = 0; i < _valid; i++)
            {
                Point p = PointAt(i, plot);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 2), geo);

        Brush pointBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        for (int i = 0; i < _valid; i++)
        {
            Point p = PointAt(i, plot);
            dc.DrawEllipse(new SolidColorBrush(pointFill), new Pen(pointBrush, 2), p, 5, 5);
            if (_temps[i] is > 0 and < byte.MaxValue)
            {
                FormattedText temp = new(
                    _temps[i].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    9,
                    new SolidColorBrush(labelColor),
                    1.0);
                dc.DrawText(temp, new Point(p.X - temp.Width / 2, plot.top + plot.height + 8));
            }
        }
        if (_dragIndex >= 0)
        {
            Point p = PointAt(_dragIndex, plot);
            FormattedText duty = new(
                _duties[_dragIndex] + "%",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                12,
                new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1B, 0x20, 0x29)),
                1.0);
            dc.DrawText(duty, new Point(p.X - duty.Width / 2, p.Y - 26));
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        var plot = Plot();
        Point pos = e.GetPosition(this);
        int best = -1;
        double bestDistance = 18 * 18;
        for (int i = 0; i < _valid; i++)
        {
            Point p = PointAt(i, plot);
            double dx = p.X - pos.X;
            double dy = p.Y - pos.Y;
            double d = dx * dx + dy * dy;
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        if (best >= 0)
        {
            _dragIndex = best;
            _dragChanged = false;
            Focus();
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var plot = Plot();
        double y = e.GetPosition(this).Y;
        int duty = (int)Math.Round((plot.top + plot.height - y) * 100.0 / plot.height);
        duty = Math.Clamp(duty, 0, 100);
        if (duty == _duties[_dragIndex])
        {
            e.Handled = true;
            return;
        }
        _duties[_dragIndex] = duty;
        _dragChanged = true;
        // 与旧曲线一致：后段不允许低于前段。
        for (int i = _dragIndex + 1; i < _valid; i++)
        {
            if (_duties[i] < _duties[i - 1])
            {
                _duties[i] = _duties[i - 1];
            }
        }
        for (int i = _dragIndex - 1; i >= 0; i--)
        {
            if (_duties[i] > _duties[i + 1])
            {
                _duties[i] = _duties[i + 1];
            }
        }
        e.Handled = true;
        Changed?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            e.Handled = true;
            ReleaseMouseCapture();
            if (_dragChanged)
            {
                _dragChanged = false;
                Changed?.Invoke();
            }
            InvalidateVisual();
        }
    }
}
