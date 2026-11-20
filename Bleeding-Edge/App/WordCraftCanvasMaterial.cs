using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Material only; the actual TextBox remains interactive and owns source/selection/undo.
sealed class WordCraftCanvasMaterial : Grid,IDisposable
{
    readonly TextBox editor;
    readonly Image fringe=new Image {Stretch=Stretch.Fill,IsHitTestVisible=false};
    readonly MeshGuide guide=new MeshGuide();
    readonly Grid inkLayer=new Grid();
    readonly DispatcherTimer debounce=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(120)};
    bool active,fringeDirty=true;
    double strength=1,weave=2.7,opacity=.66,fraying=.71,length=6.5;
    static readonly double[] weaveTone=BuildTone();
    BitmapSource maskBitmap;
    double cachedStrength=Double.NaN;
    internal int FringeRevision {get;private set;}
    internal int MaskRevision {get;private set;}
    internal int ThreadCount {get;private set;}
    internal int FringeDrawCount {get;private set;}
    internal double TextureStrength {get{return strength;}set{strength=value;RefreshMask();}}
    internal double WeaveSize {get{return weave;}set{weave=value;RefreshMask();QueueFringe();}}
    internal double TextOpacity {get{return opacity;}set{opacity=value;inkLayer.Opacity=value;}}
    internal double Fraying {get{return fraying;}set{fraying=value;QueueFringe();}}
    internal double FiberLength {get{return length;}set{length=value;QueueFringe();}}
    internal bool ShowMesh {get{return guide.Visibility==Visibility.Visible;}set{guide.Visibility=value?Visibility.Visible:Visibility.Collapsed;}}
    internal WordCraftCanvasMaterial(TextBox text)
    {
        editor=text;inkLayer.Opacity=opacity;guide.Visibility=Visibility.Collapsed;
        var ink=new SolidColorBrush(Color.FromRgb(224,221,212));ink.Freeze();editor.Foreground=ink;
        Children.Add(guide);Children.Add(inkLayer);inkLayer.Children.Add(fringe);inkLayer.Children.Add(editor);
        fringe.SetBinding(OpacityMaskProperty,new Binding("OpacityMask") {Source=editor});
        editor.TextChanged+=TextChanged;editor.SizeChanged+=LayoutChanged;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(Scrolled));
        debounce.Tick+=(s,e)=>{debounce.Stop();UpdateNow();};RefreshMask();
    }
    void TextChanged(object sender,TextChangedEventArgs e){fringe.Source=null;QueueFringe();}
    void LayoutChanged(object sender,SizeChangedEventArgs e){QueueFringe();}
    void Scrolled(object sender,ScrollChangedEventArgs e){QueueFringe();}
    internal void SetActive(bool value){active=value;if(value)QueueFringe();else debounce.Stop();}
    internal void InvalidateTextLayout(){fringe.Source=null;QueueFringe();}
    void QueueFringe(){fringeDirty=true;if(active){debounce.Stop();debounce.Start();}}
    static double Hash(int x,int y)
    {unchecked{int n=(x+113)*374761393^(y+59)*668265263;n=(n^(int)((uint)n>>13))*1274126177;return (uint)(n^(int)((uint)n>>16))/4294967295.0;}}
    static double[] BuildTone()
    {
        const int size=512;var toneValues=new double[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++){
            int i=x>>3,j=y>>3;double fx=(x%8+.5)/8,fy=(y%8+.5)/8;bool vertical=(i+j)%2==0;
            double across=vertical?fx:fy,along=vertical?y:x,thread=vertical?Hash(i,1):Hash(j,2);
            double round=Math.Pow(Math.Max(0,Math.Cos((across-.5)*Math.PI)),.7);
            double tone=Math.Max(0,Math.Min(1,.34+.51*round+.10*thread+.055*Math.Sin(across*45+thread*8)+.025*Math.Sin(along*.83+thread*12)+.055*(Hash(x,y)-.5)));
            toneValues[y*size+x]=(.5+.5*tone)*(.7+.3*tone);
        }
        return toneValues;
    }
    void RefreshMask()
    {
        const int size=512;
        if(maskBitmap==null||cachedStrength!=strength){
            var pixels=new byte[size*size*4];
            for(int i=0;i<weaveTone.Length;i++){byte alpha=(byte)Math.Round(((1-strength)+strength*weaveTone[i])*255);int q=i*4;pixels[q]=pixels[q+1]=pixels[q+2]=pixels[q+3]=alpha;}
            maskBitmap=BitmapSource.Create(size,size,96,96,PixelFormats.Pbgra32,null,pixels,size*4);maskBitmap.Freeze();cachedStrength=strength;
        }
        var brush=new ImageBrush(maskBitmap) {TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,weave*64,weave*64),Stretch=Stretch.Fill};brush.Freeze();inkLayer.OpacityMask=brush;MaskRevision++;
    }
    internal void UpdateNow()
    {
        if(!active||!fringeDirty||editor.ActualWidth<1||editor.ActualHeight<1)return;
        if(fraying<=0){fringe.Source=null;ThreadCount=0;FringeDrawCount=0;fringeDirty=false;FringeRevision++;return;}
        double width=editor.ActualWidth,height=editor.ActualHeight,scale=Math.Min(1,Math.Min(1920/width,1200/height));int w=Math.Max(1,(int)Math.Ceiling(width*scale)),h=Math.Max(1,(int)Math.Ceiling(height*scale));
        var snapshot=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
        var capture=new DrawingVisual();Brush old=editor.OpacityMask;
        try{
            editor.OpacityMask=null;
            using(var dc=capture.RenderOpen()){dc.PushTransform(new ScaleTransform(scale,scale));dc.DrawRectangle(new VisualBrush(editor),null,new Rect(0,0,width,height));dc.Pop();}
            snapshot.Render(capture);
        }finally{editor.OpacityMask=old;}
        var source=new byte[w*h*4];snapshot.CopyPixels(source,w*4,0);
        Func<int,int,byte> alpha=(x,y)=>x<0||y<0||x>=w||y>=h?(byte)0:source[(y*w+x)*4+3];
        var drawing=new DrawingVisual();int count=0,drawCount=0;double pitch=weave*scale,reach=length*scale;
        // Batch disconnected fibers with near-identical ink/stroke into a small
        // set of paths. Dense text must not create thousands of WPF draw objects.
        var paths=new StreamGeometry[128];var contexts=new StreamGeometryContext[128];
        using(var dc=drawing.RenderOpen()){
            Action<int,int,int,int> strand=(x,y,dx,dy)=>{
                if(Hash(x+dx*19,y+dy*23)>fraying)return;
                double end=reach*(.3+.7*Hash(x+71,y+13)),bend=(Hash(x+11,y+91)-.5)*scale*1.5;
                int shade=(int)Math.Round(Hash(x+2,y+3)*15),stroke=(int)Math.Round(Hash(x+4,y+8)*7),bucket=shade*8+stroke;
                if(paths[bucket]==null){paths[bucket]=new StreamGeometry();contexts[bucket]=paths[bucket].Open();}
                var g=contexts[bucket];g.BeginFigure(new Point(x-dx,y-dy),false,false);
                g.QuadraticBezierTo(new Point(x+dx*end*.55-dy*bend,y+dy*end*.55+dx*bend),new Point(x+dx*end-dy*bend*.3,y+dy*end+dx*bend*.3),true,false);count++;
            };
            for(int y=1;y<h-1;y+=2)for(int x=1;x<w-24*scale;x+=2){
                if(alpha(x,y)<100)continue;
                bool horizontal=Math.Abs(y/pitch-Math.Round(y/pitch))*pitch<1.1,vertical=Math.Abs(x/pitch-Math.Round(x/pitch))*pitch<1.1;
                if(horizontal){if(alpha(x+2,y)<32)strand(x,y,1,0);if(alpha(x-2,y)<32)strand(x,y,-1,0);}
                if(vertical){if(alpha(x,y+2)<32)strand(x,y,0,1);if(alpha(x,y-2)<32)strand(x,y,0,-1);}
            }
            for(int bucket=0;bucket<paths.Length;bucket++)if(paths[bucket]!=null){
                contexts[bucket].Close();paths[bucket].Freeze();
                var brush=new SolidColorBrush(Color.FromArgb((byte)Math.Round(128+127*(bucket/8)/15.0),224,221,212));brush.Freeze();
                var pen=new Pen(brush,scale*(.35+.45*(bucket%8)/7.0)) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};pen.Freeze();
                dc.DrawGeometry(null,pen,paths[bucket]);drawCount++;
            }
        }
        FringeDrawCount=drawCount;
        var output=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);output.Render(drawing);output.Freeze();fringe.Source=output;ThreadCount=count;fringeDirty=false;FringeRevision++;
    }
    public void Dispose(){active=false;debounce.Stop();editor.TextChanged-=TextChanged;editor.SizeChanged-=LayoutChanged;editor.RemoveHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(Scrolled));inkLayer.Children.Clear();Children.Clear();}
    sealed class MeshGuide : FrameworkElement
    {
        internal MeshGuide(){IsHitTestVisible=false;}
        protected override void OnRender(DrawingContext dc)
        {var pen=new Pen(new SolidColorBrush(Color.FromArgb(55,190,207,205)),.7);for(int i=0;i<=24;i++)dc.DrawLine(pen,new Point(ActualWidth*i/24,0),new Point(ActualWidth*i/24,ActualHeight));for(int j=0;j<=10;j++)dc.DrawLine(pen,new Point(0,ActualHeight*j/10),new Point(ActualWidth,ActualHeight*j/10));}
    }
}
