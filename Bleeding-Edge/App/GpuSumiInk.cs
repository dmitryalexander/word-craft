using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

sealed class GpuSumiInk : IDisposable
{
    // The solver keeps 0.6.27's 288 velocity rows so letter wakes move the dye as before.
    const int FlowRows = 288;
    readonly int simulationRows;
    readonly GpuFluid fluid;
    readonly GpuPresenter presenter = new GpuPresenter();
    readonly Stopwatch entranceClock = new Stopwatch();
    readonly Random shades = new Random();
    int width, height, flowWidth, flowHeight, iterations;
    byte[] depositPixels, outlinePixels;
    Func<Point> readScale;
    Point previousScale;
    int nextStroke, frames, ink = 14;
    bool failed;
    internal bool HoldDye { get; set; }
    internal bool Coasting { get; set; }
    internal bool TypingCoast { get; set; }
    internal bool ReadingFlow { get; set; }
    float readingMotion, billowDetail, coastMotion;
    bool flowDynamics;
    internal bool Turbulent { get; set; }
    float cloudLighting;

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int SumiCreate(int width, int height, int flowWidth, int flowHeight, int iterations,
        out GpuFluid result);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int SumiResize(GpuFluid fluid, int width, int height, int flowWidth, int flowHeight,
        int iterations);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiDisturb(GpuFluid fluid, byte[] mask, float x, float y,
        float radius, float phase, float strength, int letter, int ink);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int SumiFrame(GpuFluid fluid);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiSetOutline(GpuFluid fluid, byte[] mask);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiSetInk(GpuFluid fluid, float ink);

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiMoveOutline(GpuFluid fluid, float oldX, float oldY, float newX, float newY);

    // The Ink view runs this same system with ink that absorbs light on paper and fades back to white.
    readonly bool paper;
    int words, letters;
    readonly Random inkPicker = new Random();
    // Smoke's ink color for each letter or word. Ink chooses among its five inks instead.
    readonly ColorScheme colors = new ColorScheme("smoke", new[] { "smoke.ink", "smoke.c2", "smoke.c3", "smoke.c4", "smoke.c5" },
        new[] { "smoke.d1", "smoke.d2", "smoke.d3", "smoke.d4", "smoke.d5" });
    const int SmokeColorSlot = 80;

    GpuSumiInk(GpuFluid fluid, bool paper, int simulationRows)
    {
        this.fluid = fluid;
        this.paper = paper;
        this.simulationRows = simulationRows;
        if (paper)
        {
            ink = 100;
            SumiSetInk(fluid, ink);
        }
    }

    static void Measure(Size pixels, QualityTier tier, out int width, out int height,
        out int flowWidth, out int flowHeight, int simulationRows)
    {
        double aspect = Math.Max(0.5, Math.Min(4, pixels.Width / Math.Max(1, pixels.Height)));
        height = (int)Math.Max(180, Math.Min(GpuQuality.Rows(tier), pixels.Height));
        width = Math.Max(16, (int)Math.Round(height * aspect));
        flowHeight = simulationRows;
        flowWidth = Math.Max(16, (int)Math.Round(simulationRows * aspect));
    }

    void Adopt(int newWidth, int newHeight, int newFlowWidth, int newFlowHeight, int newIterations)
    {
        width = newWidth;
        height = newHeight;
        flowWidth = newFlowWidth;
        flowHeight = newFlowHeight;
        iterations = newIterations;
        depositPixels = new byte[width * height * 4];
        outlinePixels = new byte[flowWidth * flowHeight * 4];
    }

    public static GpuSumiInk TryCreate(Size pixels, QualityTier tier, bool paper = false, int simulationRows = FlowRows)
    {
        int width, height, flowWidth, flowHeight;
        Measure(pixels, tier, out width, out height, out flowWidth, out flowHeight, simulationRows);
        try
        {
            GpuFluid handle;
            int status = SumiCreate(width, height, flowWidth, flowHeight, GpuQuality.Iterations(tier), out handle);
            if (status >= 0)
            {
                var created = new GpuSumiInk(handle, paper, simulationRows);
                created.Adopt(width, height, flowWidth, flowHeight, GpuQuality.Iterations(tier));
                if (created.presenter.Attach(handle))
                {
                    // Fill the new surface before WPF shows it.
                    created.frames = 1;
                    created.Advance();
                    if (!created.failed)
                        return created;
                }
                created.Dispose();
                return null;
            }
            handle.Dispose();
            Trace.WriteLine("WordCraft Smoke Direct3D initialization: " + status.ToString("X8"));
        }
        catch (DllNotFoundException error) { Trace.WriteLine(error.Message); }
        catch (BadImageFormatException error) { Trace.WriteLine(error.Message); }
        catch (EntryPointNotFoundException error) { Trace.WriteLine(error.Message); }
        return null;
    }

    public ImageSource Image { get { return presenter.Image; } }
    // Ink keeps running so it can fade back to paper.
    public bool Moving { get { return !failed && (paper || TypingCoast || Coasting || ReadingFlow || readingMotion > 0 || frames > 0); } }
    public bool Failed { get { return failed; } }
    public bool EntranceMoving { get { return readScale != null; } }
    public float Milliseconds { get { return failed ? 0 : GpuPresenter.Milliseconds(fluid); } }
    public string Adapter { get { return GpuPresenter.Adapter(fluid); } }
    public string Grid { get { return width + " × " + height + " dye, " + flowWidth + " × " + flowHeight + " flow"; } }

    // Follows the window and tier. Dye and motion carry over on the GPU.
    public void Resize(Size pixels, QualityTier tier)
    {
        if (failed)
            return;
        int newWidth, newHeight, newFlowWidth, newFlowHeight;
        Measure(pixels, tier, out newWidth, out newHeight, out newFlowWidth, out newFlowHeight, simulationRows);
        int newIterations = GpuQuality.Iterations(tier);
        if (newWidth == width && newHeight == height && newFlowWidth == flowWidth && newIterations == iterations)
            return;
        presenter.Detach();
        readScale = null;
        int status = SumiResize(fluid, newWidth, newHeight, newFlowWidth, newFlowHeight, newIterations);
        if (status < 0)
        {
            Trace.WriteLine("WordCraft Smoke Direct3D resize: " + status.ToString("X8"));
            Fail();
            return;
        }
        Adopt(newWidth, newHeight, newFlowWidth, newFlowHeight, newIterations);
        if (!presenter.Attach(fluid))
        {
            Fail();
            return;
        }
        // Draw the carried dye into the new surface before WPF shows it.
        frames = Math.Max(frames, 1);
        Advance();
    }

    // Keeps the dye moving so a tuning change can be seen.
    public void Wake()
    {
        if (!failed)
            frames = Math.Max(frames, 150);
    }

    internal void PushVortex(double centerX, double centerY, double aspect, float strength, double expansion = 0)
    {
        if (failed)
            return;
        double scale = Math.Min(1, aspect);
        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3;
            PushSmoke(centerX, centerY, aspect, angle, .145 * scale, .145f, angle + Math.PI / 2 + .36 - expansion, strength);
            if (i % 2 == 0)
                PushSmoke(centerX, centerY, aspect, angle + .36, .31 * scale, .14f, angle + 1.95 - expansion, strength * .24f);
        }
        frames = Math.Max(frames, 100);
    }

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiColorGradient(GpuFluid fluid, float[] values);

    static readonly Stopwatch gradientClock = Stopwatch.StartNew();
    internal static double GradientSeconds { get { return gradientClock.Elapsed.TotalSeconds; } }
    float[] gradientValues;
    double gradientAngle, gradientRotation;

    internal void SetColorGradient(Color start, Color end, double amount, double angle, double rotation = 0)
    {
        if(failed || paper)return;
        gradientAngle=angle;gradientRotation=rotation;
        gradientValues=new float[] {
            start.R/255f,start.G/255f,start.B/255f,0,
            end.R/255f,end.G/255f,end.B/255f,0,(float)amount,0,0,0 };
        UpdateColorGradient();
        Wake();
    }

    void UpdateColorGradient()
    {
        if(gradientValues==null)return;
        double radians=(gradientAngle+gradientRotation*GradientSeconds)*Math.PI/180;
        gradientValues[9]=(float)Math.Cos(radians);
        gradientValues[10]=(float)Math.Sin(radians);
        SumiColorGradient(fluid,gradientValues);
    }

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiReadingBlur(GpuFluid fluid,float[] values);
    internal void SetReadingBlur(double amount,double halfHeight)
    {
        if(failed || paper)return;
        SumiReadingBlur(fluid,new float[]{(float)amount,(float)halfHeight,.0075f,0});
        Wake();
    }

    bool ambientSeeded;
    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiSeedAmbient(GpuFluid fluid);

    internal void SeedAmbient()
    {
        if (failed || ambientSeeded || paper) return;
        ViewTuning.SetCurrent(SmokeColorSlot, ViewTuning.ColorOf("smoke.ink"));
        GpuPresenter.Tune(fluid, ViewTuning.Shader(false));
        SumiSetInk(fluid, 20);
        SumiSeedAmbient(fluid);
        ambientSeeded = true;
        frames = Math.Max(frames, 150);
    }

    internal void DriveWindTunnel(double aspect, float strength, bool emit)
    {
        if (failed)
            return;
        float orbit = (float)(.23 * Math.Min(1, aspect));
        float radius = flowHeight * .16f;
        // Equal opposing jets on opposite sides of the circle drive the same clockwise circulation.
        for (int jet = 0; jet < 2; jet++)
        {
            if (emit)
            {
                ViewTuning.SetCurrent(SmokeColorSlot, ViewTuning.ColorOf(jet == 0 ? "smoke.ink" : "smoke.d2"));
                GpuPresenter.Tune(fluid, ViewTuning.Shader(false));
            }
            float y = .5f + (jet == 0 ? -orbit : orbit);
            float direction = jet == 0 ? 0 : (float)Math.PI;
            SumiDisturb(fluid, null, .5f, y, radius, direction, strength, emit ? 1 : 0, 16);
        }
        frames = Math.Max(frames, 100);
    }

    [DllImport("SumiInkGpu.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void SumiStirLine(GpuFluid fluid, float x, float y, float radius, float angle, float strength);

    [DllImport("SumiInkGpu.dll",CallingConvention=CallingConvention.Cdecl)]
    static extern void SumiAbsorbFlow(GpuFluid fluid,byte[] mask,float x,float y,float velocity,float release,float push,float size,float gain);

    internal void AbsorbFlow(BitmapSource mask,double x,double y,double velocity,double release)
    {
        if(failed)return;
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
            drawing.DrawImage(mask,new Rect(0,0,width,height));
        var resized=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        resized.Render(visual);
        resized.CopyPixels(depositPixels,width*4,0);
        bool dark;
        flowDynamics=true;
        ViewTuning.SetCurrent(SmokeColorSlot,colors.Keystroke(out dark));
        GpuPresenter.Tune(fluid,ViewTuning.Shader(paper,HoldDye,Turbulent,cloudLighting,
            Coasting,readingMotion,billowDetail,coastMotion,flowDynamics&&HoldDye,TypingCoast));
        SumiAbsorbFlow(fluid,depositPixels,(float)x,(float)y,(float)velocity,(float)release,
            ViewTuning.Get("smoke.push"),ViewTuning.Get("smoke.size"),ViewTuning.Get("smoke.amount"));
        frames=Math.Max(frames,150);
    }

    internal void StirLine(float x, float y, float radius, float angle, float strength)
    {
        if (failed) return;
        SumiStirLine(fluid, x, y, flowHeight * radius, angle, strength);
        frames = Math.Max(frames, 100);
    }

    internal void ApplyWind(SmokeWind[] forces)
    {
        if (failed)
            return;
        foreach (SmokeWind force in forces)
            SumiDisturb(fluid, null, (float)force.X, (float)force.Y, (float)(flowHeight * force.Radius),
                (float)force.Direction, (float)force.Strength, 0, ink);
        frames = Math.Max(frames, 100);
    }

    internal void EmitCenterJets(double aspect, bool swapped)
    {
        if (failed)
            return;
        double scale = Math.Min(1, aspect);
        for (int jet = 0; jet < 2; jet++)
        {
            bool right = jet == 0;
            bool dark = right == swapped;
            ViewTuning.SetCurrent(SmokeColorSlot, ViewTuning.ColorOf(dark ? "smoke.d2" : "smoke.ink"));
            GpuPresenter.Tune(fluid, ViewTuning.Shader(false));
            float x = (float)(.5 + (right ? 1 : -1) * .035 * scale / aspect);
            SumiDisturb(fluid, null, x, .5f, flowHeight * .09f * (float)scale,
                right ? 0 : (float)Math.PI, 4.8f, 1, 16);
        }
        frames = Math.Max(frames, 100);
    }

    internal void SpreadSmoke(double x, double y, double aspect, double reach, float strength)
    {
        if (failed)
            return;
        double orbit = reach * Math.Min(1, aspect);
        for (int i = 0; i < 5; i++)
        {
            double angle = -.27 + i * Math.PI * 2 / 5;
            float wind = strength * (float)(.86 + .14 * Math.Cos(angle));
            PushSmoke(x, y, aspect, angle, orbit, .18f, angle + .24, wind);
        }
        frames = Math.Max(frames, 100);
    }

    void PushSmoke(double x, double y, double aspect, double angle, double orbit, float radius,
        double direction, float strength)
    {
        float px = (float)(x + Math.Cos(angle) * orbit / aspect);
        float py = (float)(y + Math.Sin(angle) * orbit);
        SumiDisturb(fluid, null, px, py, flowHeight * radius, (float)direction, strength, 0, ink);
    }

    internal void EmitMist(double x, double y, double direction, bool dark = false, float size = .19f)
    {
        if (failed)
            return;
        ViewTuning.SetCurrent(SmokeColorSlot, ViewTuning.ColorOf(dark ? "smoke.d2" : "smoke.ink"));
        GpuPresenter.Tune(fluid, ViewTuning.Shader(false));
        // Lay down a short, soft trail through the existing dye brush, then let the flow carry it.
        SumiDisturb(fluid, null, (float)x, (float)y, flowHeight * size, (float)direction, 2.7f, 1, 16);
        if (size < .15f)
        {
            double aspect = (double)flowWidth / flowHeight;
            for (int i = 0; i < 3; i++)
            {
                double angle = direction + i * Math.PI * 2 / 3;
                double turn = i % 2 == 0 ? 1 : -1;
                PushSmoke(x, y, aspect, angle, size * .32, size * .40f, angle + turn * Math.PI / 2, 3.1f);
            }
        }
        frames = Math.Max(frames, 100);
    }

    void Fail()
    {
        frames = 0;
        failed = true;
        readScale = null;
        presenter.Detach();
    }

    public void DepositWordVersion(Geometry glyph, Size panel)
    {
        if (failed)
            return;
        if (paper)
        {
            int order = ViewTuning.Choice("ink.colormode");
            if (order == 2)
                ink = 100 + ++letters % 5;
            else if (order == 3)
                ink = 100;
        }
        else
        {
            // The shader reads this deposit's color from the tuning buffer, so send it first.
            bool dark;
            ViewTuning.SetCurrent(SmokeColorSlot, colors.Keystroke(out dark));
            GpuPresenter.Tune(fluid, ViewTuning.Shader(false));
            int shade = shades.Next(24);
            // Give consecutive letters a visible separation in lightness. The second color of a pair is laid at
            // its strongest shade so it reads over the first.
            ink = dark ? 31 : (ink + 7 + shade) % 32;
        }
        Rasterize(glyph, panel, width, height, depositPixels);
        Rect bounds = glyph.Bounds;
        float x = (float)((bounds.Left + bounds.Width * 0.5) / panel.Width);
        float y = (float)((bounds.Top + bounds.Height * 0.5) / panel.Height);
        float phase = (float)((nextStroke++ * 2.399963) % (Math.PI * 2));
        SumiDisturb(fluid, depositPixels, x, y, 28 * ViewTuning.Get(paper ? "ink.size" : "smoke.size"), phase,
            5.4f * ViewTuning.Get(paper ? "ink.push" : "smoke.push"), 1, ink);
        frames = 235;
    }

    public void StartEntrance(Geometry glyphs, Size panel, Func<Point> scale)
    {
        if (failed || glyphs.Bounds.IsEmpty || panel.Width <= 0 || panel.Height <= 0)
            return;
        Rasterize(glyphs, panel, flowWidth, flowHeight, outlinePixels);
        SumiSetOutline(fluid, outlinePixels);
        previousScale = new Point(0.82, 0.62);
        readScale = scale;
        entranceClock.Restart();
        frames = 235;
    }

    internal static void Rasterize(Geometry glyphs, Size panel, int width, int height, byte[] output)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(width / panel.Width, height / panel.Height));
            drawing.DrawGeometry(Brushes.White, null, glyphs);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.CopyPixels(output, width * 4, 0);
    }

    public void Disturb(Geometry glyphs, Size panel, bool commit, double letterX)
    {
        if (failed || (commit && glyphs == null))
            return;
        if (commit)
            readScale = null;
        byte[] mask = null;
        Rect bounds = glyphs == null ? Rect.Empty : glyphs.Bounds;
        if (!bounds.IsEmpty && panel.Width > 0 && panel.Height > 0)
        {
            mask = depositPixels;
            Rasterize(glyphs, panel, width, height, mask);
        }
        if (commit && mask == null)
            return;

        float phase = (float)((nextStroke++ * 2.399963) % (Math.PI * 2));
        if (mask == null)
        {
            // Move the fluid around the new letter, alternating the direction of its wake.
            float x = (float)Math.Max(0.15, Math.Min(0.85, letterX));
            float y = 0.5f + 0.07f * (float)Math.Sin(phase);
            SumiDisturb(fluid, null, x, y, 34 * ViewTuning.Get(paper ? "ink.size" : "smoke.size"), phase,
                8.4f * ViewTuning.Get(paper ? "ink.push" : "smoke.push"), 1, ink);
        }
        else
        {
            float y = (float)((bounds.Top + bounds.Height * 0.5) / panel.Height);
            float across = ViewTuning.Get(paper ? "ink.strokes" : "smoke.strokes");
            int strokes = Math.Max(1, (int)Math.Ceiling(bounds.Width / panel.Width * across));
            for (int i = 0; i < strokes; ++i)
            {
                float x = (float)((bounds.Left + bounds.Width * ((i + 0.5) / strokes)) / panel.Width);
                float offsetY = i % 2 == 0 ? -0.09f : 0.08f;
                SumiDisturb(fluid, i == 0 ? mask : null, x, y + offsetY, 34 * ViewTuning.Get(paper ? "ink.size" : "smoke.size"),
                    phase + 1.8f * i, 9.5f * ViewTuning.Get(paper ? "ink.push" : "smoke.push"), 0, ink);
            }
        }
        frames = 235;
        if (!commit)
            return;
        if (!paper)
            colors.EndWord();
        else
        {
            // Ink picks the next word's ink after this word's Space burst.
            int order = ViewTuning.Choice("ink.colormode");
            if (order == 0)
                ink = 100 + ++words % 5;
            else if (order == 1)
                ink = 100 + inkPicker.Next(5);
            else if (order == 3)
                ink = 100;
        }
    }

    public void Advance()
    {
        if (!Moving)
            return;
        if (readScale != null)
        {
            Point scale = readScale();
            if (scale != previousScale)
                SumiMoveOutline(fluid, (float)previousScale.X, (float)previousScale.Y,
                    (float)scale.X, (float)scale.Y);
            previousScale = scale;
            if (entranceClock.ElapsedMilliseconds >= 165)
                readScale = null;
        }
        cloudLighting += ((Turbulent ? 1f : 0f) - cloudLighting) * .14f;
        if (!Turbulent && cloudLighting < .005f)
            cloudLighting = 0;
        readingMotion += ((ReadingFlow ? 1f : 0f) - readingMotion) * .18f;
        if (!ReadingFlow && readingMotion < .006f)
            readingMotion = 0;
        if(!HoldDye)flowDynamics=false;
        coastMotion += ((Coasting ? .86f : 0) - coastMotion) * .10f;
        if (!Coasting && coastMotion < .002f) coastMotion = 0;
        billowDetail += ((HoldDye ? 1f : 0f) - billowDetail) * .14f;
        if (!HoldDye && billowDetail < .005f) billowDetail = 0;
        GpuPresenter.Tune(fluid, ViewTuning.Shader(paper, HoldDye, Turbulent, cloudLighting, Coasting, readingMotion, billowDetail, coastMotion, flowDynamics&&HoldDye,TypingCoast));
        GpuPresenter.Motion(fluid, paper ? "ink." : "smoke.", 3);
        if(gradientRotation!=0 && gradientValues!=null && gradientValues[8]>0)UpdateColorGradient();
        int status = SumiFrame(fluid);
        if (status < 0)
        {
            Trace.WriteLine("WordCraft Smoke Direct3D frame: " + status.ToString("X8"));
            Fail();
            return;
        }
        presenter.Invalidate();
        if (frames > 0)
            --frames;
    }

    public void Dispose()
    {
        presenter.Detach();
        fluid.Dispose();
    }
}
