using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

// Resize the native window in monitor pixels: no document/camera/content transforms.
sealed partial class WordCraftEditor
{
    [StructLayout(LayoutKind.Sequential)]struct ZoomRect {internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",SetLastError=true)]static extern bool GetWindowRect(IntPtr window,out ZoomRect rect);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);

    internal static Drawing.Rectangle ZoomBounds(Drawing.Rectangle monitor,double fraction)
    {
        if(Double.IsNaN(fraction)||Double.IsInfinity(fraction))fraction=1;
        fraction=Math.Max(.25,Math.Min(1,fraction));
        int width=Math.Max(1,(int)Math.Round(monitor.Width*fraction)),height=Math.Max(1,(int)Math.Round(monitor.Height*fraction));
        return new Drawing.Rectangle(monitor.Left+(monitor.Width-width)/2,monitor.Top+(monitor.Height-height)/2,width,height);
    }
    internal Drawing.Rectangle WindowPixelBounds
    {
        get{ZoomRect rect;var handle=new WindowInteropHelper(this).EnsureHandle();if(!GetWindowRect(handle,out rect))throw new Win32Exception(Marshal.GetLastWin32Error());return Drawing.Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);}
    }
    internal void SetWindowZoom(double fraction)
    {
        var handle=new WindowInteropHelper(this).EnsureHandle();
        var bounds=ZoomBounds(Forms.Screen.FromHandle(handle).Bounds,fraction);
        WindowState=WindowState.Normal;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        MinWidth=1;MinHeight=1;
        // Native physical pixels avoid DIP rounding/origin errors on mixed-DPI monitors.
        if(!SetWindowPos(handle,IntPtr.Zero,bounds.X,bounds.Y,bounds.Width,bounds.Height,0x14))throw new Win32Exception(Marshal.GetLastWin32Error());
        fullscreen=Double.IsNaN(fraction)||Double.IsInfinity(fraction)||fraction>=1;
    }
    internal bool HandleWindowZoomWheel(int delta,ModifierKeys modifiers)
    {
        if(delta==0||modifiers!=ModifierKeys.None||settings!=null||volumeDrag||scrubbing||WindowState==WindowState.Minimized)return false;
        var handle=new WindowInteropHelper(this).EnsureHandle();
        var monitor=Forms.Screen.FromHandle(handle).Bounds;var current=WindowPixelBounds;
        double fraction=fullscreen||WindowState==WindowState.Maximized?1:Math.Min((double)current.Width/monitor.Width,(double)current.Height/monitor.Height);
        SetWindowZoom(Math.Max(.25,Math.Min(1,fraction+delta/120.0*.05)));
        return true;
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if(!e.Handled&&HandleWindowZoomWheel(e.Delta,Keyboard.Modifiers))e.Handled=true;
    }
}
