using System;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

// Present Flow's font raster in the same WPF scene as the GPU smoke.
sealed class FlowSmokeOverlay : FrameworkElement
{
    readonly WordFlowView flow;
    readonly CloudSmokeView smoke;
    readonly Drawing.Color readInk, unreadInk;
    WriteableBitmap image;
    Drawing.Rectangle uploadBounds;
    Drawing.Size canvasSize;
    bool showText = true;

    internal FlowSmokeOverlay(WordFlowView flow, Action wake, CloudSmokeView smoke)
    {
        this.flow = flow;
        this.smoke = smoke;
        flow.Reader.DissolvingLine = true;
        flow.Reader.SeparateSmoke = true;
        Color paper = CloudSmokeView.PaperColor;
        readInk = Shade(paper, .80);
        unreadInk = Shade(paper, 1.25);
        SizeChanged += (sender, args) => { UpdateFrame(); wake(); };
        MouseLeftButtonDown += (sender, args) =>
        {
            var point = args.GetPosition(this);
            var dpi = VisualTreeHelper.GetDpi(this);
            flow.FocusAtPoint(new Drawing.Point((int)(point.X * dpi.DpiScaleX), (int)(point.Y * dpi.DpiScaleY)));
            wake();
            args.Handled = true;
        };
        MouseWheel += (sender, args) =>
        {
            flow.ApplyWheel(args.Delta, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            wake();
            args.Handled = true;
        };
    }

    static Drawing.Color Shade(Color paper, double scale)
    {
        return Drawing.Color.FromArgb(ShadeChannel(paper.R, scale), ShadeChannel(paper.G, scale),
            ShadeChannel(paper.B, scale));
    }

    static int ShadeChannel(byte channel, double scale)
    {
        return Math.Min(255, (int)Math.Round(channel * scale));
    }

    internal void UpdateFrame()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width < 16 || height < 16)
            return;
        var size = new Drawing.Size(width, height);
        if (flow.Child.Size != size)
            flow.Child.Size = size;
        smoke.SetReadingBlurHeight(flow.Reader.FocusScreenBounds.Height/Math.Max(1,height)*.65+.02);
        var frame = flow.Reader.RenderOverlay(readInk, unreadInk);
        if (frame == null)
            return;
        var nextBounds = flow.Reader.OverlayUploadBounds;
        // Grow the strip as new glyphs arrive; do not reallocate as each letter changes size.
        if (canvasSize == frame.Size && !uploadBounds.IsEmpty)
            nextBounds = Drawing.Rectangle.Union(uploadBounds, nextBounds);
        canvasSize = frame.Size;
        uploadBounds = nextBounds;
        if (image == null || image.PixelWidth != uploadBounds.Width || image.PixelHeight != uploadBounds.Height)
        {
            image = new WriteableBitmap(uploadBounds.Width, uploadBounds.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32, null);
        }
        var data = frame.LockBits(new Drawing.Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            image.WritePixels(new Int32Rect(0, 0, uploadBounds.Width, uploadBounds.Height),
                IntPtr.Add(data.Scan0, uploadBounds.Top * data.Stride),
                data.Stride * uploadBounds.Height, data.Stride);
        }
        finally { frame.UnlockBits(data); }
        InvalidateVisual();
    }

    internal void ShowText() { showText = true; InvalidateVisual(); }
    internal void HideText() { showText = false; InvalidateVisual(); }

    internal void ClearFrame()
    {
        image = null;
        uploadBounds = Drawing.Rectangle.Empty;
        canvasSize = Drawing.Size.Empty;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize);
        drawing.DrawRectangle(Brushes.Transparent, null, bounds);
        if (image != null)
        {
            if (showText)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                drawing.DrawImage(image, new Rect(0, uploadBounds.Top/dpi.DpiScaleY,
                    uploadBounds.Width/dpi.DpiScaleX, uploadBounds.Height/dpi.DpiScaleY));
            }
        }
    }
}
