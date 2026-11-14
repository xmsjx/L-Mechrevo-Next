using MechrevoLite.Hardware;
namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 单键 RGB 帧流的窗口侧门面：界面只按名字读写效果参数，具体的帧流对象留在服务层。
/// <para>成员名与帧流对象一致，便于界面按原来的写法调用。</para>
/// </summary>
public sealed class PerKeyRgbView
{
    private readonly KeyboardRgb _rgb;

    private PerKeyRgbView(KeyboardRgb rgb)
    {
        _rgb = rgb;
    }

    /// <summary>取当前帧流对象（没有就按配置新建并连接），失败返回 null。</summary>
    public static PerKeyRgbView? Attach()
    {
        try
        {
            KeyboardRgb? rgb = WpfBackend.EnsurePerKeyRgb();
            return rgb == null ? null : new PerKeyRgbView(rgb);
        }
        catch (Exception ex)
        {
            Logger.WriteError("[hid-fx] 打开单键灯效窗口失败：" + ex.Message);
            return null;
        }
    }

    public bool IsConnected => _rgb.IsConnected;

    public string LastError => _rgb.LastError;

    public bool KbPowerOn
    {
        get => _rgb.KbPowerOn;
        set => _rgb.KbPowerOn = value;
    }

    public bool UseGlobalInput
    {
        get => _rgb.UseGlobalInput;
        set => _rgb.UseGlobalInput = value;
    }

    public int Brightness
    {
        get => _rgb.Brightness;
        set => _rgb.Brightness = value;
    }

    public int KbHidMode
    {
        get => _rgb.KbHidMode;
        set => _rgb.KbHidMode = value;
    }

    public int TargetFps
    {
        get => _rgb.TargetFps;
        set => _rgb.TargetFps = value;
    }

    public int WaveAngle
    {
        get => _rgb.WaveAngle;
        set => _rgb.WaveAngle = value;
    }

    public int WaveSpeed
    {
        get => _rgb.WaveSpeed;
        set => _rgb.WaveSpeed = value;
    }

    public System.Drawing.Color BreathColor
    {
        get => _rgb.BreathColor;
        set => _rgb.BreathColor = value;
    }

    public int BreathSpeed
    {
        get => _rgb.BreathSpeed;
        set => _rgb.BreathSpeed = value;
    }

    public int BreathSmooth
    {
        get => _rgb.BreathSmooth;
        set => _rgb.BreathSmooth = value;
    }

    public int SparkleDensity
    {
        get => _rgb.SparkleDensity;
        set => _rgb.SparkleDensity = value;
    }

    public int SparkleSpeed
    {
        get => _rgb.SparkleSpeed;
        set => _rgb.SparkleSpeed = value;
    }

    public bool WheelReverse
    {
        get => _rgb.WheelReverse;
        set => _rgb.WheelReverse = value;
    }

    public int WheelSpeed
    {
        get => _rgb.WheelSpeed;
        set => _rgb.WheelSpeed = value;
    }

    public int LightningWidth
    {
        get => _rgb.LightningWidth;
        set => _rgb.LightningWidth = value;
    }

    public int LightningSpeed
    {
        get => _rgb.LightningSpeed;
        set => _rgb.LightningSpeed = value;
    }

    public int LightningSmooth
    {
        get => _rgb.LightningSmooth;
        set => _rgb.LightningSmooth = value;
    }

    public int LightningConcurrent
    {
        get => _rgb.LightningConcurrent;
        set => _rgb.LightningConcurrent = value;
    }

    public int FlameSpeed
    {
        get => _rgb.FlameSpeed;
        set => _rgb.FlameSpeed = value;
    }

    public int FlameSmooth
    {
        get => _rgb.FlameSmooth;
        set => _rgb.FlameSmooth = value;
    }

    public int RainDensity
    {
        get => _rgb.RainDensity;
        set => _rgb.RainDensity = value;
    }

    public int RainSpeed
    {
        get => _rgb.RainSpeed;
        set => _rgb.RainSpeed = value;
    }

    public int RainColorCount
    {
        get => _rgb.RainColorCount;
        set => _rgb.RainColorCount = value;
    }

    public System.Drawing.Color[] RainColors => _rgb.RainColors;

    public int MatrixDensity
    {
        get => _rgb.MatrixDensity;
        set => _rgb.MatrixDensity = value;
    }

    public int MatrixSpeed
    {
        get => _rgb.MatrixSpeed;
        set => _rgb.MatrixSpeed = value;
    }

    public int MatrixStyle
    {
        get => _rgb.MatrixStyle;
        set => _rgb.MatrixStyle = value;
    }

    public int MatrixColorCount
    {
        get => _rgb.MatrixColorCount;
        set => _rgb.MatrixColorCount = value;
    }

    public System.Drawing.Color[] MatrixColors => _rgb.MatrixColors;

    public System.Drawing.Color ReactiveColor
    {
        get => _rgb.ReactiveColor;
        set => _rgb.ReactiveColor = value;
    }

    public int ReactiveDuration
    {
        get => _rgb.ReactiveDuration;
        set => _rgb.ReactiveDuration = value;
    }

    public bool ReactiveRandom
    {
        get => _rgb.ReactiveRandom;
        set => _rgb.ReactiveRandom = value;
    }

    public System.Drawing.Color StaticColor
    {
        get => _rgb.StaticColor;
        set => _rgb.StaticColor = value;
    }

    public System.Drawing.Color[] StaticColors => _rgb.StaticColors;

    public int StaticLayout
    {
        get => _rgb.StaticLayout;
        set => _rgb.StaticLayout = value;
    }

    public int StaticZones
    {
        get => _rgb.StaticZones;
        set => _rgb.StaticZones = value;
    }

    public void SaveConfig() => _rgb.SaveConfig();

    public void StartMode(int mode) => _rgb.StartMode(mode);

    public void StopCurrentEffect() => _rgb.StopCurrentEffect();

    public bool ApplyBrightnessToDevice() => _rgb.ApplyBrightnessToDevice();

    public bool Connect() => _rgb.Connect();

    public Task<bool> EnsureHidReadyAsync() => _rgb.EnsureHidReadyAsync();
}
