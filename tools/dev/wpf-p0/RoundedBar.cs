using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace L_Mechrevo.WpfP0;

internal sealed class RoundedBar : FrameworkElement
{
    private double _value;
    private double _maximum = 100;
    // 动画用：_display 是"正在画的比率"，向 _target 逼近（传感器每几秒才给一次新值，硬赋值看起来是一次跳变）。
    private double _display = double.NaN;
    private double _target;
    private DispatcherTimer? _animTimer;

    internal double Maximum
    {
        get => _maximum;
        set => _maximum = Math.Max(1, value);
    }

    /// <summary>是否为这条指示条启用平滑过渡。默认关闭，由需要的界面显式打开。</summary>
    internal bool Animate { get; set; }

    internal void SetValue(double value, double maximum)
    {
        _value = value;
        _maximum = Math.Max(1, maximum);
        double target = Math.Clamp(_value / _maximum, 0, 1);
        if (!Animate)
        {
            _display = target;
            _target = target;
            StopAnimation();
            InvalidateVisual();
            return;
        }
        _target = target;
        if (double.IsNaN(_display))
        {
            // 首次出现直接就位，避免从 0 扫一遍。
            _display = target;
            InvalidateVisual();
            return;
        }
        if (Math.Abs(_target - _display) < 0.004)
        {
            _display = _target;
            StopAnimation();
            InvalidateVisual();
            return;
        }
        EnsureAnimation();
        InvalidateVisual();
    }

    private void EnsureAnimation()
    {
        if (_animTimer != null)
        {
            if (!_animTimer.IsEnabled)
            {
                _animTimer.Start();
            }
            return;
        }
        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += (_, _) => StepAnimation();
        _animTimer.Start();
    }

    private void StopAnimation()
    {
        if (_animTimer is { IsEnabled: true })
        {
            _animTimer.Stop();
        }
    }

    private void StepAnimation()
    {
        double diff = _target - _display;
        if (Math.Abs(diff) < 0.004)
        {
            _display = _target;
            StopAnimation();
            InvalidateVisual();
            return;
        }
        // 指数逼近：每帧走剩余差的 25%，约 250ms 收敛，不会过冲。
        _display += diff * 0.25;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(1, ActualHeight);
        double radius = height / 2;
        Brush track = (Brush?)Application.Current.Resources["TrackBrush"] ?? Brushes.DimGray;
        Brush accent = (Brush?)Application.Current.Resources["AccentBrush"] ?? Brushes.DeepSkyBlue;
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), radius, radius);
        double ratio = double.IsNaN(_display) ? Math.Clamp(_value / _maximum, 0, 1) : Math.Clamp(_display, 0, 1);
        if (ratio > 0)
        {
            drawingContext.DrawRoundedRectangle(accent, null, new Rect(0, 0, width * ratio, height), radius, radius);
        }
    }
}
