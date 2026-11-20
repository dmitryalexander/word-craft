using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

// Native keycap guide. Finite WPF fades only; no capture, blur or permanent timer.
sealed class WordCraftControlsOverlay : Grid
{
    readonly ScrollViewer scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
    readonly WrapPanel groups=new WrapPanel();
    readonly List<StackPanel> sections=new List<StackPanel>();
    readonly List<Grid> rows=new List<Grid>();
    int generation;
    internal bool IsOpen {get;private set;}
    internal int KeycapCount {get;private set;}
    static readonly Brush ink=new SolidColorBrush(Color.FromRgb(230,237,246));
    static readonly Brush muted=new SolidColorBrush(Color.FromRgb(157,173,194));
    internal WordCraftControlsOverlay()
    {
        Visibility=Visibility.Collapsed;Opacity=0;IsHitTestVisible=false;Focusable=false;
        Background=new SolidColorBrush(Color.FromArgb(215,5,9,15));
        scroll.MaxWidth=1080;scroll.Margin=new Thickness(32,22,32,22);scroll.HorizontalAlignment=HorizontalAlignment.Center;
        Children.Add(scroll);var body=new StackPanel();scroll.Content=body;
        var heading=new StackPanel {Margin=new Thickness(12,4,12,24)};body.Children.Add(heading);
        heading.Children.Add(new TextBlock {Text="Controls",FontFamily=new FontFamily("Segoe UI"),FontSize=28,FontWeight=FontWeights.SemiBold,Foreground=ink});
        heading.Children.Add(new TextBlock {Text="Hold F1 to keep this guide open.  Release to return.",FontSize=13,Foreground=muted,Margin=new Thickness(0,7,0,0)});
        body.Children.Add(groups);
        Section("WRITE",new[]{
            new[]{"Enter","Read / return to writing"},new[]{"Shift+Enter","New line"},
            new[]{"Ctrl+V","Paste and read"},new[]{"Ctrl+A","Full text / select all"},
            new[]{"Alt+A","Back to smoke typing"},new[]{"Ctrl+N","Clear the draft"}});
        Section("READ",new[]{
            new[]{"Space","Play / pause"},new[]{"Ctrl+Shift+Space","Replay from the beginning"},
            new[]{"Shift","Slow reading · hold"},new[]{"← / →","Previous / next word"},
            new[]{"Home / End","Beginning / end"},new[]{"@Click a word","Edit at that word"}});
        Section("MOVE & LOOK",new[]{
            new[]{"@Wheel ↑↓","Simulation size · 25–100%"},new[]{"Ctrl+@wheel","Reading text size"},
            new[]{"Shift+@drag","Scrub through the text"},new[]{"Ctrl+@drag ↑↓","Master volume"},
            new[]{"`","Toggle fullscreen"},new[]{"Esc","Cancel gesture / close"}});
        Section("OPTIONS & EDITING",new[]{
            new[]{"Caps Lock","Quality, sound, colors & cloth"},new[]{"Ctrl+C","Copy · whole text in reading"},
            new[]{"Ctrl+X","Cut selected text"},new[]{"Ctrl+Z / Y","Undo / redo in editing"},
            new[]{"F1","Show this guide · hold"},new[]{"Esc","Close options / exit app"}});
        body.Children.Add(new TextBlock {Text="Your draft is session-only. Copy it elsewhere before closing.  ·  Scroll for more controls on smaller screens.",TextWrapping=TextWrapping.Wrap,Foreground=muted,FontSize=12,Margin=new Thickness(12,0,12,8)});
        SizeChanged+=(s,e)=>AdaptLayout();
    }
    void Section(string title,string[][] entries)
    {
        var section=new StackPanel {Margin=new Thickness(12,0,12,20)};sections.Add(section);groups.Children.Add(section);
        section.Children.Add(new TextBlock {Text=title,Foreground=muted,FontSize=11,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        foreach(var entry in entries){
            var row=new Grid {Margin=new Thickness(0,0,0,8)};row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(220)});row.ColumnDefinitions.Add(new ColumnDefinition());
            row.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});row.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var keys=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            foreach(string raw in Regex.Split(entry[0],"([+/])")){
                string key=raw.Trim();if(key.Length==0)continue;
                if(key=="+"||key=="/")keys.Children.Add(new TextBlock {Text=key,Foreground=muted,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,3,0),FontSize=12});
                else if(key.StartsWith("@"))keys.Children.Add(new TextBlock {Text=key.Substring(1),Foreground=ink,FontSize=13,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,3,4)});
                else keys.Children.Add(Keycap(key));
            }
            var label=new TextBlock {Text=entry[1],Foreground=ink,FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12,0,0,0)};
            Grid.SetColumn(label,1);row.Children.Add(keys);row.Children.Add(label);rows.Add(row);section.Children.Add(row);
        }
    }
    Border Keycap(string label)
    {
        KeycapCount++;
        double width=label=="Space"?68:label=="Caps Lock"?82:label=="Ctrl"?44:label=="Shift"?48:label=="Enter"?52:label.Length>2?48:30;
        return new Border {Tag="keycap",CornerRadius=new CornerRadius(6),Background=new SolidColorBrush(Color.FromRgb(4,8,13)),Padding=new Thickness(1,1,1,3),Margin=new Thickness(0,0,3,0),
            Child=new Border {CornerRadius=new CornerRadius(5),MinWidth=width,Padding=new Thickness(7,4,7,4),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromRgb(69,88,111)),Background=new SolidColorBrush(Color.FromRgb(28,39,53)),
                Child=new TextBlock {Text=label,FontFamily=new FontFamily("Segoe UI, Segoe UI Symbol"),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=ink,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}}};
    }
    void AdaptLayout()
    {
        double width=Math.Max(180,Math.Min(1040,ActualWidth-96));int columns=width>=1024?2:1;
        double sectionWidth=width/columns-24;foreach(var section in sections)section.Width=sectionWidth;
        bool stacked=sectionWidth<420;
        foreach(var row in rows){
            row.ColumnDefinitions[0].Width=stacked?new GridLength(1,GridUnitType.Star):new GridLength(220);
            var label=(TextBlock)row.Children[1];Grid.SetRow(label,stacked?1:0);Grid.SetColumn(label,stacked?0:1);Grid.SetColumnSpan(label,stacked?2:1);label.Margin=stacked?new Thickness(0,6,0,2):new Thickness(12,0,0,0);
        }
    }
    internal void Show()
    {
        if(IsOpen)return;
        ++generation;double from=Opacity;IsOpen=true;Visibility=Visibility.Visible;IsHitTestVisible=true;
        BeginAnimation(OpacityProperty,null);Opacity=1;AdaptLayout();
        BeginAnimation(OpacityProperty,new DoubleAnimation(from,1,TimeSpan.FromMilliseconds(180)) {EasingFunction=new SineEase {EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop});
    }
    internal void Hide(bool immediate=false)
    {
        int current=++generation;double from=Opacity;IsOpen=false;IsHitTestVisible=false;BeginAnimation(OpacityProperty,null);Opacity=0;
        if(immediate){Visibility=Visibility.Collapsed;return;}
        var fade=new DoubleAnimation(from,0,TimeSpan.FromMilliseconds(130)) {FillBehavior=FillBehavior.Stop};
        fade.Completed+=(s,e)=>{if(current==generation&&!IsOpen)Visibility=Visibility.Collapsed;};BeginAnimation(OpacityProperty,fade);
    }
    internal void HandleKey(Key key)
    {
        if(key==Key.Escape)Hide();else if(key==Key.Down)scroll.LineDown();else if(key==Key.Up)scroll.LineUp();
        else if(key==Key.PageDown)scroll.PageDown();else if(key==Key.PageUp)scroll.PageUp();else if(key==Key.Home)scroll.ScrollToTop();else if(key==Key.End)scroll.ScrollToEnd();
    }
}
