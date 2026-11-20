using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

sealed class CloudSmokeView : FrameworkElement, IDisposable
{
    readonly WordViewEffects smoke;
    readonly ScaleTransform pop = new ScaleTransform();
    static readonly CubicEase SnapEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly SineEase ReboundEase = new SineEase { EasingMode = EasingMode.EaseInOut };
    static readonly double[] HorizontalPop = { .82, 1.10, .96 };
    static readonly double[] VerticalPop = { .62, .94, 1.08 };
    internal static Color PaperColor { get { return ViewTuning.ColorOf("smoke.paper"); } }
    internal static readonly Brush Backdrop = new SolidColorBrush(PaperColor);
    BitmapSource cornerBackground;
    internal readonly ChatColorProfiles Colors = new ChatColorProfiles();
    internal int Phase { get; private set; }
    int? previewPhase;
    bool replyPalette;
    readonly SolidColorBrush previewTint = new SolidColorBrush();
    readonly SolidColorBrush typingTint = new SolidColorBrush { Opacity = 0 };

    internal void PreviewColors(int? phase)
    {
        previewPhase=phase;
        ApplyColors();
    }

    void ApplyColors()
    {
        int phase=previewPhase ?? Phase;
        ViewTuning.SetColor("smoke.paper",Colors.Get(phase,"background"));
        for(int i=4;i<ChatColorProfiles.Keys.Length;i++)
            ViewTuning.SetColor(ChatColorProfiles.Keys[i],Colors.Get(phase,ChatColorProfiles.Keys[i]));
        smoke.SetColorGradient(Colors.Get(phase,"background"),Colors.Get(phase,"corner"),
            Colors.Gradient(phase),Colors.Angle(phase),Colors.Rotation(phase));
        cornerBackground=null;
        thinkingPaper.Color=Colors.Get(phase,"background");
        thinkingTint.Color=Colors.Get(1,"tint");
        replyTint.Color=Colors.Get(2,"tint");
        typingTint.Color=Colors.Get(0,"tint");
        typingTint.BeginAnimation(Brush.OpacityProperty,new DoubleAnimation(typingTint.Opacity,
            Phase==0 ? Colors.Strength(0) : 0,TimeSpan.FromMilliseconds(700)) { EasingFunction=ReboundEase });
        previewTint.Color=Colors.Get(phase,"tint");
        previewTint.Opacity=Colors.Strength(phase);
        smoke.Wake();
        InvalidateVisual();
    }
    readonly SolidColorBrush thinkingTint = new SolidColorBrush(Color.FromRgb(123, 76, 107));
    readonly SolidColorBrush replyTint = new SolidColorBrush(Color.FromRgb(72, 177, 245)) { Opacity = 0 };
    readonly SolidColorBrush thinkingPaper = new SolidColorBrush(PaperColor) { Opacity = 0 };
    readonly List<KineticReader.LineWake> lineWakes = new List<KineticReader.LineWake>();
    int nextLine;
    Rect panel;
    string word = "", depositedWord, error = "";
    double nextReplyPush;
    Point lastPopScale = new Point(.82, .62);
    bool entrancePending, thinkingPalette, retainedPigment, suspended;
    readonly Stopwatch entranceClock = new Stopwatch();
    readonly Stopwatch draftActivity = Stopwatch.StartNew();

    internal CloudSmokeView()
    {
        smoke = new WordViewEffects(InvalidateVisual, 576);
        smoke.TypingCoast = true;
        Colors.Changed += () => { ApplyColors(); SetThinkingPalette(thinkingPalette,true); SetReplyPalette(replyPalette); };
        ApplyColors();
        thinkingTint.Opacity = 0;
        retainedPigment = true;
        smoke.HoldSmokeDye = true;
        smoke.CoastSmoke = true;
        IsVisibleChanged += (sender, args) => smoke.SetVisible(IsVisible && !suspended);
    }

    internal static readonly DependencyProperty ReadingBlurProperty=DependencyProperty.Register(
        "ReadingBlur",typeof(double),typeof(CloudSmokeView),
        new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender,
            (d,e)=>((CloudSmokeView)d).ApplyReadingBlur()));
    double readingBlurHeight=.08;
    internal double ReadingBlur { get { return (double)GetValue(ReadingBlurProperty); } }
    void ApplyReadingBlur()
    {
        if(smoke!=null)smoke.SetReadingBlur(ReadingBlur,readingBlurHeight);
    }
    internal void SetReadingBlurHeight(double height)
    {
        height=Math.Max(.04,Math.Min(.20,height));
        if(Math.Abs(height-readingBlurHeight)<.003)return;
        readingBlurHeight=height;ApplyReadingBlur();
    }
    void FadeReadingBlur(bool enabled)
    {
        BeginAnimation(ReadingBlurProperty,new DoubleAnimation(ReadingBlur,enabled?.60:0,
            TimeSpan.FromMilliseconds(enabled?650:700)) { EasingFunction=ReboundEase });
    }

    internal void SetSuspended(bool value)
    {
        suspended = value;
        smoke.SetVisible(IsVisible && !suspended);
    }

    internal string VisibleWord { get { return word; } }

    BitmapSource CornerBackground(int width, int height)
    {
        if (cornerBackground != null && cornerBackground.PixelWidth == width && cornerBackground.PixelHeight == height)
            return cornerBackground;
        var pixels = new byte[width * height * 4];
        Color corner=Colors.Get(previewPhase ?? Phase,"corner");
        Color background=PaperColor;

        for (int y = 0; y < height; ++y)
        {

            for (int x = 0; x < width; ++x)
            {
                double across = (x + .5) / width;
                double shade = across;
                shade=shade*shade*(3-2*shade);
                // Stochastic rounding breaks 8-bit contours without a visible grain pattern.
                uint hash = unchecked((uint)x * 374761393u + (uint)y * 668265263u);
                hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
                hash ^= hash >> 16;
                double dither = (hash & 65535) / 65536.0;
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)Math.Min(255,background.B+(corner.B-background.B)*shade+dither);
                pixels[offset + 1] = (byte)Math.Min(255,background.G+(corner.G-background.G)*shade+dither);
                pixels[offset + 2] = (byte)Math.Min(255,background.R+(corner.R-background.R)*shade+dither);
                pixels[offset + 3] = 255;
            }
        }
        cornerBackground = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        cornerBackground.Freeze();
        return cornerBackground;
    }

    void DrawBackground(DrawingContext drawing)
    {
        int phase=previewPhase ?? Phase;
        double radians=(Colors.Angle(phase)+Colors.Rotation(phase)*GpuSumiInk.GradientSeconds)*Math.PI/180;
        double x=Math.Cos(radians)/panel.Width,y=Math.Sin(radians)/panel.Height;
        double angle=Math.Atan2(y,x),cs=Math.Cos(angle),sn=Math.Sin(angle);
        double width=panel.Width*Math.Abs(cs)+panel.Height*Math.Abs(sn);
        double height=panel.Width*Math.Abs(sn)+panel.Height*Math.Abs(cs);
        drawing.PushClip(new RectangleGeometry(panel));
        drawing.PushTransform(new RotateTransform(angle*180/Math.PI,panel.Width/2,panel.Height/2));
        drawing.DrawImage(CornerBackground(768,256),new Rect((panel.Width-width)/2,(panel.Height-height)/2,width,height));
        drawing.Pop();drawing.Pop();
    }

    internal void ShowWord(string value, bool restart = false)
    {
        if (word == value && !restart)
            return;
        if (restart)
            depositedWord = null;
        word = value;
        entrancePending = !String.IsNullOrEmpty(value);
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, entrancePending ? PopAnimation(HorizontalPop) : null);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, entrancePending ? PopAnimation(VerticalPop) : null);
        if (entrancePending)
            entranceClock.Restart();
        else
            entranceClock.Reset();
        InvalidateVisual();
    }

    static DoubleAnimationUsingKeyFrames PopAnimation(double[] scale)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(scale[0], KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(scale[1], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(48)), SnapEase));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(scale[2], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(86)), ReboundEase));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(145)), SnapEase));
        return animation;
    }

    Point ReadPopScale()
    {
        if (!smoke.HoldSmokeDye)
            lastPopScale = new Point(pop.ScaleX, pop.ScaleY);
        return lastPopScale;
    }

    internal void DepositTypedVersion(string prefix, string inserted, string suffix)
    {
        draftActivity.Restart();
        BeginDraftInput();
        if (smoke.HoldSmokeDye || panel.IsEmpty || panel.Width <= 0)
            return;
        var elements = StringInfo.GetTextElementEnumerator(inserted);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (String.IsNullOrWhiteSpace(element))
                prefix = "";
            else
            {
                prefix += element;
                Deposit(prefix + suffix);
            }
        }
        double caret = (double)prefix.Length / Math.Max(1, prefix.Length + suffix.Length);
        Geometry glyph = BuildWord(word);
        double letterX = glyph == null ? .5 : (glyph.Bounds.Left + glyph.Bounds.Width * caret) / panel.Width;
        smoke.Disturb(false, null, panel, letterX);
    }

    internal void CommitWord()
    {
        if (smoke.HoldSmokeDye)
            return;
        Geometry glyph = BuildWord(word);
        if (glyph != null)
            smoke.Disturb(true, glyph, panel, .5);
        depositedWord = null;
    }

    internal void ShowError(string message)
    {
        error = message ?? "";
        InvalidateVisual();
    }

    internal void BeginThinking()
    {
        ClearReplySmoke();
        smoke.MinimumInkFrameMilliseconds = 0;
        FadeReadingBlur(false);
        Phase=1; ApplyColors();
        SetReplyPalette(false);
        HoldPigment();
        smoke.ReadingFlow = false;
        smoke.CoastSmoke = true;
        SetThinkingPalette(true);
    }

    int replyWordStart = -1;
    string replyPrefix = "";
    readonly Stopwatch replySmokeClock = Stopwatch.StartNew();
    double lastReplySmoke = Double.NegativeInfinity;
    int pendingReplyStart;
    string pendingReplyPrefix, pendingReplyLetter;
    internal bool ReplySmokePending { get { return pendingReplyPrefix != null; } }

    internal void ClearReplySmoke()
    {
        pendingReplyPrefix = pendingReplyLetter = null;
        lastReplySmoke = Double.NegativeInfinity;
    }

    internal void BeginReply()
    {
        ClearReplySmoke();
        smoke.MinimumInkFrameMilliseconds = 33;
        FadeReadingBlur(true);
        Phase=2; ApplyColors();
        ResumePigmentFade();
        SetReplyPalette(true);
        smoke.ReadingFlow = false;
        smoke.CoastSmoke = false;
        replyWordStart = -1;
        replyPrefix = "";
        ShowWord("");
        smoke.Wake();
    }

    internal void TypeReplyLetter(int start, string prefix, string letter)
    {
        QueueReplyLetter(start,prefix,letter);
        FlushReplySmoke();
    }

    internal void QueueReplyLetter(int start, string prefix, string letter)
    {
        pendingReplyStart = start;
        pendingReplyPrefix = prefix;
        pendingReplyLetter = letter;
    }

    internal void FlushReplySmoke()
    {
        FlushReplySmokeAt(replySmokeClock.Elapsed.TotalSeconds);
    }

    internal void FlushReplySmokeAt(double now)
    {
        if (!ReplySmokePending || now - lastReplySmoke < .050)
            return;
        int start = pendingReplyStart;
        string prefix = pendingReplyPrefix, letter = pendingReplyLetter;
        pendingReplyPrefix = pendingReplyLetter = null;
        lastReplySmoke = now;
        if (start != replyWordStart)
        {
            CommitWord();
            replyWordStart = start;
            replyPrefix = "";
        }
        ShowWord(prefix, true);
        string before = prefix.EndsWith(letter, StringComparison.Ordinal)
            ? prefix.Substring(0, prefix.Length - letter.Length) : replyPrefix;
        DepositTypedVersion(before, letter, "");
        replyPrefix = prefix;
    }

    void HoldPigment()
    {
        retainedPigment = false;
        smoke.HoldSmokeDye = true;
        nextReplyPush = 0;
        nextLine = 0;
        ShowWord("");
    }

    internal void KeepReplyPigment()
    {
        ClearReplySmoke();
        smoke.MinimumInkFrameMilliseconds = 0;
        FadeReadingBlur(false);
        Phase=0; ApplyColors();
        retainedPigment = true;
        smoke.HoldSmokeDye = true;
        smoke.ReadingFlow = false;
        smoke.CoastSmoke = true;
        SetThinkingPalette(false);
        SetReplyPalette(false);
    }

    internal void BeginDraftInput()
    {
        draftActivity.Restart();
        if (retainedPigment)
            ResumePigmentFade();
    }

    internal void ResumePigmentFade()
    {
        retainedPigment = false;
        smoke.ReadingFlow = false;
        smoke.CoastSmoke = false;
        SetThinkingPalette(false);
        if (!smoke.HoldSmokeDye)
            return;
        smoke.HoldSmokeDye = false;
        smoke.Wake();
    }

    void SetReplyPalette(bool enabled)
    {
        replyPalette=enabled;
        replyTint.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(replyTint.Opacity,
            enabled ? Colors.Strength(2) : 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = ReboundEase });
    }

    void SetThinkingPalette(bool enabled, bool force = false)
    {
        if (enabled == thinkingPalette && !force)
            return;
        thinkingPalette = enabled;
        var fade = new DoubleAnimation(thinkingTint.Opacity, enabled ? Colors.Strength(1) : 0, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = ReboundEase
        };
        thinkingTint.BeginAnimation(Brush.OpacityProperty, fade);
        thinkingPaper.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(thinkingPaper.Opacity,
            enabled ? 1 : 0, fade.Duration) { EasingFunction = ReboundEase });
    }

    internal void DissolveFlow(BitmapSource letters,double overlayHeight,double x,double y,double velocity,double release)
    {
        if(!smoke.HoldSmokeDye || panel.Height<16)return;
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
            drawing.DrawImage(letters,new Rect(0,0,letters.PixelWidth,letters.PixelHeight*overlayHeight/panel.Height));
        var mask=new RenderTargetBitmap(letters.PixelWidth,letters.PixelHeight,96,96,PixelFormats.Pbgra32);
        mask.Render(visual);
        mask.Freeze();
        smoke.AbsorbFlow(mask,x,y*overlayHeight/panel.Height,velocity,release);
    }

    internal void DriveReplySmoke(double now, KineticReader reader, double overlayHeight)
    {
        if (!smoke.HoldSmokeDye || now < nextReplyPush || panel.Height < 16 || overlayHeight < 16)
            return;
        nextReplyPush = now + .075;
        reader.SampleLineWakes(lineWakes);
        if (lineWakes.Count == 0)
            return;
        // Interleave rows to bound GPU work on tall windows. Each stir is a local pair
        // of opposite rotations. The sign and strength follow the actual lettering.
        int applied = 0;
        double rowHeight = reader.RowStep / reader.Height * overlayHeight / panel.Height;
        for (int visited = 0; visited < lineWakes.Count && applied < 3; ++visited)
        {
            nextLine %= lineWakes.Count;
            var wake = lineWakes[nextLine++];
            if (wake.Priority == 0)
                continue;
            double speed = Math.Abs(wake.Speed);
            double radius = Math.Max(.016, Math.Min(.065, rowHeight * (.26 + .16 * speed / (.27 + speed))));
            double strength = Math.Sign(wake.Speed) * 2.4 * speed / (.32 + speed);
            smoke.StirLine(wake.X, wake.Y * overlayHeight / panel.Height, radius, wake.Direction, strength);
            ++applied;
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        panel = new Rect(RenderSize);
        if (panel.Width < 16 || panel.Height < 16)
            return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if(Phase==0 && !smoke.HoldSmokeDye && draftActivity.Elapsed.TotalSeconds>=.65)
            KeepReplyPigment();
        DrawBackground(drawing);
        smoke.SetViewport(new Size(panel.Width * dpi.DpiScaleX, panel.Height * dpi.DpiScaleY));
        smoke.SeedAmbient();
        if (!smoke.HoldSmokeDye && depositedWord != word)
            Deposit(word);
        if (!smoke.HoldSmokeDye && entrancePending)
        {
            entrancePending = false;
            Geometry glyph = BuildWord(word);
            if (glyph != null)
            {
                lastPopScale = new Point(.82, .62);
                smoke.StartInkEntrance(glyph, panel.Size, ReadPopScale);
            }
        }
        smoke.Draw(drawing, panel);
        if (previewPhase.HasValue) drawing.DrawRectangle(previewTint,null,panel);
        else
        {
            drawing.DrawRectangle(typingTint,null,panel);
            drawing.DrawRectangle(thinkingTint, null, panel);
            drawing.DrawRectangle(replyTint, null, panel);
        }
        if (entranceClock.IsRunning && entranceClock.ElapsedMilliseconds < 165)
            InvalidateVisual();
        if (error.Length != 0)
        {
            var face = new Typeface("Consolas");
            var text = new FormattedText(error, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, 16, Brushes.WhiteSmoke, dpi.PixelsPerDip) { MaxTextWidth = Math.Max(80, panel.Width * .70) };
            var box = new Rect((panel.Width - text.Width) / 2 - 14, panel.Height - text.Height - 39,
                text.Width + 28, text.Height + 20);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 46, 71, 96)), null, box, 8, 8);
            drawing.DrawText(text, new Point(box.X + 14, box.Y + 10));
        }
    }

    void Deposit(string value)
    {
        depositedWord = value;
        Geometry glyph = BuildWord(value);
        if (glyph != null)
        {
            smoke.DepositWordVersion(glyph, panel.Size);
        }
    }

    string cachedGlyphWord;
    Geometry cachedGlyph;
    Size cachedGlyphPanel;
    double cachedGlyphDpi;
    int cachedGlyphRevision;
    Geometry BuildWord(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || panel.Width <= 0 || panel.Height <= 0)
            return null;
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if(cachedGlyph!=null&&cachedGlyphWord==value&&cachedGlyphPanel==panel.Size&&
            cachedGlyphDpi==dpi&&cachedGlyphRevision==ViewTuning.Revision)return cachedGlyph;
        var face = ChatTypography.SmokeFace;
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            face, 100, Brushes.White, dpi);
        var outline = text.BuildGeometry(new Point());
        Rect ink = outline.Bounds;
        if (ink.IsEmpty || ink.Width <= 0 || ink.Height <= 0)
            return null;

        // Same sizing and baseline as CodexCraft's original Smoke display.
        double widthScale = (panel.Width - 2 * Math.Min(104, panel.Width * .08)) / ink.Width;
        double heightScale = (panel.Height - 2 * Math.Min(64, panel.Height * .12)) / ink.Height;
        double size = ViewTuning.Get("smoke.wordsize");
        double baseScale = panel.Height * .32 / 100 * size;
        bool constant = ViewTuning.Flag(WordViewMode.SumiInk, "constant");
        double scale = constant
            ? Math.Min(widthScale, Math.Min(baseScale, heightScale))
            : Math.Min(widthScale, heightScale) / 1.10 * Math.Min(1, size);
        double x = (panel.Width - ink.Width * scale) / 2 - ink.X * scale;
        double y = constant
            ? panel.Height / 2 + 50 * face.CapsHeight * baseScale - text.Baseline * scale
            : (panel.Height - ink.Height * scale) / 2 - ink.Y * scale;
        var glyph = new GeometryGroup { Transform = new MatrixTransform(scale, 0, 0, scale, x, y) };
        glyph.Children.Add(outline);
        glyph.Freeze();
        cachedGlyphWord=value;cachedGlyphPanel=panel.Size;cachedGlyphDpi=dpi;
        cachedGlyphRevision=ViewTuning.Revision;cachedGlyph=glyph;
        return glyph;
    }

    public void Dispose() { smoke.Close(); }
}
