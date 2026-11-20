using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Web.Script.Serialization;

sealed class ChatColorProfiles
{
    internal static readonly string[] Keys = {
        "background", "corner", "tint", "text",
        "smoke.ink", "smoke.c2", "smoke.c3", "smoke.c4", "smoke.c5",
        "smoke.d1", "smoke.d2", "smoke.d3", "smoke.d4", "smoke.d5"
    };
    internal static readonly string[] Labels = {
        "Gradient start / background", "Gradient end", "Phase tint", "Reply text",
        "Smoke light 1", "Smoke light 2", "Smoke light 3", "Smoke light 4", "Smoke light 5",
        "Smoke dark 1", "Smoke dark 2", "Smoke dark 3", "Smoke dark 4", "Smoke dark 5"
    };
    internal string FilePath = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(ChatColorProfiles).Assembly.Location), "../Color-Tuning.json"));
    readonly Dictionary<string,string>[] defaults = new Dictionary<string,string>[3];
    Dictionary<string,string>[] data = new Dictionary<string,string>[3];
    internal event Action Changed;
    internal string Error = "";
    bool saveBlocked;

    internal ChatColorProfiles(string filePath = null)
    {
        if(filePath!=null)FilePath=filePath;
        Color bg = ViewTuning.ColorOf("smoke.paper");
        Color corner = Color.FromRgb((byte)(bg.R*.79),(byte)(bg.G*.81),(byte)(bg.B*.85));
        for(int phase=0;phase<3;phase++)
        {
            var values = new Dictionary<string,string>();
            values["background"]=Hex(bg); values["corner"]=Hex(corner);
            values["tint"]=phase==1 ? "#7B4C6B" : phase==2 ? "#48B1F5" : Hex(bg);
            values["text"]="#FFFFFF";
            values["strength"]=phase==1 ? "34" : phase==2 ? "62" : "0";
            values["gradient"]="25";values["angle"]="45";values["rotation"]="1.5";
            for(int i=4;i<Keys.Length;i++) values[Keys[i]]=Hex(ViewTuning.ColorOf(Keys[i]));
            defaults[phase]=values;
            data[phase]=new Dictionary<string,string>(values);
        }
        if(!File.Exists(FilePath))return;
        try
        {
            var saved = new JavaScriptSerializer().Deserialize<Dictionary<string,string>[]>(File.ReadAllText(FilePath));
            if(saved==null || saved.Length!=3)throw new FormatException("Three phase palettes are required.");
            for(int p=0;p<3;p++)
            {
                foreach(string key in Keys) { Color c; if(!saved[p].ContainsKey(key)||!TryColor(saved[p][key],out c))throw new FormatException("Invalid color."); }
                if(!saved[p].ContainsKey("gradient"))saved[p]["gradient"]="25";
                if(!saved[p].ContainsKey("angle"))saved[p]["angle"]="45";
                if(!saved[p].ContainsKey("rotation"))saved[p]["rotation"]="1.5";
                foreach(string scalar in new[]{"gradient","angle","rotation"})
                {
                    double value;
                    if(!Double.TryParse(saved[p][scalar],NumberStyles.Float,CultureInfo.InvariantCulture,out value) ||
                        Double.IsNaN(value) || value<0 || value>(scalar=="angle"?360:scalar=="rotation"?6:100))throw new FormatException("Invalid gradient setting.");
                }
                double n; if(!saved[p].ContainsKey("strength") || !Double.TryParse(saved[p]["strength"],NumberStyles.Float,CultureInfo.InvariantCulture,out n) || Double.IsNaN(n) || n<0 || n>100)throw new FormatException("Invalid tint strength.");
            }
            data=saved;
        }
        catch(Exception e)
        {
            Error="Could not load saved colors: "+e.Message;
            // Retain an unreadable preference file before a later explicit save.
            try { File.Copy(FilePath,FilePath+"."+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".recovery"); }
            catch(Exception copy) { saveBlocked=true;Error += " Recovery copy failed: "+copy.Message; }
        }
    }
    internal static string Hex(Color c) { return String.Format("#{0:X2}{1:X2}{2:X2}",c.R,c.G,c.B); }
    internal static bool TryColor(string text,out Color color)
    {
        color=Colors.Black;
        if(text==null || text.Length!=7 || text[0]!='#')return false;
        uint n;if(!UInt32.TryParse(text.Substring(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out n))return false;
        color=Color.FromRgb((byte)(n>>16),(byte)(n>>8),(byte)n);return true;
    }
    internal Color Get(int phase,string key) { Color c;TryColor(data[phase][key],out c);return c; }
    internal double Strength(int phase) { return Double.Parse(data[phase]["strength"],CultureInfo.InvariantCulture)/100; }
    internal void Set(int phase,string key,Color value) { data[phase][key]=Hex(value); if(Changed!=null)Changed(); }
    internal void SetStrength(int phase,double value) { data[phase]["strength"]=Math.Max(0,Math.Min(100,value)).ToString("0.##",CultureInfo.InvariantCulture);if(Changed!=null)Changed(); }
    internal double Gradient(int phase) { return Double.Parse(data[phase]["gradient"],CultureInfo.InvariantCulture)/100; }
    internal double Angle(int phase) { return Double.Parse(data[phase]["angle"],CultureInfo.InvariantCulture); }
    internal void SetGradient(int phase,double value) { data[phase]["gradient"]=Math.Max(0,Math.Min(100,value)).ToString("0.##",CultureInfo.InvariantCulture);if(Changed!=null)Changed(); }
    internal void SetAngle(int phase,double value) { data[phase]["angle"]=Math.Max(0,Math.Min(360,value)).ToString("0.##",CultureInfo.InvariantCulture);if(Changed!=null)Changed(); }
    internal double Rotation(int phase) { return Double.Parse(data[phase]["rotation"],CultureInfo.InvariantCulture); }
    internal void SetRotation(int phase,double value) { data[phase]["rotation"]=Math.Max(0,Math.Min(6,value)).ToString("0.##",CultureInfo.InvariantCulture);if(Changed!=null)Changed(); }
    internal void CopyGradient(int phase)
    {
        foreach(var palette in data)foreach(string key in new[]{"background","corner","gradient","angle","rotation"})palette[key]=data[phase][key];
        if(Changed!=null)Changed();
    }
    internal void Reset(int phase) { data[phase]=new Dictionary<string,string>(defaults[phase]);if(Changed!=null)Changed(); }
    internal bool Save()
    {
        if(saveBlocked)return false;
        try
        {
            string temp=FilePath+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(data));
            if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);
            Error="";return true;
        }
        catch(Exception e){Error="Colors could not be saved: "+e.Message;return false;}
    }
}

sealed class ChatColorPanel : Border
{
    readonly ChatColorProfiles profiles;
    readonly Action<int?> preview;
    readonly Action close;
    readonly ComboBox target = new ComboBox { Margin=new Thickness(0,10,0,12),Foreground=Brushes.Black };
    readonly TextBox hex = new TextBox { FontFamily=new FontFamily("Consolas"),FontSize=18,Margin=new Thickness(0,8,0,8) };
    readonly Border swatch = new Border { Height=24,CornerRadius=new CornerRadius(7) };
    readonly ChatVisualColorPicker picker = new ChatVisualColorPicker();
    readonly Slider gradient = new Slider { Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true };
    readonly Slider angle = new Slider { Minimum=0,Maximum=360,TickFrequency=1,IsSnapToTickEnabled=true };
    readonly Slider rotation = new Slider { Minimum=0,Maximum=6,TickFrequency=.1,IsSnapToTickEnabled=true };
    readonly TextBlock gradientValue = new TextBlock(), angleValue = new TextBlock(), rotationValue=new TextBlock();
    readonly Slider[] channels = new Slider[3];
    readonly Slider strength = new Slider { Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true };
    readonly TextBlock strengthValue = new TextBlock();
    readonly TextBlock status = new TextBlock { TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Salmon,Margin=new Thickness(0,8,0,0) };
    readonly Button[] phases = new Button[3];
    bool loading;
    int phase;
    internal int Phase { get { return phase; } }
    internal ChatColorPanel(ChatColorProfiles profiles,int initialPhase,Action<int?> preview,Action close)
    {
        this.profiles=profiles;this.phase=initialPhase;this.preview=preview;this.close=close;
        Width=350;HorizontalAlignment=HorizontalAlignment.Right;VerticalAlignment=VerticalAlignment.Stretch;
        Margin=new Thickness(18);Padding=new Thickness(20);CornerRadius=new CornerRadius(15);
        Background=new SolidColorBrush(Color.FromArgb(249,18,25,37));
        BorderBrush=new SolidColorBrush(Color.FromRgb(73,94,118));BorderThickness=new Thickness(1);
        var scroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        var body=new StackPanel();scroll.Content=body;Child=scroll;
        body.Children.Add(new TextBlock { Text="Colors",FontSize=23,FontWeight=FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text="Caps Lock / Esc saves & closes",Opacity=.65,Margin=new Thickness(0,4,0,14) });
        var tabs=new Grid();for(int i=0;i<3;i++)tabs.ColumnDefinitions.Add(new ColumnDefinition());
        string[] names={"Typing","Thinking","Reply"};
        for(int i=0;i<3;i++)
        {
            int p=i;var b=Button(names[i]);phases[i]=b;Grid.SetColumn(b,i);tabs.Children.Add(b);
            b.Click+=(s,e)=>SelectPhase(p);
        }
        body.Children.Add(tabs);
        foreach(string label in ChatColorProfiles.Labels)target.Items.Add(label);
        target.SelectedIndex=0;body.Children.Add(target);body.Children.Add(swatch);
        picker.Margin=new Thickness(0,10,0,6);body.Children.Add(picker);
        picker.Changed+=c=>{
            if(loading)return;
            profiles.Set(phase,Key,c);
            loading=true;hex.Text=ChatColorProfiles.Hex(c);LoadChannels(c);loading=false;
        };
        var precise=new Expander { Header="RGB / hex",Foreground=Brushes.WhiteSmoke,IsExpanded=false,Margin=new Thickness(0,5,0,8) };
        var preciseBody=new StackPanel();precise.Content=preciseBody;body.Children.Add(precise);
        preciseBody.Children.Add(hex);
        for(int i=0;i<3;i++)
        {
            var row=new DockPanel { Margin=new Thickness(0,6,0,6) };
            var label=new TextBlock { Text=new[]{"R","G","B"}[i],Width=24,VerticalAlignment=VerticalAlignment.Center };
            row.Children.Add(label);
            var slider=new Slider { Minimum=0,Maximum=255,TickFrequency=1,IsSnapToTickEnabled=true };
            channels[i]=slider;row.Children.Add(slider);preciseBody.Children.Add(row);
            slider.ValueChanged+=(s,e)=>FromSliders();
        }
        body.Children.Add(new TextBlock { Text="Phase tint strength",Margin=new Thickness(0,18,0,5) });
        body.Children.Add(strength);body.Children.Add(strengthValue);
        strength.ValueChanged+=(s,e)=>{if(!loading){profiles.SetStrength(phase,strength.Value);strengthValue.Text=strength.Value.ToString("0")+"%";}};
        body.Children.Add(new TextBlock { Text="Smoke color gradient",Margin=new Thickness(0,14,0,4) });
        body.Children.Add(gradient);body.Children.Add(gradientValue);
        gradient.ValueChanged+=(a,e)=>{if(!loading){profiles.SetGradient(phase,gradient.Value);gradientValue.Text=gradient.Value.ToString("0")+"%";}};
        body.Children.Add(new TextBlock { Text="Gradient direction",Margin=new Thickness(0,10,0,4) });
        body.Children.Add(angle);body.Children.Add(angleValue);
        angle.ValueChanged+=(a,e)=>{if(!loading){profiles.SetAngle(phase,angle.Value);angleValue.Text=angle.Value.ToString("0")+"°";}};
        body.Children.Add(new TextBlock { Text="Gradient rotation speed",Margin=new Thickness(0,10,0,4) });
        body.Children.Add(rotation);body.Children.Add(rotationValue);
        rotation.ValueChanged+=(a,e)=>{if(!loading){profiles.SetRotation(phase,rotation.Value);rotationValue.Text=rotation.Value.ToString("0.0")+"°/s · 0 stops rotation";}};
        var copy=Button("Copy gradient to all modes");
        copy.Margin=new Thickness(0,10,0,0);copy.Click+=(a,e)=>profiles.CopyGradient(phase);body.Children.Add(copy);
        body.Children.Add(new TextBlock { Text="Background and tint preview live. Smoke palette colors affect new pigment.",TextWrapping=TextWrapping.Wrap,Opacity=.65,Margin=new Thickness(0,16,0,14) });
        var reset=Button("Reset this phase");reset.Click+=(s,e)=>{profiles.Reset(phase);LoadColor();};body.Children.Add(reset);
        var done=Button("Save & close");done.Margin=new Thickness(0,8,0,0);done.Click+=(s,e)=>close();body.Children.Add(done);body.Children.Add(status);
        target.SelectionChanged+=(s,e)=>LoadColor();
        hex.TextChanged+=(s,e)=>{
            if(loading)return;
            Color c;if(!ChatColorProfiles.TryColor(hex.Text,out c)){hex.BorderBrush=Brushes.Salmon;return;}
            hex.ClearValue(Control.BorderBrushProperty);profiles.Set(phase,Key,c);LoadChannels(c);
        };
        System.Windows.Documents.TextElement.SetForeground(this,Brushes.WhiteSmoke);
        status.Text=profiles.Error;
        SelectPhase(initialPhase);
    }
    static Button Button(string text) { return new Button { Content=text,Padding=new Thickness(7),Margin=new Thickness(1),Foreground=Brushes.WhiteSmoke,Background=new SolidColorBrush(Color.FromRgb(37,50,68)) }; }
    string Key { get { return ChatColorProfiles.Keys[Math.Max(0,target.SelectedIndex)]; } }
    void SelectPhase(int value)
    {
        phase=value;
        for(int i=0;i<3;i++)phases[i].Background=new SolidColorBrush(i==phase?Color.FromRgb(65,90,119):Color.FromRgb(37,50,68));
        preview(phase);LoadColor();
    }
    void LoadChannels(Color c)
    {
        bool old=loading;loading=true;
        channels[0].Value=c.R;channels[1].Value=c.G;channels[2].Value=c.B;
        swatch.Background=new SolidColorBrush(c);picker.SetColor(c);loading=old;
    }
    void LoadColor()
    {
        loading=true;Color c=profiles.Get(phase,Key);hex.Text=ChatColorProfiles.Hex(c);LoadChannels(c);
        strength.Value=profiles.Strength(phase)*100;strengthValue.Text=strength.Value.ToString("0")+"%";
        gradient.Value=profiles.Gradient(phase)*100;gradientValue.Text=gradient.Value.ToString("0")+"%";
        angle.Value=profiles.Angle(phase);angleValue.Text=angle.Value.ToString("0")+"°";
        rotation.Value=profiles.Rotation(phase);rotationValue.Text=rotation.Value.ToString("0.0")+"°/s · 0 stops rotation";loading=false;
    }
    void FromSliders()
    {
        if(loading)return;
        Color c=Color.FromRgb((byte)channels[0].Value,(byte)channels[1].Value,(byte)channels[2].Value);
        profiles.Set(phase,Key,c);loading=true;hex.Text=ChatColorProfiles.Hex(c);LoadChannels(c);loading=false;
    }
    internal bool Save() { bool ok=profiles.Save();status.Text=profiles.Error;return ok; }
    internal void FocusEditor() { target.Focus(); }
}

sealed class ChatVisualColorPicker : Grid
{
    readonly ChatColorSurface square = new ChatColorSurface();
    readonly ChatColorSurface hue = new ChatColorSurface { HueOnly=true };
    double h,s=1,v=1;
    internal event Action<Color> Changed;
    internal ChatVisualColorPicker()
    {
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(142) });
        RowDefinitions.Add(new RowDefinition { Height=new GridLength(28) });
        square.ToolTip="Drag to choose saturation and brightness";
        hue.ToolTip="Drag to choose hue";
        hue.Margin=new Thickness(0,7,0,0);
        Children.Add(square);SetRow(hue,1);Children.Add(hue);
        square.Picked+=(x,y)=>{s=x;v=y;Publish();};
        hue.Picked+=(x,y)=>{h=x;Publish();};
        Sync();
    }
    void Sync()
    {
        square.H=h;square.S=s;square.V=v;
        hue.H=h;hue.S=s;hue.V=v;
        square.InvalidateVisual();hue.InvalidateVisual();
    }
    void Publish() { Sync();if(Changed!=null)Changed(FromHsv(h,s,v)); }
    internal void SetColor(Color c)
    {
        double r=c.R/255.0,g=c.G/255.0,b=c.B/255.0;
        double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;
        v=max;s=max==0?0:d/max;
        if(d>0)h=((max==r?(g-b)/d+(g<b?6:0):max==g?(b-r)/d+2:(r-g)/d+4)/6);
        Sync();
    }
    internal static Color FromHsv(double h,double s,double v)
    {
        h=(h%1+1)%1;s=Math.Max(0,Math.Min(1,s));v=Math.Max(0,Math.Min(1,v));
        double x=h*6;int sector=(int)x;double f=x-sector,p=v*(1-s),q=v*(1-f*s),t=v*(1-(1-f)*s);
        double r=0,g=0,b=0;
        switch(sector) {
            case 0:r=v;g=t;b=p;break;case 1:r=q;g=v;b=p;break;
            case 2:r=p;g=v;b=t;break;case 3:r=p;g=q;b=v;break;
            case 4:r=t;g=p;b=v;break;default:r=v;g=p;b=q;break;
        }
        return Color.FromRgb((byte)Math.Round(r*255),(byte)Math.Round(g*255),(byte)Math.Round(b*255));
    }
}

sealed class ChatColorSurface : FrameworkElement
{
    internal bool HueOnly;
    internal double H,S,V;
    internal event Action<double,double> Picked;
    static readonly LinearGradientBrush Rainbow=MakeRainbow();
    static readonly LinearGradientBrush White=new LinearGradientBrush(Colors.White,Colors.Transparent,0);
    static readonly LinearGradientBrush Black=new LinearGradientBrush(Colors.Transparent,Colors.Black,90);
    internal ChatColorSurface() { Focusable=true;ClipToBounds=true;Cursor=System.Windows.Input.Cursors.Cross; }
    static LinearGradientBrush MakeRainbow()
    {
        var b=new LinearGradientBrush { StartPoint=new Point(0,0),EndPoint=new Point(1,0) };
        for(int i=0;i<=6;i++)b.GradientStops.Add(new GradientStop(ChatVisualColorPicker.FromHsv(i/6.0,1,1),i/6.0));
        b.Freeze();return b;
    }
    protected override void OnRender(DrawingContext d)
    {
        var bounds=new Rect(RenderSize);
        if(HueOnly)
        {
            d.DrawRectangle(Rainbow,null,bounds);
            double x=H*Math.Max(0,ActualWidth-1);
            d.DrawLine(new Pen(Brushes.Black,4),new Point(x,0),new Point(x,ActualHeight));
            d.DrawLine(new Pen(Brushes.White,2),new Point(x,0),new Point(x,ActualHeight));
        }
        else
        {
            d.DrawRectangle(new SolidColorBrush(ChatVisualColorPicker.FromHsv(H,1,1)),null,bounds);
            d.DrawRectangle(White,null,bounds);d.DrawRectangle(Black,null,bounds);
            var point=new Point(S*Math.Max(0,ActualWidth-1),(1-V)*Math.Max(0,ActualHeight-1));
            d.DrawEllipse(null,new Pen(Brushes.Black,3),point,5,5);
            d.DrawEllipse(null,new Pen(Brushes.White,1.5),point,5,5);
        }
    }
    internal void PickAt(double x,double y)
    {
        if(Picked==null)return;
        if(HueOnly)Picked(Math.Max(0,Math.Min(1,x/Math.Max(1,ActualWidth-1))),0);
        else Picked(Math.Max(0,Math.Min(1,x/Math.Max(1,ActualWidth-1))),
                    1-Math.Max(0,Math.Min(1,y/Math.Max(1,ActualHeight-1))));
    }
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);Focus();CaptureMouse();var p=e.GetPosition(this);PickAt(p.X,p.Y);e.Handled=true;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(IsMouseCaptured && e.LeftButton==System.Windows.Input.MouseButtonState.Pressed){var p=e.GetPosition(this);PickAt(p.X,p.Y);e.Handled=true;}
    }
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);if(IsMouseCaptured){ReleaseMouseCapture();e.Handled=true;}
    }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int horizontal=e.Key==System.Windows.Input.Key.Right?1:e.Key==System.Windows.Input.Key.Left?-1:0;
        int vertical=e.Key==System.Windows.Input.Key.Up?1:e.Key==System.Windows.Input.Key.Down?-1:0;
        if(Picked!=null && (horizontal!=0||vertical!=0))
        {
            if(HueOnly)Picked((H+(horizontal+vertical)/360.0+1)%1,0);
            else Picked(Math.Max(0,Math.Min(1,S+horizontal*.01)),Math.Max(0,Math.Min(1,V+vertical*.01)));
            e.Handled=true;
        }
    }
}
