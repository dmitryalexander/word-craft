using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

sealed partial class WordCraftEditor
{
    readonly Grid windowRoot=new Grid {Background=Brushes.Black,ClipToBounds=true};
    readonly Viewbox simulationView=new Viewbox {Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    double simulationZoom=1;
    internal double SimulationZoom {get{return simulationZoom;}}
    void InitializeSimulationZoom()
    {
        simulationView.Child=canvas;windowRoot.Children.Add(simulationView);
        windowRoot.Children.Add(feedback);windowRoot.Children.Add(help);Panel.SetZIndex(help,200);
        windowRoot.SizeChanged+=(s,e)=>ResizeSimulationView();
        Content=windowRoot;
    }
    void ResizeSimulationView()
    {
        if(windowRoot.ActualWidth<=0||windowRoot.ActualHeight<=0)return;
        // Keep the complete scene's logical layout, render state and text wrapping.
        // Only its final presentation scales; WPF maps pointer hits back into it.
        canvas.Width=windowRoot.ActualWidth;canvas.Height=windowRoot.ActualHeight;
        simulationView.Width=windowRoot.ActualWidth*simulationZoom;
        simulationView.Height=windowRoot.ActualHeight*simulationZoom;
        // Clip the final display in screen DIPs, rather than rounding the cloth itself.
        simulationView.Clip=simulationZoom<1
            ? new RectangleGeometry(new Rect(0,0,simulationView.Width,simulationView.Height),20,20)
            : null;
    }
    internal void SetSimulationZoom(double fraction)
    {
        simulationZoom=Double.IsNaN(fraction)||Double.IsInfinity(fraction)?1:Math.Max(.25,Math.Min(1,fraction));
        ResizeSimulationView();
    }
    internal bool HandleSimulationZoomWheel(int delta,ModifierKeys modifiers)
    {
        if(delta==0||modifiers!=ModifierKeys.None||settings!=null||help.IsOpen||volumeDrag||scrubbing||WindowState==WindowState.Minimized)return false;
        SetSimulationZoom(simulationZoom+delta/120.0*.05);
        return true;
    }
    internal bool HandleMeshFontWheel(int delta,ModifierKeys modifiers)
    {
        if(delta==0||modifiers!=ModifierKeys.Control||settings!=null||help.IsOpen||volumeDrag||scrubbing||WindowState==WindowState.Minimized)return false;
        // Reserve Ctrl+wheel in all document modes so the reader never changes size.
        if(!reading&&editor.IsHitTestVisible){
            double size=Math.Max(8,Math.Min(160,editor.FontSize*Math.Pow(1.1,delta/120.0)));
            if(size!=editor.FontSize){
                editor.FontSize=size;
                windSurface.MaterialRoot.InvalidateTextLayout();
            }
        }
        return true;
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if(!e.Handled&&(HandleMeshFontWheel(e.Delta,Keyboard.Modifiers)||HandleSimulationZoomWheel(e.Delta,Keyboard.Modifiers)))e.Handled=true;
    }
}
