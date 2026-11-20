using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;

enum QualityTier { Low, Medium, High, Ultra }

// One tier applies to every view. It starts from measured GPU time and is remembered between launches.
static class GpuQuality
{
    static readonly int[] RowLimit = { 540, 720, 1080, 2160 };
    static readonly int[] Effort = { 12, 16, 22, 32 };
    const int Samples = 90;
    const double SlowFrame = 11, FastFrame = 3.5;
    static readonly string path = Path.Combine(
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")), "View-Quality.txt");
    static QualityTier tier = QualityTier.High;
    static bool chosen, lowered, raised;
    static double total;
    static int count;

    public static bool Remember = true;
    public static QualityTier Tier { get { return tier; } }
    public static bool Measuring { get { return !chosen; } }

    static GpuQuality()
    {
        try
        {
            QualityTier saved;
            if (File.Exists(path) && Enum.TryParse(File.ReadAllText(path).Trim(), true, out saved) &&
                Enum.IsDefined(typeof(QualityTier), saved))
            {
                tier = saved;
                chosen = true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static int Rows(QualityTier value) { return RowLimit[(int)value]; }
    public static int Iterations(QualityTier value) { return Effort[(int)value]; }

    public static void Choose(QualityTier value)
    {
        tier = value;
        chosen = true;
        total = 0;
        count = 0;
        Save();
    }

    // Returns true when the measured frame time moved the tier.
    public static bool Observe(float milliseconds)
    {
        if (chosen || milliseconds <= 0)
            return false;
        total += milliseconds;
        if (++count < Samples)
            return false;
        double mean = total / count;
        total = 0;
        count = 0;
        if (mean > SlowFrame && tier != QualityTier.Low)
        {
            --tier;
            lowered = true;
            if (raised)
            {
                chosen = true;
                Save();
            }
            return true;
        }
        if (mean < FastFrame && tier == QualityTier.High && !lowered && !raised)
        {
            tier = QualityTier.Ultra;
            raised = true;
            return true;
        }
        chosen = true;
        Save();
        return false;
    }

    static void Save()
    {
        if (!Remember)
            return;
        try { File.WriteAllText(path, tier.ToString()); }
        catch (IOException error) { Trace.WriteLine(error.Message); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error.Message); }
    }
}

sealed class GpuFluid : SafeHandleZeroOrMinusOneIsInvalid
{
    public GpuFluid() : base(true) { }

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiDestroy(IntPtr fluid);

    protected override bool ReleaseHandle()
    {
        SumiDestroy(handle);
        return true;
    }
}

// Shows a view's Direct3D surface in WPF. The frame stays on the GPU.
sealed class GpuPresenter
{
    readonly D3DImage image = new D3DImage();
    IntPtr surface;

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr GpuSurface(GpuFluid fluid);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern float GpuMilliseconds(GpuFluid fluid);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void GpuTune(GpuFluid fluid, float[] values);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int GpuAdapter(GpuFluid fluid, StringBuilder name, int capacity);

    public GpuPresenter()
    {
        image.IsFrontBufferAvailableChanged += (sender, args) =>
        {
            if (image.IsFrontBufferAvailable)
                Bind();
        };
    }

    public ImageSource Image { get { return surface == IntPtr.Zero ? null : image; } }

    public bool Attach(GpuFluid fluid)
    {
        surface = GpuSurface(fluid);
        return Bind();
    }

    bool Bind()
    {
        if (surface == IntPtr.Zero)
            return false;
        image.Lock();
        try
        {
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface, true);
            return true;
        }
        catch (ArgumentException error) { Trace.WriteLine("WordCraft surface: " + error.Message); }
        catch (InvalidOperationException error) { Trace.WriteLine("WordCraft surface: " + error.Message); }
        finally { image.Unlock(); }
        surface = IntPtr.Zero;
        return false;
    }

    // Release WPF's hold before the native surface is replaced.
    public void Detach()
    {
        if (surface == IntPtr.Zero)
            return;
        surface = IntPtr.Zero;
        image.Lock();
        try { image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero, true); }
        catch (InvalidOperationException error) { Trace.WriteLine("WordCraft surface: " + error.Message); }
        finally { image.Unlock(); }
    }

    public void Invalidate()
    {
        if (surface == IntPtr.Zero || image.PixelWidth == 0)
            return;
        image.Lock();
        try { image.AddDirtyRect(new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight)); }
        catch (InvalidOperationException error) { Trace.WriteLine("WordCraft surface: " + error.Message); }
        finally { image.Unlock(); }
    }

    public static float Milliseconds(GpuFluid fluid) { return GpuMilliseconds(fluid); }

    // Sends the tuning panel's current values to the view's shaders.
    public static void Tune(GpuFluid fluid) { GpuTune(fluid, ViewTuning.Shader()); }
    public static void Tune(GpuFluid fluid, float[] values) { GpuTune(fluid, values); }

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void GpuSetMotion(GpuFluid fluid, int iterations, float reach);

    // Pressure iterations from the panel, or the quality tier's count when the panel says 0.
    public static void Motion(GpuFluid fluid, string prefix, float reach)
    {
        int chosen = (int)Math.Round(ViewTuning.Get(prefix + "iterations"));
        GpuSetMotion(fluid, chosen > 0 ? chosen : GpuQuality.Iterations(GpuQuality.Tier), reach);
    }

    public static string Adapter(GpuFluid fluid)
    {
        var name = new StringBuilder(128);
        return GpuAdapter(fluid, name, name.Capacity) >= 0 ? name.ToString() : "unknown adapter";
    }
}
