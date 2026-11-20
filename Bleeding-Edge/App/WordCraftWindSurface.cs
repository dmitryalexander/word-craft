using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

sealed class WordCraftWindSurface : Viewport3D,IDisposable
{
    readonly TextBox editor;
    readonly WordCraftCanvasMaterial material;
    readonly MeshGeometry3D mesh=new MeshGeometry3D();
    readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Render) {Interval=TimeSpan.FromMilliseconds(33)};
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly double[] displayed=new double[WordCraftClothSimulation.Count*3],normals=new double[WordCraftClothSimulation.Count*3];
    WordCraftClothSimulation cloth;
    double wind=2,turbulence=.83,tautness=.17,gravity=.81,lastTick,accumulator;
    bool shown,suspended,paused,dirty;
    internal string FilePath=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(WordCraftWindSurface).Assembly.Location),"../Motion-Settings.json"));
    internal string Error {get;private set;}
    internal bool Running {get{return timer.IsEnabled;}}
    internal MeshGeometry3D SurfaceMesh {get{return mesh;}}
    internal WordCraftCanvasMaterial MaterialRoot {get{return material;}}
    internal double SimulationTime {get{return cloth==null?0:cloth.Time;}}
    internal double PinError {get{return cloth==null?0:cloth.PinError;}}
    internal double MaxDepth {get{return cloth==null?0:cloth.MaxDepth;}}
    internal double Wind {get{return wind;}set{wind=Clamp(value,0,2);Changed();}}
    internal double Turbulence {get{return turbulence;}set{turbulence=Clamp(value,0,1);Changed();}}
    internal double Tautness {get{return tautness;}set{tautness=Clamp(value,0,1);Changed();}}
    internal double Gravity {get{return gravity;}set{gravity=Clamp(value,0,1);Changed();}}
    internal bool KeepLinesLevel {get;set;}
    internal bool ShowMesh {get{return material.ShowMesh;}set{material.ShowMesh=value;Changed();}}
    internal bool Paused {get{return paused;}set{paused=value;RefreshTimer();}}
    internal double TextureStrength {get{return material.TextureStrength;}set{material.TextureStrength=Clamp(value,0,1);Changed();}}
    internal double WeaveSize {get{return material.WeaveSize;}set{material.WeaveSize=Clamp(value,1,5);Changed();}}
    internal double TextOpacity {get{return material.TextOpacity;}set{material.TextOpacity=Clamp(value,0,1);Changed();}}
    internal double Fraying {get{return material.Fraying;}set{material.Fraying=Clamp(value,0,1);Changed();}}
    internal double FiberLength {get{return material.FiberLength;}set{material.FiberLength=Clamp(value,1,16);Changed();}}
    static double Clamp(double x,double min,double max){return Double.IsNaN(x)||Double.IsInfinity(x)?min:Math.Max(min,Math.Min(max,x));}
    internal WordCraftWindSurface(TextBox text,string filePath=null)
    {
        editor=text;ClipToBounds=true;Opacity=0;material=new WordCraftCanvasMaterial(editor);
        if(filePath!=null)FilePath=Path.GetFullPath(filePath);
        try{
            if(File.Exists(FilePath)){
                var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(FilePath));
                if(d!=null&&d.ContainsKey("clothVersion")&&Convert.ToInt32(d["clothVersion"])==1){
                    wind=Read(d,"wind",wind,0,2);turbulence=Read(d,"turbulence",turbulence,0,1);tautness=Read(d,"tautness",tautness,0,1);gravity=Read(d,"gravity",gravity,0,1);
                    material.TextureStrength=Read(d,"textureStrength",1,0,1);material.WeaveSize=Read(d,"weaveSize",2.7,1,5);material.TextOpacity=Read(d,"opacity",.66,0,1);material.Fraying=Read(d,"fraying",.71,0,1);material.FiberLength=Read(d,"fiberLength",6.5,1,16);
                    KeepLinesLevel=d.ContainsKey("levelLines")&&Convert.ToBoolean(d["levelLines"]);material.ShowMesh=d.ContainsKey("showMesh")&&Convert.ToBoolean(d["showMesh"]);
                }else dirty=true; // Earlier formula controls have different meanings; use chosen PBD preset.
            }
        }catch(Exception e){Error=e.Message;}
        var uv=new PointCollection();var triangles=new Int32Collection();int columns=WordCraftClothSimulation.Columns,rows=WordCraftClothSimulation.Rows;
        for(int j=0;j<=rows;j++)for(int i=0;i<=columns;i++)uv.Add(new Point((double)i/columns,1-(double)j/rows));
        for(int j=0;j<rows;j++)for(int i=0;i<columns;i++){int a=j*(columns+1)+i,b=a+1,c=a+columns+1,d=c+1;foreach(int index in new[]{a,b,c,b,d,c})triangles.Add(index);}
        uv.Freeze();triangles.Freeze();mesh.TextureCoordinates=uv;mesh.TriangleIndices=triangles;
        var hostMaterial=new DiffuseMaterial(Brushes.White);Viewport2DVisual3D.SetIsVisualHostMaterial(hostMaterial,true);
        Children.Add(new Viewport2DVisual3D {Geometry=mesh,Material=hostMaterial,Visual=material});
        var lights=new Model3DGroup();lights.Children.Add(new AmbientLight(Color.FromRgb(180,180,180)));lights.Children.Add(new DirectionalLight(Color.FromRgb(80,80,80),new Vector3D(.6,-.8,-1.1)));Children.Add(new ModelVisual3D {Content=lights});
        SizeChanged+=(s,e)=>Resize();editor.TextChanged+=(s,e)=>RefreshTimer();
        timer.Tick+=(s,e)=>{double now=clock.Elapsed.TotalSeconds;accumulator+=Math.Min(.05,Math.Max(0,now-lastTick));lastTick=now;for(int i=0;i<6&&accumulator>=1.0/120;i++){cloth.Step(wind,turbulence,tautness,gravity);accumulator-=1.0/120;}UpdateFrame(0);};
    }
    static double Read(Dictionary<string,object> d,string name,double fallback,double min,double max){return d.ContainsKey(name)?Clamp(Convert.ToDouble(d[name]),min,max):fallback;}
    void Changed(){dirty=true;UpdateFrame(0);}
    internal void SetLevelLines(bool value){KeepLinesLevel=value;Changed();}
    void Resize()
    {
        if(ActualWidth<1||ActualHeight<1)return;
        editor.Width=material.Width=ActualWidth;editor.Height=material.Height=ActualHeight;
        Camera=new OrthographicCamera(new Point3D(0,0,ActualWidth*3),new Vector3D(0,0,-1),new Vector3D(0,1,0),ActualWidth) {NearPlaneDistance=.1,FarPlaneDistance=ActualWidth*6};
        cloth=new WordCraftClothSimulation(3.4*ActualHeight/ActualWidth);accumulator=0;UpdateFrame(0);
    }
    internal void UpdateFrame(double unused)
    {
        if(cloth==null)return;double scale=ActualWidth/3.4;Array.Copy(cloth.Positions,displayed,displayed.Length);
        if(KeepLinesLevel)for(int i=1;i<displayed.Length;i+=3)displayed[i]=cloth.Rest[i];
        var points=new Point3DCollection(WordCraftClothSimulation.Count);var values=new Vector3DCollection(WordCraftClothSimulation.Count);
        for(int i=0;i<WordCraftClothSimulation.Count;i++)WordCraftClothSimulation.NormalAt(displayed,i,normals);
        for(int i=0;i<displayed.Length;i+=3){points.Add(new Point3D(displayed[i]*scale,displayed[i+1]*scale,displayed[i+2]*scale));values.Add(new Vector3D(normals[i],normals[i+1],normals[i+2]));}
        points.Freeze();values.Freeze();mesh.Positions=points;mesh.Normals=values;
    }
    internal void AdvanceSimulation(int steps){if(cloth==null)return;for(int i=0;i<Math.Max(0,Math.Min(2400,steps));i++)cloth.Step(wind,turbulence,tautness,gravity);UpdateFrame(0);}
    internal void ResetCloth(){if(cloth!=null)cloth.Reset();accumulator=0;UpdateFrame(0);}
    void RefreshTimer(){if(shown&&!suspended&&!paused&&!String.IsNullOrEmpty(editor.Text)&&cloth!=null){lastTick=clock.Elapsed.TotalSeconds;timer.Start();}else timer.Stop();}
    internal void Show(bool value,bool animate=true)
    {
        double from=Opacity;shown=value;BeginAnimation(OpacityProperty,null);Opacity=value?1:0;
        if(animate)BeginAnimation(OpacityProperty,new DoubleAnimation(from,Opacity,TimeSpan.FromMilliseconds(700)) {FillBehavior=FillBehavior.Stop});
        IsHitTestVisible=value;material.SetActive(value&&!suspended);UpdateFrame(0);RefreshTimer();
    }
    internal void SetSuspended(bool value){suspended=value;material.SetActive(shown&&!value);RefreshTimer();}
    internal bool Save()
    {
        if(!dirty)return true;
        try{
            string temp=FilePath+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new {clothVersion=1,wind,turbulence,tautness,gravity,levelLines=KeepLinesLevel,textureStrength=TextureStrength,weaveSize=WeaveSize,opacity=TextOpacity,fraying=Fraying,fiberLength=FiberLength,showMesh=ShowMesh}));
            if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);dirty=false;Error=null;return true;
        }catch(Exception e){Error=e.Message;return false;}
    }
    public void Dispose(){timer.Stop();material.Dispose();BeginAnimation(OpacityProperty,null);Children.Clear();}
}
