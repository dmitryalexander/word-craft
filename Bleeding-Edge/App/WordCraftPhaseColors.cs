using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

// WordCraft-only companion to the staged shared scene. Never edits saved palettes.
sealed partial class CloudSmokeView
{
    int appliedColorPhase=-1;
    bool readingColorExit;
    BitmapSource previousColorRamp;
    Color colorFromStart,colorFromEnd,colorToStart,colorToEnd;
    double colorFromAmount,colorToAmount,colorFromAngle,colorToAngle,colorFromRotation,colorToRotation;
    internal static readonly DependencyProperty PhaseColorProgressProperty=DependencyProperty.Register(
        "PhaseColorProgress",typeof(double),typeof(CloudSmokeView),
        new FrameworkPropertyMetadata(1.0,FrameworkPropertyMetadataOptions.AffectsRender,
            (d,e)=>((CloudSmokeView)d).ApplyPhaseColorFrame()));
    internal double PhaseColorProgress { get { return (double)GetValue(PhaseColorProgressProperty); } }
    static double Mix(double a,double b,double t){return a+(b-a)*t;}
    static Color MixColor(Color a,Color b,double t){return Color.FromRgb(
        (byte)Math.Round(Mix(a.R,b.R,t)),(byte)Math.Round(Mix(a.G,b.G,t)),(byte)Math.Round(Mix(a.B,b.B,t)));}
    internal void ResetReadingColorExit(){readingColorExit=false;}
    internal void BeginReadingColorExit()
    {
        if(readingColorExit)return;
        readingColorExit=true;
        var previous=previewPhase;previewPhase=0;ApplyColors();previewPhase=previous;
        SetReplyPalette(false);
    }
    void ApplyColors(bool force=false)
    {
        int target=previewPhase??(readingColorExit?0:Phase);
        if(target==appliedColorPhase&&!force)return;
        double progress=PhaseColorProgress;
        BitmapSource oldRamp=null;
        if(appliedColorPhase>=0&&!force)
        {
            var original=previewPhase;previewPhase=appliedColorPhase;
            oldRamp=CornerBackground(768,256);previewPhase=original;
            if(previousColorRamp!=null&&progress<1)
            {
                var visual=new DrawingVisual();
                using(var drawing=visual.RenderOpen()){
                    drawing.DrawImage(previousColorRamp,new Rect(0,0,768,256));
                    drawing.PushOpacity(progress);drawing.DrawImage(oldRamp,new Rect(0,0,768,256));drawing.Pop();
                }
                var bitmap=new RenderTargetBitmap(768,256,96,96,PixelFormats.Pbgra32);
                bitmap.Render(visual);bitmap.Freeze();oldRamp=bitmap;
            }
        }
        Color start=MixColor(colorFromStart,colorToStart,progress),end=MixColor(colorFromEnd,colorToEnd,progress);
        double amount=Mix(colorFromAmount,colorToAmount,progress),angle=Mix(colorFromAngle,colorToAngle,progress),rotation=Mix(colorFromRotation,colorToRotation,progress);
        BeginAnimation(PhaseColorProgressProperty,null);
        var savedPreview=previewPhase;previewPhase=target;ApplyBaseColors();previewPhase=savedPreview;
        appliedColorPhase=target;
        colorToStart=Colors.Get(target,"background");colorToEnd=Colors.Get(target,"corner");
        colorToAmount=Colors.Gradient(target);colorToAngle=Colors.Angle(target);colorToRotation=Colors.Rotation(target);
        colorFromStart=oldRamp==null?colorToStart:start;colorFromEnd=oldRamp==null?colorToEnd:end;
        colorFromAmount=oldRamp==null?colorToAmount:amount;colorFromAngle=oldRamp==null?colorToAngle:angle;colorFromRotation=oldRamp==null?colorToRotation:rotation;
        previousColorRamp=oldRamp;
        SetValue(PhaseColorProgressProperty,oldRamp==null?1.0:0.0);
        if(oldRamp!=null)
            BeginAnimation(PhaseColorProgressProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(700)) {
                EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut },FillBehavior=FillBehavior.HoldEnd });
        ApplyPhaseColorFrame(oldRamp==null?1:0);
    }
    void ApplyPhaseColorFrame()
    {ApplyPhaseColorFrame(PhaseColorProgress);}
    void ApplyPhaseColorFrame(double t)
    {
        if(smoke==null||appliedColorPhase<0)return;
        smoke.SetColorGradient(MixColor(colorFromStart,colorToStart,t),MixColor(colorFromEnd,colorToEnd,t),
            Mix(colorFromAmount,colorToAmount,t),Mix(colorFromAngle,colorToAngle,t),Mix(colorFromRotation,colorToRotation,t));
        InvalidateVisual();
    }
    void DrawBackground(DrawingContext drawing)
    {
        double t=PhaseColorProgress;
        if(previousColorRamp==null||t>=1){DrawBaseBackground(drawing);return;}
        double radians=(colorFromAngle+colorFromRotation*GpuSumiInk.GradientSeconds)*Math.PI/180;
        double angle=Math.Atan2(Math.Sin(radians)/panel.Height,Math.Cos(radians)/panel.Width);
        double cs=Math.Cos(angle),sn=Math.Sin(angle);
        double width=panel.Width*Math.Abs(cs)+panel.Height*Math.Abs(sn),height=panel.Width*Math.Abs(sn)+panel.Height*Math.Abs(cs);
        drawing.PushClip(new RectangleGeometry(panel));
        drawing.PushTransform(new RotateTransform(angle*180/Math.PI,panel.Width/2,panel.Height/2));
        drawing.DrawImage(previousColorRamp,new Rect((panel.Width-width)/2,(panel.Height-height)/2,width,height));
        drawing.Pop();drawing.Pop();
        drawing.PushOpacity(t);DrawBaseBackground(drawing);drawing.Pop();
    }
}
