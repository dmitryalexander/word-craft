using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

// The Fluid and Particles views. Both run the shared solver and differ in settings and drawing.
sealed class GpuFlow : IDisposable
{
    const int FlowRows = 288;
    // Particle counts start low: about 33, 66, 131 and 262 thousand.
    static readonly int[] ParticleColumns = { 256, 256, 512, 512 }, ParticleRows = { 128, 256, 256, 512 };
    readonly GpuFluid fluid;
    readonly GpuPresenter presenter = new GpuPresenter();
    readonly int kind;
    readonly string prefix;
    readonly float[] settings = new float[19];
    Size lastPixels;
    int spawns;
    int width, height, flowWidth, flowHeight, iterations, particleColumns, particleRows;
    byte[] maskPixels, outlinePixels, letterPixels;
    Geometry word;
    Size wordPanel;
    bool hasWord, failed;
    // Fluid's dye and Particles' glow colors.
    readonly ColorScheme colors;
    // Particles' glow color for the current keystroke, in the shader's tuning buffer.
    const int GlowSlot = 76;

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int FlowCreate(int kind, int width, int height, int flowWidth, int flowHeight, int iterations,
        int particleWidth, int particleHeight, out GpuFluid result);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int FlowResize(GpuFluid fluid, int width, int height, int flowWidth, int flowHeight, int iterations,
        int particleWidth, int particleHeight);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowSetWord(GpuFluid fluid, byte[] mask, byte[] outline);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowSplat(GpuFluid fluid, float x, float y, float pushX, float pushY, float radius,
        float red, float green, float blue, float amount);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowBurst(GpuFluid fluid, float force, float lift, float red, float green, float blue,
        float amount);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int FlowFrame(GpuFluid fluid, float[] settings, int fueled);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowSpawn(GpuFluid fluid, float thickness, float share, float left, float top, float right,
        float bottom, float seed);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowEject(GpuFluid fluid, float particleStrength, float fluidStrength, float limit);
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void FlowPushShape(GpuFluid fluid, byte[] shape, byte[] word, float strength);

    GpuFlow(GpuFluid fluid, WordViewMode mode)
    {
        this.fluid = fluid;
        kind = Kind(mode);
        prefix = mode.ToString().ToLowerInvariant() + ".";
        if (kind == 0)
            colors = new ColorScheme("fluid", new[] { "fluid.color", "fluid.c2", "fluid.c3", "fluid.c4", "fluid.c5" },
                new[] { "fluid.d1", "fluid.d2", "fluid.d3", "fluid.d4", "fluid.d5" });
        else
            colors = new ColorScheme("particles", new[] { "particles.glowcolor", "particles.c2", "particles.c3",
                "particles.c4", "particles.c5" });
    }

    // These views draw at two thirds of the tier's rows. WPF scales the frame to the window.
    static void Measure(Size pixels, QualityTier tier, out int width, out int height, out int flowWidth)
    {
        double aspect = Math.Max(0.5, Math.Min(4, pixels.Width / Math.Max(1, pixels.Height)));
        height = (int)Math.Max(180, Math.Min(GpuQuality.Rows(tier) * 2 / 3, pixels.Height));
        width = Math.Max(16, (int)Math.Round(height * aspect));
        flowWidth = Math.Max(16, (int)Math.Round(FlowRows * aspect));
    }

    void Adopt(int newWidth, int newHeight, int newFlowWidth, QualityTier tier)
    {
        width = newWidth;
        height = newHeight;
        flowWidth = newFlowWidth;
        flowHeight = FlowRows;
        iterations = GpuQuality.Iterations(tier);
        Swarm(tier, out particleColumns, out particleRows);
        maskPixels = new byte[width * height * 4];
        outlinePixels = new byte[flowWidth * flowHeight * 4];
        letterPixels = new byte[flowWidth * flowHeight * 4];
    }

    // The particle grid: the panel's Particle count when set, otherwise the quality tier's.
    static void Swarm(QualityTier tier, out int columns, out int rows)
    {
        double thousands = ViewTuning.Get("particles.count");
        if (thousands > 0)
        {
            columns = 512;
            rows = Math.Max(1, (int)Math.Ceiling(thousands * 1000 / columns));
        }
        else
        {
            columns = ParticleColumns[(int)tier];
            rows = ParticleRows[(int)tier];
        }
    }

    // The native module's kinds: 0 is Fluid and 2 is Particles. 1 was Fire, removed to be rebuilt later.
    static int Kind(WordViewMode mode) { return mode == WordViewMode.Particles ? 2 : 0; }

    public static GpuFlow TryCreate(WordViewMode mode, Size pixels, QualityTier tier)
    {
        int swarmColumns, swarmRows;
        Swarm(tier, out swarmColumns, out swarmRows);
        int width, height, flowWidth;
        Measure(pixels, tier, out width, out height, out flowWidth);
        try
        {
            GpuFluid handle;
            int status = FlowCreate(Kind(mode), width, height, flowWidth, FlowRows,
                GpuQuality.Iterations(tier), swarmColumns, swarmRows, out handle);
            if (status >= 0)
            {
                var created = new GpuFlow(handle, mode);
                created.lastPixels = pixels;
                created.Adopt(width, height, flowWidth, tier);
                if (created.presenter.Attach(handle))
                {
                    // Fill the new surface before WPF shows it.
                    created.Advance();
                    if (!created.failed)
                        return created;
                }
                created.Dispose();
                return null;
            }
            handle.Dispose();
            Trace.WriteLine("WordCraft " + mode + " Direct3D initialization: " + status.ToString("X8"));
        }
        catch (DllNotFoundException error) { Trace.WriteLine(error.Message); }
        catch (BadImageFormatException error) { Trace.WriteLine(error.Message); }
        catch (EntryPointNotFoundException error) { Trace.WriteLine(error.Message); }
        return null;
    }

    public ImageSource Image { get { return presenter.Image; } }
    public bool Moving { get { return !failed; } }
    public bool Failed { get { return failed; } }
    public float Milliseconds { get { return failed ? 0 : GpuPresenter.Milliseconds(fluid); } }
    public string Adapter { get { return GpuPresenter.Adapter(fluid); } }
    public string Grid
    {
        get
        {
            string grid = width + " × " + height + " dye, " + flowWidth + " × " + flowHeight + " flow";
            return kind == 2 ? grid + ", " + particleColumns * particleRows + " particles" : grid;
        }
    }

    public void Resize(Size pixels, QualityTier tier)
    {
        if (failed)
            return;
        lastPixels = pixels;
        int newWidth, newHeight, newFlowWidth, swarmColumns, swarmRows;
        Measure(pixels, tier, out newWidth, out newHeight, out newFlowWidth);
        Swarm(tier, out swarmColumns, out swarmRows);
        bool sameSwarm = kind != 2 || (swarmColumns == particleColumns && swarmRows == particleRows);
        if (newWidth == width && newHeight == height && newFlowWidth == flowWidth &&
            GpuQuality.Iterations(tier) == iterations && sameSwarm)
            return;
        presenter.Detach();
        int status = FlowResize(fluid, newWidth, newHeight, newFlowWidth, FlowRows, GpuQuality.Iterations(tier),
            swarmColumns, swarmRows);
        if (status < 0)
        {
            Trace.WriteLine("WordCraft flow view Direct3D resize: " + status.ToString("X8"));
            Fail();
            return;
        }
        Adopt(newWidth, newHeight, newFlowWidth, tier);
        if (!presenter.Attach(fluid))
        {
            Fail();
            return;
        }
        // The word masks were rebuilt empty at the new size.
        SetWord(word, wordPanel);
        Advance();
    }

    void Fail()
    {
        failed = true;
        presenter.Detach();
    }

    // The word on screen, or null for a blank step. Space releases it.
    public void SetWord(Geometry glyphs, Size panel)
    {
        if (failed)
            return;
        word = glyphs;
        wordPanel = panel;
        hasWord = glyphs != null && !glyphs.Bounds.IsEmpty && panel.Width > 0 && panel.Height > 0;
        if (hasWord)
        {
            GpuSumiInk.Rasterize(glyphs, panel, width, height, maskPixels);
            GpuSumiInk.Rasterize(glyphs, panel, flowWidth, flowHeight, outlinePixels);
        }
        else
        {
            Array.Clear(maskPixels, 0, maskPixels.Length);
            Array.Clear(outlinePixels, 0, outlinePixels.Length);
        }
        FlowSetWord(fluid, maskPixels, outlinePixels);
    }

    // Every typed version of the word disturbs the fluid in its own shape, as in Smoke.
    // The fluid is pushed outward from the edges of the word fragment, and harder from the letter just typed.
    // Fluid also lays the fragment in as dye. Particles light new particles along the letters' outlines.
    public void Deposit(Geometry glyphs, Size panel, Geometry letter)
    {
        SetWord(glyphs, panel);
        if (failed || !hasWord)
            return;
        float push = ViewTuning.Get(prefix + "push");
        float red = 0, green = 0, blue = 0, ink = 0;
        // Lay mode: 0 adds, 2 replaces. With Alternate keystrokes on, every other keystroke lays its color over
        // what is there, so each new letter shows against built-up light.
        float lay = 0;
        if (kind == 0)
        {
            bool dark;
            Color color = colors.Keystroke(out dark);
            red = color.R / 255f;
            green = color.G / 255f;
            blue = color.B / 255f;
            ink = dark ? 1 : ViewTuning.Get("fluid.ink");
            lay = dark ? 2 : 0;
        }
        // Particles: a random share of the whole swarm moves onto the letters' outlines and glows in this
        // keystroke's color before the word pushes.
        if (kind == 2)
        {
            // The whole word pops in again: a fresh share of the swarm fills every letter, then everything inside
            // the letters is thrown out at once, then new glowing particles land on the outlines.
            ViewTuning.SetCurrent(GlowSlot, colors.Next());
            GpuPresenter.Tune(fluid);
            Rect bounds = glyphs.Bounds;
            float left = (float)(bounds.Left / panel.Width), top = (float)(bounds.Top / panel.Height);
            float right = (float)(bounds.Right / panel.Width), bottom = (float)(bounds.Bottom / panel.Height);
            // A thickness of 0 fills the letters instead of lining them.
            FlowSpawn(fluid, 0, ViewTuning.Get("particles.burst"), left, top, right, bottom, ++spawns);
            float burst = ViewTuning.Get("particles.void");
            FlowEject(fluid, 0.004f * burst, 1.5f * burst, ViewTuning.Get("particles.limit"));
            float thickness = ViewTuning.Get("particles.outline") * width / Math.Max(1.0f, (float)panel.Width);
            FlowSpawn(fluid, thickness, ViewTuning.Get("particles.spawn"), left, top, right, bottom, ++spawns);
        }
        // Every letter of the fragment pushes equally.
        FlowBurst(fluid, 2.2f * push, lay, red, green, blue, ink);
        // Particles push the whole word only. The newest letter's extra push is Fluid's.
        float newest = kind == 0 ? ViewTuning.Get("fluid.newest") : 0;
        if (letter == null || letter.Bounds.IsEmpty || newest <= 0)
            return;
        GpuSumiInk.Rasterize(letter, panel, flowWidth, flowHeight, letterPixels);
        FlowPushShape(fluid, letterPixels, outlinePixels, 2.2f * newest);
    }

    // Space: the finished word bursts outward. Fluid's burst carries the word's color.
    public void Disturb(bool commit, double letterX)
    {
        // Letters reach the fluid through Deposit, with their impact.
        if (failed || !commit || !hasWord)
            return;
        float push = ViewTuning.Get(prefix + "push");
        if (kind == 0)
        {
            Color color = colors.Current;
            FlowBurst(fluid, 2.2f * push, 0, color.R / 255f, color.G / 255f, color.B / 255f, 0.9f);
        }
        else
            FlowBurst(fluid, 3 * push, 0, 0, 0, 0, 0);
        if (colors != null)
            colors.EndWord();
    }

    public void Advance()
    {
        if (failed)
            return;
        // Order matches the native FlowFrame settings.
        if (kind == 2)
        {
            // A new Particle count rebuilds the swarm.
            int swarmColumns, swarmRows;
            Swarm(GpuQuality.Tier, out swarmColumns, out swarmRows);
            if (swarmColumns != particleColumns || swarmRows != particleRows)
            {
                Resize(lastPixels, GpuQuality.Tier);
                return;
            }
        }
        settings[0] = ViewTuning.Get(prefix + "curl");
        settings[1] = ViewTuning.Get(prefix + "keep");
        settings[7] = ViewTuning.Get(prefix + "exposure");
        settings[16] = ViewTuning.Get(prefix + "flowspeed");
        settings[17] = ViewTuning.Get(prefix + "limit");
        settings[18] = kind == 2 && ViewTuning.Choice("particles.grid") == 1 ? ViewTuning.Get("particles.gridpull") : 0;
        GpuPresenter.Motion(fluid, prefix, ViewTuning.Get(prefix + "reach"));
        if (kind == 0)
        {
            settings[2] = ViewTuning.Get("fluid.fade");
            settings[8] = ViewTuning.Get("fluid.glow");
        }
        else
        {
            settings[2] = 0.99f;
            settings[9] = ViewTuning.Get("particles.follow");
            settings[10] = ViewTuning.Get("particles.size");
            settings[11] = ViewTuning.Get("particles.light");
            settings[12] = ViewTuning.Get("particles.speed");
            settings[13] = 0;
            settings[14] = ViewTuning.Get("particles.glowkeep");
            settings[15] = ViewTuning.Get("particles.glow");
        }
        GpuPresenter.Tune(fluid);
        int status = FlowFrame(fluid, settings, 0);
        if (status < 0)
        {
            Trace.WriteLine("WordCraft flow view Direct3D frame: " + status.ToString("X8"));
            Fail();
            return;
        }
        presenter.Invalidate();
    }

    public void Dispose()
    {
        presenter.Detach();
        fluid.Dispose();
    }
}
