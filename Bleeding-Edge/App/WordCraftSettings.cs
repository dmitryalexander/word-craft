using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// Compose the existing native picker with app-local audio controls.
sealed class WordCraftSettings : Grid
{
    readonly ChatColorPanel colors;
    readonly Slider morse,noise,secondaryNoise;
    readonly ToggleButton[] primaryKinds,secondaryKinds;
    readonly CheckBox playbackOnly,secondaryWhileReading;
    readonly WordCraftCharacterCaret characterCaret;
    readonly WordCraftWindSurface windSurface;
    readonly Slider wind,turbulence,tautness,gravity,texture,weave,textOpacity,fraying,fiberLength;
    readonly CheckBox levelLines,meshVisible,paused;
    readonly TextBlock characterStatus=new TextBlock { Foreground=Brushes.Salmon,TextWrapping=TextWrapping.Wrap };
    internal WordCraftSettings(CloudSmokeView scene,WordCraftAudio sound,WordCraftCharacterCaret characterCaret,WordCraftWindSurface windSurface,Action changed,Action close)
    {
        this.characterCaret=characterCaret;
        this.windSurface=windSurface;
        Width=390;Margin=new Thickness(18);HorizontalAlignment=HorizontalAlignment.Right;
        VerticalAlignment=VerticalAlignment.Stretch;
        RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var audio=new Border { Background=new SolidColorBrush(Color.FromArgb(249,18,25,37)),
            CornerRadius=new CornerRadius(15),Padding=new Thickness(20,14,20,14),Margin=new Thickness(0,0,0,8) };
        var body=new StackPanel();audio.Child=body;Children.Add(audio);
        body.Children.Add(new TextBlock { Text="Audio balance",FontSize=20,Foreground=Brushes.WhiteSmoke });
        morse=AddGain(body,"Morse · typing + reading",sound.MorseVolume,
            value=>{sound.MorseVolume=value;changed();});
        primaryKinds=AddChoices(body,"Primary noise",sound.NoiseKind,value=>sound.NoiseKind=value,changed);
        noise=AddGain(body,"Primary level",sound.NoiseVolume,
            value=>{sound.NoiseVolume=value;changed();});
        secondaryKinds=AddChoices(body,"Secondary noise",sound.SecondaryNoiseKind,value=>sound.SecondaryNoiseKind=value,changed);
        secondaryNoise=AddGain(body,"Secondary level",sound.SecondaryNoiseVolume,
            value=>{sound.SecondaryNoiseVolume=value;changed();});
        secondaryWhileReading=new CheckBox { Content="Add secondary noise while reading",IsChecked=sound.NoiseSecondaryWhileReading,
            Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,9,0,0),
            ToolTip="Adds a separate noise layer over the primary; fades the added layer out when reading stops." };
        body.Children.Add(secondaryWhileReading);
        secondaryWhileReading.Checked+=(s,e)=>{sound.NoiseSecondaryWhileReading=true;changed();};
        secondaryWhileReading.Unchecked+=(s,e)=>{sound.NoiseSecondaryWhileReading=false;changed();};
        playbackOnly=new CheckBox { Content="Primary only during playback",IsChecked=sound.NoisePlaybackOnly,
            Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,9,0,0),ToolTip="Fades in while reading; fades out when paused or finished" };
        body.Children.Add(playbackOnly);
        playbackOnly.Checked+=(s,e)=>{sound.NoisePlaybackOnly=true;changed();};
        playbackOnly.Unchecked+=(s,e)=>{sound.NoisePlaybackOnly=false;changed();};
        colors=new ChatColorPanel(scene.Colors,scene.Phase,phase=>scene.PreviewColors(phase),close);
        colors.Width=Double.NaN;colors.Margin=new Thickness(0);
        colors.HorizontalAlignment=HorizontalAlignment.Stretch;
        // WordCraft has Typing and Reading; collapse the shared panel's unused Thinking tab.
        var colorBody=(StackPanel)((ScrollViewer)colors.Child).Content;
        var motionBody=new StackPanel();
        wind=AddMotion(motionBody,"Wind",windSurface.Wind,2,.1,value=>windSurface.Wind=value);
        turbulence=AddMotion(motionBody,"Turbulence",windSurface.Turbulence,1,.01,value=>windSurface.Turbulence=value);
        tautness=AddMotion(motionBody,"Tautness",windSurface.Tautness,1,.01,value=>windSurface.Tautness=value);
        gravity=AddMotion(motionBody,"Gravity",windSurface.Gravity,1,.01,value=>windSurface.Gravity=value);
        texture=AddMotion(motionBody,"Texture",windSurface.TextureStrength,1,.01,value=>windSurface.TextureStrength=value);
        weave=AddMotion(motionBody,"Weave size",windSurface.WeaveSize,5,.1,value=>windSurface.WeaveSize=value);weave.Minimum=1;
        textOpacity=AddMotion(motionBody,"Opacity",windSurface.TextOpacity,1,.01,value=>windSurface.TextOpacity=value);
        fraying=AddMotion(motionBody,"Fraying",windSurface.Fraying,1,.01,value=>windSurface.Fraying=value);
        fiberLength=AddMotion(motionBody,"Fiber length",windSurface.FiberLength,16,.5,value=>windSurface.FiberLength=value);fiberLength.Minimum=1;
        levelLines=new CheckBox {Content="Keep lines level",IsChecked=windSurface.KeepLinesLevel,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,10,0,0)};
        levelLines.Checked+=(s,e)=>windSurface.SetLevelLines(true);levelLines.Unchecked+=(s,e)=>windSurface.SetLevelLines(false);motionBody.Children.Add(levelLines);
        meshVisible=new CheckBox {Content="Show mesh",IsChecked=windSurface.ShowMesh,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,8,0,0)};
        meshVisible.Checked+=(s,e)=>windSurface.ShowMesh=true;meshVisible.Unchecked+=(s,e)=>windSurface.ShowMesh=false;motionBody.Children.Add(meshVisible);
        paused=new CheckBox {Content="Pause cloth",IsChecked=windSurface.Paused,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,8,0,0)};
        paused.Checked+=(s,e)=>windSurface.Paused=true;paused.Unchecked+=(s,e)=>windSurface.Paused=false;motionBody.Children.Add(paused);
        var reset=new Button {Content="Reset cloth",Margin=new Thickness(0,8,0,6),Padding=new Thickness(8,4,8,4)};reset.Click+=(s,e)=>windSurface.ResetCloth();motionBody.Children.Add(reset);
        motionBody.Children.Add(new TextBlock {Text="Full text · Ctrl+A",Foreground=Brushes.LightSteelBlue});
        colorBody.Children.Insert(2,new Expander {Header="Text motion",Content=motionBody,IsExpanded=true,Foreground=Brushes.WhiteSmoke,Margin=new Thickness(0,0,0,12)});
        var selectedPicker=new ChatVisualColorPicker();selectedPicker.SetColor(characterCaret.SelectedColor);
        var selectedHex=new TextBox { Text=ChatColorProfiles.Hex(characterCaret.SelectedColor),Margin=new Thickness(0,8,0,5),FontFamily=new FontFamily("Consolas") };
        bool changing=false;
        selectedPicker.Changed+=value=>{characterCaret.SelectedColor=value;changing=true;selectedHex.Text=ChatColorProfiles.Hex(value);changing=false;};
        selectedHex.TextChanged+=(s,e)=>{Color value;if(!changing&&ChatColorProfiles.TryColor(selectedHex.Text,out value)){characterCaret.SelectedColor=value;selectedPicker.SetColor(value);}};
        var selectedBody=new StackPanel();selectedBody.Children.Add(selectedPicker);selectedBody.Children.Add(selectedHex);selectedBody.Children.Add(characterStatus);
        colorBody.Children.Insert(2,new Expander { Header="Selected characters",Content=selectedBody,Foreground=Brushes.WhiteSmoke,Margin=new Thickness(0,0,0,12) });
        foreach(UIElement child in colorBody.Children)
        {
            var target=child as ComboBox;
            if(target!=null&&target.Items.Count>3)target.Items[3]="Reading text";
            var tabs=child as Grid;if(tabs==null||tabs.ColumnDefinitions.Count!=3)continue;
            tabs.ColumnDefinitions[1].Width=new GridLength(0);
            foreach(UIElement item in tabs.Children)
            {
                var button=item as Button;if(button==null)continue;
                if(Grid.GetColumn(button)==1)button.Visibility=Visibility.Collapsed;
                else if(Grid.GetColumn(button)==2)button.Content="Reading";
            }
        }
        SetRow(colors,1);Children.Add(colors);
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
    }
    static ToggleButton[] AddChoices(Panel body,string label,int selected,Action<int> select,Action changed)
    {
        body.Children.Add(new TextBlock { Text=label,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,9,0,3) });
        var row=new UniformGrid { Columns=3 };body.Children.Add(row);
        var buttons=new ToggleButton[3];
        Action refresh=()=>{
            for(int i=0;i<3;i++){
                buttons[i].IsChecked=i==selected;
                buttons[i].Background=new SolidColorBrush(i==selected?Color.FromRgb(61,100,119):Color.FromRgb(31,43,59));
                buttons[i].BorderBrush=new SolidColorBrush(i==selected?Color.FromRgb(130,200,204):Color.FromRgb(55,70,89));
            }
        };
        for(int i=0;i<3;i++){
            int index=i;
            var border=new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
            border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            border.SetValue(Border.PaddingProperty,new Thickness(5,7,5,7));
            border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            var content=new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);
            border.AppendChild(content);
            var button=new ToggleButton { Content=new[]{"Dark noise","Darker noise","Darkest noise"}[i],
                Margin=new Thickness(1),Foreground=Brushes.WhiteSmoke,FontSize=12,
                Template=new ControlTemplate(typeof(ToggleButton)) { VisualTree=border } };
            buttons[i]=button;row.Children.Add(button);
            button.Click+=(s,e)=>{selected=index;select(index);changed();refresh();};
        }
        refresh();return buttons;
    }
    static Slider AddGain(Panel body,string name,double initial,Action<double> changed)
    {
        var label=new TextBlock { Text=name+"  "+Math.Round(initial*100)+"%",Foreground=Brushes.LightSteelBlue,
            Margin=new Thickness(0,9,0,2) };
        var slider=new Slider { Minimum=0,Maximum=100,Value=initial*100,TickFrequency=1,
            IsSnapToTickEnabled=true,ToolTip="0% mutes this layer" };
        body.Children.Add(label);body.Children.Add(slider);
        slider.ValueChanged+=(s,e)=>{label.Text=name+"  "+Math.Round(slider.Value)+"%";changed(slider.Value/100);};
        return slider;
    }
    static Slider AddMotion(Panel body,string name,double initial,double maximum,double step,Action<double> changed)
    {
        var label=new TextBlock {Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,8,0,2)};
        var slider=new Slider {Minimum=0,Maximum=maximum,Value=initial,TickFrequency=step,IsSnapToTickEnabled=true};
        Action refresh=()=>label.Text=name+"  "+(name=="Wind"?slider.Value.ToString("0.0")+"×":name=="Weave size"||name=="Fiber length"?slider.Value.ToString("0.0")+" px":Math.Round(slider.Value*100)+"%");
        refresh();slider.ValueChanged+=(s,e)=>{refresh();changed(slider.Value);};body.Children.Add(label);body.Children.Add(slider);return slider;
    }
    internal bool Save(){bool result=colors.Save()&&characterCaret.Save()&&windSurface.Save();characterStatus.Text=characterCaret.Error??windSurface.Error??"";return result;}
    internal void FocusEditor(){morse.Focus();}
}
