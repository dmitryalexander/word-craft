using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Web.Script.Serialization;

// Native selection/editing stays intact; only its ink and presentation change.
sealed class WordCraftCharacterCaret : IDisposable
{
    readonly TextBox editor;
    readonly SolidColorBrush blink=new SolidColorBrush(Colors.White);
    bool shown,dirty;
    Rect blinkRect=Rect.Empty;
    int blinkIndex=-1,blinkLength;
    Color selected=Color.FromRgb(128,218,216);
    internal string FilePath=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(WordCraftCharacterCaret).Assembly.Location),"../Editing-Settings.json"));
    internal string Error {get;private set;}
    internal int BlinkIndex {get{return blinkIndex;}}
    internal int BlinkLength {get{return blinkLength;}}
    internal Rect BlinkRect {get{return blinkRect;}}
    internal Color SelectedColor {get{return selected;}set{selected=value;dirty=true;ApplyColor();}}
    internal WordCraftCharacterCaret(TextBox text,string filePath=null)
    {
        editor=text;
        if(filePath!=null)FilePath=Path.GetFullPath(filePath);
        editor.CaretBrush=Brushes.Transparent;
        editor.SelectionBrush=Brushes.Transparent;editor.SelectionOpacity=1;
        editor.IsInactiveSelectionHighlightEnabled=true;
        try{
            if(File.Exists(FilePath)){
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(FilePath));
                Color color;if(data!=null&&data.ContainsKey("selectedCharacters")&&ChatColorProfiles.TryColor(data["selectedCharacters"],out color))selected=color;
            }
        }catch(Exception error){Error=error.Message;}
        ApplyColor();
        editor.SelectionChanged+=(s,e)=>RefreshFocus();editor.TextChanged+=(s,e)=>RefreshFocus();
        editor.GotKeyboardFocus+=(s,e)=>RefreshFocus();editor.LostKeyboardFocus+=(s,e)=>RefreshFocus();
        editor.SizeChanged+=(s,e)=>RefreshFocus();editor.LayoutUpdated+=(s,e)=>RefreshFocus();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>RefreshFocus()));
    }
    void ApplyColor(){var brush=new SolidColorBrush(selected);brush.Freeze();editor.SelectionTextBrush=brush;}
    internal void Show(bool value){shown=value;RefreshFocus();}
    void RefreshFocus(){Refresh(editor.IsKeyboardFocused);}
    internal void Refresh(bool focused)
    {
        int start=editor.CaretIndex;string text=editor.Text;
        if(!shown||!focused||editor.SelectionLength!=0||start<0||start>=text.Length||Char.IsWhiteSpace(text[start])){ClearBlink();return;}
        if(start>0&&Char.IsLowSurrogate(text[start])&&Char.IsHighSurrogate(text[start-1]))start--;
        int length=StringInfo.GetNextTextElement(text,start).Length;
        Rect first=editor.GetRectFromCharacterIndex(start,false),last=editor.GetRectFromCharacterIndex(start+length-1,true);
        if(first.IsEmpty||last.IsEmpty||Math.Abs(first.Top-last.Top)>1){ClearBlink();return;}
        double left=Math.Min(first.Left,last.Left),right=Math.Max(first.Right,last.Right);
        var rect=new Rect(left-.5,first.Top-.5,Math.Max(1,right-left)+1,first.Height+1);
        rect.Intersect(new Rect(0,0,Math.Max(0,editor.ActualWidth),Math.Max(0,editor.ActualHeight)));
        if(rect.IsEmpty){ClearBlink();return;}
        if(blinkIndex==start&&blinkLength==length&&blinkRect==rect&&editor.OpacityMask!=null)return;
        ClearBlink();blinkIndex=start;blinkLength=length;blinkRect=rect;
        var full=new RectangleGeometry(new Rect(0,0,editor.ActualWidth,editor.ActualHeight));
        var hole=new RectangleGeometry(rect);
        var outside=Geometry.Combine(full,hole,GeometryCombineMode.Exclude,null);outside.Freeze();
        var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(Brushes.White,null,outside));
        drawing.Children.Add(new GeometryDrawing(blink,null,hole));
        editor.OpacityMask=new DrawingBrush(drawing) { Stretch=Stretch.Fill };
        var animation=new DoubleAnimationUsingKeyFrames { Duration=TimeSpan.FromSeconds(1),RepeatBehavior=RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.4))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.5))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.9))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        blink.BeginAnimation(Brush.OpacityProperty,animation);
    }
    void ClearBlink()
    {
        if(blinkIndex<0&&editor.OpacityMask==null)return;
        blink.BeginAnimation(Brush.OpacityProperty,null);blink.Opacity=1;
        editor.OpacityMask=null;blinkIndex=-1;blinkLength=0;blinkRect=Rect.Empty;
    }
    internal bool Save()
    {
        if(!dirty)return true;
        try{
            string temp=FilePath+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new {selectedCharacters=ChatColorProfiles.Hex(selected)}));
            if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);
            dirty=false;Error=null;return true;
        }catch(Exception error){Error=error.Message;return false;}
    }
    public void Dispose(){shown=false;ClearBlink();}
}
