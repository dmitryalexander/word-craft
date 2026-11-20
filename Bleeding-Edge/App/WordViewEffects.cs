using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Tab order. Smoke is the startup view.
enum WordViewMode { SumiInk, Water, Fluid, Particles, Ink }

struct SmokeWind
{
    internal double X, Y, Radius, Direction, Strength;

    internal SmokeWind(double x, double y, double radius, double direction, double strength)
    {
        X = x; Y = y; Radius = radius; Direction = direction; Strength = strength;
    }
}

sealed class WordViewEffects
{
    readonly Action redraw;
    readonly int smokeRows;
    readonly DispatcherTimer timer;
    readonly DispatcherTimer resizeTimer, noticeTimer;
    // Window size in device pixels. Views are built and resized to it, up to the quality tier's limit.
    Size pixels = new Size(1280, 720);
    int inkRecoveries, surfaceRecoveries, flowRecoveries;
    double timeTotal;
    string notice;
    InkField ink;
    GpuSumiInk gpuInk, paperInk;
    bool paperTried;
    Shallows water;
    GpuWater gpuWater;
    // Fluid and Particles, built when first shown.
    readonly GpuFlow[] flows = new GpuFlow[2];
    readonly bool[] flowTried = new bool[2];
    bool visible, started;
    bool holdSmokeDye;
    bool turbulentSmoke;
    bool coastSmoke;
    bool readingFlow;
    bool typingCoast;
    Color gradientStart, gradientEnd;
    double gradientAmount, gradientAngle, gradientRotation;
    internal void SetColorGradient(Color start, Color end, double amount, double angle, double rotation = 0)
    {
        gradientStart=start;gradientEnd=end;gradientAmount=amount;gradientAngle=angle;gradientRotation=rotation;
        if(gpuInk!=null)gpuInk.SetColorGradient(start,end,amount,angle,rotation);
    }
    double readingBlurAmount, readingBlurHeight=.08;
    int minimumInkFrameMilliseconds;
    internal int MinimumInkFrameMilliseconds
    {
        get { return minimumInkFrameMilliseconds; }
        set { minimumInkFrameMilliseconds=Math.Max(0,value); UpdateTimer(); }
    }
    internal void SetReadingBlur(double amount,double halfHeight)
    {
        readingBlurAmount=amount;readingBlurHeight=halfHeight;
        if(gpuInk!=null)gpuInk.SetReadingBlur(amount,halfHeight);
        UpdateTimer();
    }
    internal bool TypingCoast
    {
        set
        {
            typingCoast = value;
            if (gpuInk != null) gpuInk.TypingCoast = value;
        }
    }
    internal bool ReadingFlow
    {
        set
        {
            readingFlow = value;
            if (gpuInk != null)
                gpuInk.ReadingFlow = value;
            UpdateTimer();
        }
    }
    internal bool CoastSmoke
    {
        set
        {
            coastSmoke = value;
            if (gpuInk != null)
                gpuInk.Coasting = value;
            if (ink != null)
                ink.Coasting = value;
            UpdateTimer();
        }
    }
    internal bool HoldSmokeDye
    {
        get { return holdSmokeDye; }
        set
        {
            holdSmokeDye = value;
            if (gpuInk != null)
                gpuInk.HoldDye = value;
        }
    }
    internal bool TurbulentSmoke
    {
        get { return turbulentSmoke; }
        set
        {
            turbulentSmoke = value;
            if (gpuInk != null)
                gpuInk.Turbulent = value;
        }
    }
    public bool Diagnostics { get; set; }
    public string Notice { get { return notice; } }
    public int SurfaceGeneration { get; private set; }
    public int TimedFrames { get; private set; }
    public double AverageMilliseconds { get { return TimedFrames == 0 ? 0 : timeTotal / TimedFrames; } }
    public WordViewMode Mode { get; private set; }
    public bool HeightFieldActive { get { return Mode == WordViewMode.Water; } }
    public bool FlowActive { get { return Mode >= WordViewMode.Fluid && Mode <= WordViewMode.Particles; } }
    // Smoke and Ink share one system.
    public bool InkActive { get { return Mode == WordViewMode.SumiInk || Mode == WordViewMode.Ink; } }
    GpuSumiInk Paint
    {
        get { return Mode == WordViewMode.SumiInk ? gpuInk : Mode == WordViewMode.Ink ? paperInk : null; }
        set
        {
            if (Mode == WordViewMode.Ink)
                paperInk = value;
            else
            {
                gpuInk = value;
                if (gpuInk != null)
                {
                    gpuInk.HoldDye = holdSmokeDye;
                    gpuInk.Turbulent = turbulentSmoke;
                    gpuInk.Coasting = coastSmoke;
                    gpuInk.TypingCoast = typingCoast;
                    if(readingBlurAmount>0)gpuInk.SetReadingBlur(readingBlurAmount,readingBlurHeight);
                    if(gradientAmount>0)gpuInk.SetColorGradient(gradientStart,gradientEnd,gradientAmount,gradientAngle,gradientRotation);
                    gpuInk.ReadingFlow = readingFlow;
                }
            }
        }
    }
    // The CPU fallback is Smoke's only.
    InkField CpuInk { get { return Mode == WordViewMode.SumiInk ? ink : null; } }
    GpuWater Surface { get { return gpuWater; } }
    GpuFlow Flow { get { return FlowActive ? flows[Mode - WordViewMode.Fluid] : null; } }

    public WordViewEffects(Action redraw, int smokeRows = 288)
    {
        this.redraw = redraw;
        this.smokeRows = smokeRows;
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (sender, args) =>
        {
            if (InkActive)
            {
                if (Paint != null)
                {
                    Paint.Advance();
                    if (Paint.Failed)
                        RecoverInk();
                    else
                    {
                        Time(Paint.Milliseconds);
                        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(minimumInkFrameMilliseconds, Paint.EntranceMoving ? 16 : 33));
                    }
                }
                else if (CpuInk != null)
                    CpuInk.Advance();
            }
            else if (HeightFieldActive)
            {
                GpuWater surface = Surface;
                if (surface != null && !surface.Failed)
                {
                    surface.Advance();
                    if (surface.Failed)
                        RecoverSurface();
                    else
                        Time(surface.Milliseconds);
                }
                else if (water != null)
                    water.Advance();
            }
            else if (Flow != null)
            {
                Flow.Advance();
                if (Flow.Failed)
                    RecoverFlow();
                else
                    Time(Flow.Milliseconds);
            }
            redraw();
            UpdateTimer();
        };
        // Wait for a window drag to settle before rebuilding GPU surfaces.
        resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        resizeTimer.Tick += (sender, args) => ApplyViewport();
        noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        noticeTimer.Tick += (sender, args) =>
        {
            noticeTimer.Stop();
            notice = null;
            redraw();
        };
    }

    public void SetViewport(Size value)
    {
        if (value.Width < 16 || value.Height < 16)
            return;
        bool same = Math.Abs(value.Width - pixels.Width) < 1 && Math.Abs(value.Height - pixels.Height) < 1;
        pixels = value;
        if (!started)
        {
            // Build the startup view at the window's real size.
            started = true;
            Enter();
        }
        else if (!same)
        {
            resizeTimer.Stop();
            resizeTimer.Start();
        }
    }

    void ApplyViewport()
    {
        resizeTimer.Stop();
        if (InkActive && Paint != null)
        {
            Paint.Resize(pixels, GpuQuality.Tier);
            if (Paint.Failed)
                RecoverInk();
        }
        else if (HeightFieldActive && Surface != null && !Surface.Failed)
        {
            Surface.Resize(pixels, GpuQuality.Tier);
            if (Surface.Failed)
                RecoverSurface();
        }
        else if (Flow != null)
        {
            Flow.Resize(pixels, GpuQuality.Tier);
            if (Flow.Failed)
                RecoverFlow();
        }
        ResetTiming();
        redraw();
        UpdateTimer();
    }

    public void CycleQuality()
    {
        SetQuality((QualityTier)(((int)GpuQuality.Tier + 1) % 4));
    }

    public void SetQuality(QualityTier tier)
    {
        GpuQuality.Choose(tier);
        ApplyViewport();
        Announce("Quality: " + tier);
    }

    // A tuning change should be visible at once, even when Smoke has settled.
    public void Wake()
    {
        if (InkActive && Paint != null)
            Paint.Wake();
        redraw();
        UpdateTimer();
    }

    public void ResetTiming()
    {
        timeTotal = 0;
        TimedFrames = 0;
    }

    void Time(float milliseconds)
    {
        if (milliseconds <= 0)
            return;
        timeTotal += milliseconds;
        ++TimedFrames;
        if (GpuQuality.Observe(milliseconds))
        {
            ApplyViewport();
            Announce("Quality: " + GpuQuality.Tier);
        }
    }

    void Announce(string text)
    {
        notice = text;
        noticeTimer.Stop();
        noticeTimer.Start();
        redraw();
    }

    // A lost device takes its dye with it. Rebuild the view, then fall back to the CPU.
    void RecoverInk()
    {
        Paint.Dispose();
        Paint = inkRecoveries++ < 2 ? GpuSumiInk.TryCreate(pixels, GpuQuality.Tier, Mode == WordViewMode.Ink,
            Mode == WordViewMode.SumiInk ? smokeRows : 288) : null;
        if (Paint != null)
            return;
        if (Mode == WordViewMode.Ink)
            Announce("GPU unavailable. This view needs the GPU.");
        else
        {
            if (ink == null)
                ink = new InkField { Coasting = coastSmoke };
            Announce("GPU unavailable. Smoke is running on the CPU.");
        }
    }

    void RecoverSurface()
    {
        gpuWater.Dispose();
        gpuWater = surfaceRecoveries++ < 2 ? GpuWater.TryCreate(pixels, GpuQuality.Tier) : null;
        // The word display sends the held word again when this changes.
        ++SurfaceGeneration;
    }

    void RecoverFlow()
    {
        int index = Mode - WordViewMode.Fluid;
        flows[index].Dispose();
        flows[index] = flowRecoveries++ < 2 ? GpuFlow.TryCreate(Mode, pixels, GpuQuality.Tier) : null;
        ++SurfaceGeneration;
        if (flows[index] == null)
            Announce("GPU unavailable. This view needs the GPU.");
    }

    public string Describe()
    {
        string grid = "CPU", adapter = String.Empty;
        float milliseconds = 0;
        if (InkActive && Paint != null)
        {
            grid = Paint.Grid;
            adapter = Paint.Adapter;
            milliseconds = Paint.Milliseconds;
        }
        else if (HeightFieldActive && Surface != null && !Surface.Failed)
        {
            grid = Surface.Grid;
            adapter = Surface.Adapter;
            milliseconds = Surface.Milliseconds;
        }
        else if (Flow != null)
        {
            grid = Flow.Grid;
            adapter = Flow.Adapter;
            milliseconds = Flow.Milliseconds;
        }
        return String.Format(CultureInfo.InvariantCulture, "{0} · {1} · {2:0.00} ms · {3} · {4}",
            Mode == WordViewMode.SumiInk ? "Smoke" : Mode.ToString(), GpuQuality.Tier, milliseconds, grid, adapter);
    }

    // Tab order: Smoke, Water, Fluid, Particles, Ink.
    public void Cycle(bool backwards)
    {
        int count = (int)WordViewMode.Ink + 1;
        Mode = (WordViewMode)(((int)Mode + (backwards ? count - 1 : 1)) % count);
        started = true;
        Enter();
    }

    void Enter()
    {
        timer.Interval = TimeSpan.FromMilliseconds(33);
        switch (Mode)
        {
            case WordViewMode.SumiInk:
                if (gpuInk == null && ink == null)
                {
                    Paint = GpuSumiInk.TryCreate(pixels, GpuQuality.Tier, false, smokeRows);
                    if (gpuInk == null)
                    {
                        ink = new InkField { Coasting = coastSmoke };
                        Announce("GPU unavailable. Smoke is running on the CPU.");
                    }
                }
                else if (gpuInk != null)
                    gpuInk.Resize(pixels, GpuQuality.Tier);
                break;
            case WordViewMode.Ink:
                if (paperInk != null)
                    paperInk.Resize(pixels, GpuQuality.Tier);
                else if (!paperTried)
                {
                    paperTried = true;
                    paperInk = GpuSumiInk.TryCreate(pixels, GpuQuality.Tier, true);
                }
                if (paperInk == null)
                    Announce("GPU unavailable. This view needs the GPU.");
                break;
            case WordViewMode.Water:
                if (water == null)
                {
                    water = new Shallows();
                    gpuWater = GpuWater.TryCreate(pixels, GpuQuality.Tier);
                }
                else if (gpuWater != null)
                    gpuWater.Resize(pixels, GpuQuality.Tier);
                timer.Interval = TimeSpan.FromMilliseconds(16);
                break;
            default:
                int index = Mode - WordViewMode.Fluid;
                if (flows[index] != null)
                    flows[index].Resize(pixels, GpuQuality.Tier);
                else if (!flowTried[index])
                {
                    flowTried[index] = true;
                    flows[index] = GpuFlow.TryCreate(Mode, pixels, GpuQuality.Tier);
                }
                if (flows[index] == null)
                    Announce("GPU unavailable. This view needs the GPU.");
                timer.Interval = TimeSpan.FromMilliseconds(16);
                break;
        }
        ResetTiming();
        UpdateTimer();
    }

    internal void SeedAmbient()
    {
        if (Mode == WordViewMode.SumiInk && gpuInk != null)
        {
            gpuInk.SeedAmbient();
            UpdateTimer();
        }
    }

    public void SetVisible(bool value)
    {
        visible = value;
        UpdateTimer();
    }

    public void Close()
    {
        timer.Stop();
        resizeTimer.Stop();
        noticeTimer.Stop();
        if (gpuInk != null)
            gpuInk.Dispose();
        if (paperInk != null)
            paperInk.Dispose();
        if (gpuWater != null)
            gpuWater.Dispose();
        foreach (GpuFlow flow in flows)
            if (flow != null)
                flow.Dispose();
    }

    public void StartInkEntrance(Geometry glyphs, Size panel, Func<Point> scale)
    {
        if (Paint == null)
            return;
        Paint.StartEntrance(glyphs, panel, scale);
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, minimumInkFrameMilliseconds));
        UpdateTimer();
    }

    void UpdateTimer()
    {
        if (InkActive && timer.Interval.TotalMilliseconds < minimumInkFrameMilliseconds)
            timer.Interval = TimeSpan.FromMilliseconds(minimumInkFrameMilliseconds);
        bool moving;
        if (InkActive)
            moving = Paint != null ? Paint.Moving : CpuInk != null && CpuInk.Moving;
        else if (HeightFieldActive)
            moving = Surface != null && !Surface.Failed ? Surface.Moving : water != null && water.Moving;
        else
            moving = Flow != null && Flow.Moving;
        timer.IsEnabled = visible && moving;
    }

    public void Disturb(bool commit, Geometry glyphs, Rect panel, double letterX)
    {
        if (InkActive)
        {
            if (Paint != null)
                Paint.Disturb(glyphs, panel.Size, commit, letterX);
            else if (CpuInk != null)
                CpuInk.Disturb(glyphs, panel.Size, commit);
        }
        else if (HeightFieldActive)
        {
            if (Surface != null && !Surface.Failed)
                DisturbSurface(glyphs, panel.Size, commit);
            else if (water != null)
                water.Disturb(commit);
        }
        else if (Flow != null)
            Flow.Disturb(commit, letterX);
        redraw();
        UpdateTimer();
    }

    internal void PushVortex(double x, double y, double aspect, float strength, double expansion = 0)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.PushVortex(x, y, aspect, strength, expansion);
        else if (CpuInk != null)
            CpuInk.PullAround(x, y, strength, expansion);
        UpdateTimer();
    }

    internal void DriveWindTunnel(double aspect, float strength, bool emit)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.DriveWindTunnel(aspect, strength, emit);
        else if (CpuInk != null)
            CpuInk.DriveWindTunnel(aspect, strength, emit);
        UpdateTimer();
    }

    internal void AbsorbFlow(BitmapSource mask,double x,double y,double velocity,double release)
    {
        if(Mode!=WordViewMode.SumiInk)return;
        if(Paint!=null)Paint.AbsorbFlow(mask,x,y,velocity,release);
        else if(CpuInk!=null)CpuInk.AbsorbFlow(mask,x,y,velocity,release);
        redraw();
        UpdateTimer();
    }

    internal void StirLine(double x, double y, double radius, double angle, double strength)
    {
        if (Mode != WordViewMode.SumiInk) return;
        if (Paint != null)
            Paint.StirLine((float)x, (float)y, (float)radius, (float)angle, (float)strength);
        else if (CpuInk != null)
            CpuInk.StirLine((float)x, (float)y, (float)radius, (float)angle, (float)strength);
        UpdateTimer();
    }

    internal void ApplyWind(SmokeWind[] forces, double aspect)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.ApplyWind(forces);
        else if (CpuInk != null)
            CpuInk.ApplyWind(forces, aspect);
        UpdateTimer();
    }

    internal void EmitCenterJets(double aspect, bool swapped)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.EmitCenterJets(aspect, swapped);
        else if (CpuInk != null)
        {
            double offset = .035 * Math.Min(1, aspect) / aspect;
            CpuInk.EmitMist(.5 + offset, .5, aspect);
            CpuInk.EmitMist(.5 - offset, .5, aspect);
        }
        UpdateTimer();
    }

    internal void EmitMist(double x, double y, double direction, double aspect, bool dark = false, float size = .19f)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.EmitMist(x, y, direction, dark, size);
        else if (CpuInk != null)
            CpuInk.EmitMist(x, y, aspect);
        UpdateTimer();
    }

    internal void SpreadSmoke(double x, double y, double aspect, double reach, float strength)
    {
        if (Mode != WordViewMode.SumiInk)
            return;
        if (Paint != null)
            Paint.SpreadSmoke(x, y, aspect, reach, strength);
        else if (CpuInk != null)
            CpuInk.Spread(x, y, reach, strength);
        UpdateTimer();
    }

    public void DepositWordVersion(Geometry glyph, Size panel)
    {
        DepositWordVersion(glyph, panel, null);
    }

    // letter is the just-typed letter's shape in the panel, or null when no letter was typed.
    public void DepositWordVersion(Geometry glyph, Size panel, Geometry letter)
    {
        if (InkActive)
        {
            if (Paint != null)
                Paint.DepositWordVersion(glyph, panel);
            else if (CpuInk != null)
                CpuInk.Disturb(glyph, panel, false);
        }
        else if (Flow != null)
            Flow.Deposit(glyph, panel, letter);
        else
            return;
        redraw();
        UpdateTimer();
    }

    public void SetSurfaceWord(Geometry glyph, Size panel, List<Point> letterCenters)
    {
        if (Surface == null || Surface.Failed)
            return;
        if (glyph == null)
            Surface.ClearWord();
        else
            DisturbSurface(glyph, panel, false, letterCenters);
        UpdateTimer();
    }

    // The word shown in Fluid or Particles. A null glyph is a blank step.
    public void SetFlowWord(Geometry glyph, Size panel)
    {
        if (Flow != null)
            Flow.SetWord(glyph, panel);
    }

    void DisturbSurface(Geometry glyph, Size panel, bool commit, List<Point> letterCenters = null)
    {
        if (glyph == null)
            return;
        Rect bounds = WaterBounds(new Rect(panel));
        var translated = new GeometryGroup { Transform = new TranslateTransform(-bounds.X, -bounds.Y) };
        translated.Children.Add(glyph);
        Surface.Disturb(translated, bounds.Size, commit, letterCenters, bounds.Location);
    }

    static Rect WaterBounds(Rect panel)
    {
        double scale = Math.Max(panel.Width / Shallows.Width, panel.Height / Shallows.Height);
        double width = Shallows.Width * scale, height = Shallows.Height * scale;
        return new Rect(panel.X + (panel.Width - width) / 2,
            panel.Y + (panel.Height - height) / 2, width, height);
    }

    public void Draw(DrawingContext drawing, Rect panel)
    {
        if (panel.Width <= 0 || panel.Height <= 0)
            return;
        drawing.PushClip(new RectangleGeometry(panel));
        if (InkActive)
        {
            ImageSource dye = Paint != null ? Paint.Image : CpuInk != null ? CpuInk.Image : null;
            if (dye != null)
                drawing.DrawImage(dye, panel);
        }
        else if (HeightFieldActive)
        {
            Rect imageBounds = WaterBounds(panel);
            ImageSource wave = Surface == null || Surface.Failed ? null : Surface.Image;
            if (wave != null)
                drawing.DrawImage(wave, imageBounds);
            else if (water != null)
            {
                drawing.DrawImage(water.Bottom, imageBounds);
                if (water.Ripple != null)
                    drawing.DrawImage(water.Ripple, imageBounds);
            }
        }
        else if (Flow != null && Flow.Image != null)
            drawing.DrawImage(Flow.Image, panel);
        drawing.Pop();
    }

    sealed class InkField
    {
        const int Width = 256, Height = 144;
        static readonly Int32Rect Region = new Int32Rect(0, 0, Width, Height);
        readonly WriteableBitmap image = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        readonly byte[] pixels = new byte[Width * Height * 4];
        float[] pigment = new float[Width * Height];
        float[] next = new float[Width * Height];
        readonly List<Vortex> vortices = new List<Vortex>();
        readonly Random random = new Random();
        bool hasInk;
        int nextTurn;
        float pullX, pullY, pullStrength, pullRadial = -.30f, puffX, puffY, puffRadius, puffStrength;
        float tunnelStrength, tunnelOrbit;
        SmokeWind[] workshopForces;
        double workshopAspect;
        int workshopFrames;

        sealed class Vortex
        {
            public float X, Y, Radius, Spin, DriftX, DriftY;
            public int Age;
        }

        public BitmapSource Image { get { return image; } }
        internal bool Coasting { get; set; }
        public bool Moving { get { return hasInk && (vortices.Count > 0 || pullStrength > .02f || puffStrength > .02f || tunnelStrength > .02f || workshopFrames > 0); } }

        internal void AbsorbFlow(BitmapSource mask,double x,double y,double velocity,double release)
        {
            var visual=new DrawingVisual();
            using(var drawing=visual.RenderOpen())drawing.DrawImage(mask,new Rect(0,0,Width,Height));
            var resized=new RenderTargetBitmap(Width,Height,96,96,PixelFormats.Pbgra32);
            resized.Render(visual);
            resized.CopyPixels(pixels,Width*4,0);
            for(int i=0;i<pigment.Length;++i){
                float incoming=Math.Min(1,pixels[i*4+3]/255f*ViewTuning.Get("smoke.amount"));
                pigment[i]+=incoming*(1-pigment[i]);
            }
            hasInk=true;
            StirLine((float)x,(float)y,.14f,0,8.4f*ViewTuning.Get("smoke.push")*(float)release);
            puffX=(float)(x*Width);puffY=(float)(y*Height);
            puffRadius=Height*.14f;puffStrength+=8.4f*ViewTuning.Get("smoke.push")*(float)release;
            Publish();
        }

        internal void StirLine(float x, float y, float radius, float angle, float strength)
        {
            float r = radius * Height;
            float dx = -(float)Math.Sin(angle) * r * .75f;
            float dy = (float)Math.Cos(angle) * r * .75f;
            AddVortex(x * Width + dx, y * Height + dy, r, strength);
            AddVortex(x * Width - dx, y * Height - dy, r, -strength);
        }

        internal void ApplyWind(SmokeWind[] forces, double aspect)
        {
            workshopForces = forces;
            workshopAspect = aspect;
            workshopFrames = 3;
        }

        internal void DriveWindTunnel(double aspect, float strength, bool emit)
        {
            tunnelStrength = strength;
            tunnelOrbit = (float)(.23 * Math.Min(1, aspect) * Height);
            if (emit)
            {
                EmitMist(.5, .5 - tunnelOrbit / Height, aspect);
                EmitMist(.5, .5 + tunnelOrbit / Height, aspect);
            }
        }

        internal void PullAround(double x, double y, float strength, double expansion)
        {
            pullX = (float)(x * Width);
            pullY = (float)(y * Height);
            pullStrength = strength;
            pullRadial = (float)(expansion - .30);
        }

        internal void Spread(double x, double y, double reach, float strength)
        {
            puffX = (float)(x * Width);
            puffY = (float)(y * Height);
            puffRadius = Height * (float)(reach + .12);
            puffStrength = strength;
        }

        internal void EmitMist(double x, double y, double aspect)
        {
            double centerX = x * Width, centerY = y * Height;
            double radiusX = Width * .075 / aspect, radiusY = Height * .075;
            int left = Math.Max(0, (int)Math.Floor(centerX - radiusX));
            int right = Math.Min(Width - 1, (int)Math.Ceiling(centerX + radiusX));
            int top = Math.Max(0, (int)Math.Floor(centerY - radiusY));
            int bottom = Math.Min(Height - 1, (int)Math.Ceiling(centerY + radiusY));
            for (int row = top; row <= bottom; row++)
            for (int column = left; column <= right; column++)
            {
                double dx = (column - centerX) / radiusX, dy = (row - centerY) / radiusY;
                double falloff = Math.Max(0, 1 - dx * dx - dy * dy);
                int cell = row * Width + column;
                pigment[cell] += .24f * (float)(falloff * falloff) * (1 - pigment[cell]);
            }
            hasInk = true;
        }

        public void Disturb(Geometry glyphs, Size panel, bool commit)
        {
            if (glyphs != null)
                Deposit(glyphs, panel);
            if (!hasInk)
                return;

            // New input adds circulation to the shared field, including pigment from earlier words.
            double phase = nextTurn++ * 2.399963;
            float x = Width * 0.5f + (float)Math.Cos(phase) * (commit ? 22 : 42);
            float y = Height * 0.5f + (float)Math.Sin(phase) * (commit ? 15 : 25);
            float direction = (nextTurn % 2 == 0) ? 1 : -1;
            AddVortex(x, y, commit ? 24 : 30, direction * (commit ? 8f : 2.8f));
            if (commit)
            {
                AddVortex(x - 33, y + 19, 16, -direction * 5.5f);
                AddVortex(x + 34, y - 18, 22, -direction * 7f);
                // Wide words need shear at their edges as well as at the center.
                if (glyphs != null && glyphs.Bounds.Width > panel.Width * 0.45)
                {
                    Rect bounds = glyphs.Bounds;
                    float left = (float)((bounds.Left + bounds.Width * 0.13) / panel.Width * Width);
                    float right = (float)((bounds.Right - bounds.Width * 0.13) / panel.Width * Width);
                    AddVortex(left, Height * 0.44f, 20, direction * 6.8f);
                    AddVortex(right, Height * 0.55f, 20, -direction * 6.4f);
                }
            }
        }

        void AddVortex(float x, float y, float radius, float spin)
        {
            if (vortices.Count == 15) vortices.RemoveAt(0);
            vortices.Add(new Vortex
            {
                X = x, Y = y, Radius = radius, Spin = spin,
                DriftX = (float)(random.NextDouble() - 0.5) * 0.52f,
                DriftY = (float)(random.NextDouble() - 0.5) * 0.42f
            });
        }

        void Deposit(Geometry glyphs, Size panel)
        {
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(Width / panel.Width, Height / panel.Height));
                drawing.DrawGeometry(Brushes.White, null, glyphs);
                drawing.Pop();
            }
            var mask = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            mask.Render(visual);
            mask.CopyPixels(pixels, Width * 4, 0);
            for (int i = 0; i < pigment.Length; ++i)
            {
                float incoming = pixels[i * 4 + 3] / 255f * 0.64f;
                pigment[i] += incoming * (1 - pigment[i]);
            }
            hasInk = true;
            Publish();
        }

        public void Advance()
        {
            if (!Moving) return;
            for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                float vx = 0, vy = 0;
                foreach (Vortex vortex in vortices)
                {
                    float dx = x - vortex.X, dy = y - vortex.Y;
                    float r = vortex.Radius;
                    float distance = (dx * dx + dy * dy) / (r * r);
                    float core = 1 / (1 + distance);
                    float twist = vortex.Spin * core * core / r;
                    vx -= dy * twist;
                    vy += dx * twist;
                }
                if (pullStrength > .02f)
                {
                    float px = x - pullX, py = y - pullY;
                    float pull = pullStrength * .048f * (float)Math.Exp(-(px * px + py * py) / 2500);
                    vx += (-py + pullRadial * px) * pull;
                    vy += (px + pullRadial * py) * pull;
                }
                if (puffStrength > .02f)
                {
                    float px = x - puffX, py = y - puffY;
                    float puff = puffStrength * .047f * (float)Math.Exp(-(px * px + py * py) / (puffRadius * puffRadius));
                    vx += (px - .17f * py) * puff;
                    vy += (py + .17f * px) * puff;
                }
                if (tunnelStrength > .02f)
                {
                    float px = x - Width * .5f;
                    float top = y - (Height * .5f - tunnelOrbit);
                    float bottom = y - (Height * .5f + tunnelOrbit);
                    float radius = Height * .16f;
                    float upper = (float)Math.Exp(-(px * px + top * top) / (radius * radius));
                    float lower = (float)Math.Exp(-(px * px + bottom * bottom) / (radius * radius));
                    vx += (upper - lower) * tunnelStrength * .65f;
                }
                if (workshopFrames > 0)
                    foreach (SmokeWind force in workshopForces)
                    {
                        double dx = ((double)x / Width - force.X) * workshopAspect;
                        double dy = (double)y / Height - force.Y;
                        double weight = Math.Exp(-(dx * dx + dy * dy) / (force.Radius * force.Radius));
                        vx += (float)(Math.Cos(force.Direction) * force.Strength * weight);
                        vy += (float)(Math.Sin(force.Direction) * force.Strength * weight);
                    }
                // Pin the outside cells to keep pigment in the panel.
                float edge = Math.Min(1, Math.Min(Math.Min(x, Width - 1 - x), Math.Min(y, Height - 1 - y)) / 10f);
                float step = edge * (Coasting ? 3.2f : 1f);
                next[y * Width + x] = Sample(pigment, Width, Height, x - vx * step, y - vy * step);
            }
            var previous = pigment;
            pigment = next;
            next = previous;
            if (!Coasting)
            {
                pullStrength *= .91f;
                puffStrength *= .81f;
                tunnelStrength *= .96f;
                workshopFrames = Math.Max(0, workshopFrames - 1);
            }
            for (int i = vortices.Count - 1; i >= 0; --i)
            {
                Vortex vortex = vortices[i];
                vortex.X += vortex.DriftX;
                vortex.Y += vortex.DriftY;
                if (!Coasting)
                {
                    vortex.Spin *= 0.967f;
                    if (++vortex.Age == 110) vortices.RemoveAt(i);
                }
            }
            Publish();
        }

        void Publish()
        {
            for (int i = 0; i < pigment.Length; ++i)
            {
                // Premultiplied white with cool undertones. No opacity decay between words.
                byte alpha = (byte)Math.Min(228, 290 * pigment[i]);
                int offset = 4 * i;
                pixels[offset] = (byte)(alpha * 0.56f);
                pixels[offset + 1] = (byte)(alpha * 0.46f);
                pixels[offset + 2] = (byte)(alpha * 0.28f);
                pixels[offset + 3] = alpha;
            }
            image.WritePixels(Region, pixels, Width * 4, 0);
        }

        static float Sample(float[] field, int width, int height, float x, float y)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(0, Math.Min(height - 1, y));
            int left = (int)x, top = (int)y;
            int right = Math.Min(width - 1, left + 1), bottom = Math.Min(height - 1, top + 1);
            float u = x - left, v = y - top;
            return (field[top * width + left] * (1 - u) + field[top * width + right] * u) * (1 - v) +
                (field[bottom * width + left] * (1 - u) + field[bottom * width + right] * u) * v;
        }
    }

    sealed class Shallows
    {
        public const int Width = 480, Height = 270;
        static readonly Int32Rect Region = new Int32Rect(0, 0, Width, Height);
        readonly BitmapSource bottom;
        readonly WriteableBitmap ripple = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        readonly byte[] texture = new byte[Width * Height * 4];
        readonly byte[] pixels = new byte[Width * Height * 4];
        readonly List<Pulse> pulses = new List<Pulse>();
        int nextPulse;
        bool hasFrame;

        sealed class Pulse
        {
            public float X, Y, Strength;
            public int Age;
        }

        public Shallows()
        {
            for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                double light = 0.52 + 0.48 * (1 - (x + y) / (double)(Width + Height));
                int i = 4 * (y * Width + x);
                texture[i] = (byte)(135 * light);
                texture[i + 1] = (byte)(133 * light);
                texture[i + 2] = (byte)(43 * light);
            }
            bottom = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, texture, Width * 4);
            bottom.Freeze();
        }

        public bool Moving { get { return pulses.Count > 0; } }
        public BitmapSource Bottom { get { return bottom; } }
        public BitmapSource Ripple { get { return Moving && hasFrame ? ripple : null; } }

        public void Disturb(bool commit)
        {
            if (pulses.Count == 7) pulses.RemoveAt(0);
            double phase = nextPulse++ * 2.399963;
            pulses.Add(new Pulse
            {
                X = Width * 0.5f + (float)Math.Cos(phase) * (commit ? 12 : 55),
                Y = Height * 0.5f + (float)Math.Sin(phase) * (commit ? 8 : 23),
                Strength = commit ? 9 : 4
            });
        }

        public void Advance()
        {
            for (int i = pulses.Count - 1; i >= 0; --i)
                if (++pulses[i].Age == 80) pulses.RemoveAt(i);
            if (!Moving)
            {
                hasFrame = false;
                return;
            }

            for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                float dx = 0, dy = 0, light = 0, coverage = 0;
                foreach (Pulse pulse in pulses)
                {
                    float px = x - pulse.X, py = y - pulse.Y;
                    float center = pulse.Age * 4.4f;
                    float distanceSquared = px * px + py * py;
                    if (distanceSquared < 1 || distanceSquared > (center + 29) * (center + 29)) continue;
                    float distance = (float)Math.Sqrt(distanceSquared);
                    float front = distance - center;
                    if (front < -29) continue;
                    float envelope = 1 - front * front / (29 * 29);
                    float strength = pulse.Strength * envelope * envelope * (1 - pulse.Age / 80f);
                    coverage = Math.Max(coverage, strength);
                    float slope = strength * (float)Math.Sin(front * 0.34f);
                    dx += slope * px / distance;
                    dy += slope * py / distance;
                    light += strength * (float)Math.Cos(front * 0.34f) * 1.8f;
                }
                int offset = 4 * (y * Width + x);
                byte alpha = (byte)Math.Min(255, coverage * 145);
                pixels[offset + 3] = alpha;
                if (alpha == 0)
                {
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
                    continue;
                }
                SampleTexture(x + dx, y + dy, light, offset, alpha);
            }
            ripple.WritePixels(Region, pixels, Width * 4, 0);
            hasFrame = true;
        }

        void SampleTexture(float x, float y, float light, int offset, byte alpha)
        {
            x = Math.Max(0, Math.Min(Width - 1, x));
            y = Math.Max(0, Math.Min(Height - 1, y));
            int left = (int)x, top = (int)y;
            int right = Math.Min(Width - 1, left + 1), bottom = Math.Min(Height - 1, top + 1);
            float u = x - left, v = y - top;
            for (int c = 0; c < 3; ++c)
            {
                float upper = texture[(top * Width + left) * 4 + c] * (1 - u) + texture[(top * Width + right) * 4 + c] * u;
                float lower = texture[(bottom * Width + left) * 4 + c] * (1 - u) + texture[(bottom * Width + right) * 4 + c] * u;
                float color = Math.Max(0, Math.Min(255, upper * (1 - v) + lower * v + light));
                pixels[offset + c] = (byte)(color * alpha / 255);
            }
        }
    }
}
