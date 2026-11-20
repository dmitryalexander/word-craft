using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

sealed class GpuWater : IDisposable
{
    const int ReleaseFrames = 180;
    readonly GpuFluid wave;
    readonly GpuPresenter presenter = new GpuPresenter();
    int width, height;
    readonly bool ferroliquid;
    readonly byte[] mask;
    readonly int maskWidth, maskHeight;
    readonly float[] pivots;
    readonly Random waterDrops = new Random();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    double lastFrame;
    float clock, nextDrop = 2;
    float pulse;
    int frames;
    bool hasFrame, failed;

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int WaterCreate(int width, int height, int ferroliquid, out GpuFluid wave);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int WaterResize(GpuFluid wave, int width, int height);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int WaterFrame(GpuFluid wave, float pulse, int held, int ferroliquid,
        float dt, float dropX, float dropY, float drop);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WaterSetOutline(GpuFluid wave, byte[] mask, int ferroliquid);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WaterSetPivots(GpuFluid wave, float[] pivots);

    GpuWater(GpuFluid wave, int width, int height, bool ferroliquid)
    {
        this.wave = wave;
        this.width = width;
        this.height = height;
        this.ferroliquid = ferroliquid;
        maskWidth = ferroliquid ? 768 : 384;
        maskHeight = ferroliquid ? 432 : 216;
        mask = new byte[maskWidth * maskHeight * 4];
        pivots = new float[maskWidth * 2];
        frames = ferroliquid ? 1 : 0;
    }

    // The surface keeps its 16:9 scene and covers the window, up to the tier's row limit.
    static void Measure(Size pixels, QualityTier tier, out int width, out int height)
    {
        double cover = Math.Max(pixels.Height, pixels.Width * 9 / 16);
        height = (int)Math.Max(180, Math.Min(GpuQuality.Rows(tier), Math.Ceiling(cover)));
        width = (int)Math.Round(height * 16.0 / 9);
    }

    public static GpuWater TryCreate(Size pixels, QualityTier tier, bool ferroliquid = false)
    {
        int width, height;
        Measure(pixels, tier, out width, out height);
        try
        {
            GpuFluid wave;
            if (WaterCreate(width, height, ferroliquid ? 1 : 0, out wave) >= 0)
            {
                var created = new GpuWater(wave, width, height, ferroliquid);
                if (created.presenter.Attach(wave))
                    return created;
                created.Dispose();
                return null;
            }
            wave.Dispose();
        }
        catch (DllNotFoundException) { }
        catch (BadImageFormatException) { }
        catch (EntryPointNotFoundException) { }
        return null;
    }

    public bool Moving { get { return !failed && (!ferroliquid || held || frames > 0); } }
    public bool Failed { get { return failed; } }
    bool held;
    public ImageSource Image { get { return (ferroliquid || Moving) && hasFrame ? presenter.Image : null; } }
    public float Milliseconds { get { return failed ? 0 : GpuPresenter.Milliseconds(wave); } }
    public string Adapter { get { return GpuPresenter.Adapter(wave); } }
    public string Grid { get { return width + " × " + height; } }
    // Only the shown frame follows the window. The wave grids stay as built until Water is rebuilt.
    public void Resize(Size pixels, QualityTier tier)
    {
        if (failed)
            return;
        int newWidth, newHeight;
        Measure(pixels, tier, out newWidth, out newHeight);
        if (newWidth == width && newHeight == height)
            return;
        presenter.Detach();
        if (WaterResize(wave, newWidth, newHeight) < 0 || !presenter.Attach(wave))
        {
            presenter.Detach();
            failed = true;
            frames = 0;
            return;
        }
        width = newWidth;
        height = newHeight;
        // Fill the new surface before WPF shows it.
        if (!held)
            frames = Math.Max(frames, 1);
        Advance();
    }

    public void ClearWord()
    {
        if (!held && (ferroliquid || pulse == 0))
            return;
        held = false;
        frames = ReleaseFrames;
        if (!ferroliquid)
        {
            Array.Clear(mask, 0, mask.Length);
            WaterSetOutline(wave, mask, 0);
        }
    }

    public void Disturb(Geometry glyphs, Size panel, bool commit, List<Point> centers, Point origin)
    {
        if (failed || glyphs == null || panel.Width <= 0 || panel.Height <= 0)
            return;
        if (!commit || ferroliquid)
        {
            GpuSumiInk.Rasterize(glyphs, panel, maskWidth, maskHeight, mask);
            WaterSetOutline(wave, mask, ferroliquid ? 1 : 0);
        }
        held = !commit;
        if (!commit)
        {
            centers.Sort((a, b) => a.X.CompareTo(b.X));
            int index = 0;
            for (int x = 0; x < maskWidth; ++x)
            {
                double position = origin.X + (x + 0.5) * panel.Width / maskWidth;
                while (index + 1 < centers.Count &&
                    2 * position > centers[index].X + centers[index + 1].X)
                    ++index;
                pivots[2 * x] = centers.Count == 0 ? 0.5f : (float)((centers[index].X - origin.X) / panel.Width);
                pivots[2 * x + 1] = centers.Count == 0 ? 0.5f : (float)((centers[index].Y - origin.Y) / panel.Height);
            }
            WaterSetPivots(wave, pivots);
        }
        if (commit)
            pulse = ferroliquid ? 0.027f : 0.086f * ViewTuning.Get("water.space");
        else if (ferroliquid)
            pulse = 0;
        if (commit)
            frames = ReleaseFrames;
    }

    public void Advance()
    {
        if (!Moving)
            return;
        double now = elapsed.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(1.0 / 30, Math.Max(1.0 / 120, now - lastFrame));
        lastFrame = now;
        clock += dt;
        float x = 0, y = 0, drop = 0;
        // Rain is drops per second. Each drop lands at a random place.
        float rain = ViewTuning.Get("water.rain");
        if (rain > 0)
            nextDrop = Math.Min(nextDrop, clock + 1.6f / rain);
        if (!ferroliquid && rain > 0 && clock > nextDrop)
        {
            nextDrop = clock + (0.4f + 1.2f * (float)waterDrops.NextDouble()) / rain;
            x = (float)waterDrops.NextDouble();
            y = (float)waterDrops.NextDouble();
            drop = 0.024f * ViewTuning.Get("water.drops");
        }
        GpuPresenter.Tune(wave);
        int status = WaterFrame(wave, pulse, held ? 1 : 0, ferroliquid ? 1 : 0, dt, x, y, drop);
        if (status < 0)
        {
            presenter.Detach();
            failed = true;
            frames = 0;
            return;
        }
        pulse = 0;
        presenter.Invalidate();
        hasFrame = true;
        if (!held && frames > 0)
            --frames;
    }

    public void Dispose()
    {
        presenter.Detach();
        wave.Dispose();
    }
}
