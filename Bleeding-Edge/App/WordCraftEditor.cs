using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

[assembly:AssemblyTitle("WordCraft")]
[assembly:AssemblyProduct("WordCraft")]
[assembly:AssemblyVersion("2.1.1.0")]

sealed class WordCraftEditor : Window
{
    static WordCraftEditor()
    {AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering",false);}
    const double ModeFadeMilliseconds=700;
    static DoubleAnimation ModeFade(double from,double to)
    {return new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(ModeFadeMilliseconds)) { FillBehavior=FillBehavior.Stop };}
    readonly Grid canvas=new Grid { Background=new SolidColorBrush(Color.FromRgb(24,31,47)) };
    readonly CloudSmokeView scene=new CloudSmokeView();
    readonly TextBox editor=new TextBox {
        AcceptsReturn=true,AcceptsTab=false,TextWrapping=TextWrapping.Wrap,
        Background=Brushes.Transparent,Foreground=Brushes.White,CaretBrush=Brushes.White,
        SelectionBrush=new SolidColorBrush(Color.FromRgb(60,98,145)),BorderThickness=new Thickness(0),
        FontFamily=new FontFamily("Yu Mincho, Meiryo, Segoe UI Emoji"),FontSize=25,
        Padding=new Thickness(50,40,50,40),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,UndoLimit=200
    };
    readonly TextBlock help=new TextBlock {
        Text="Enter  read    Shift+Enter  new line    Tab / Ctrl+Tab  typing / reading\nPaste  insert & read    Space  pause / play    Ctrl+Shift+Space  replay\nCaps Lock  audio balance + WordCraft colors\nCtrl+drag  master volume    Shift+drag  scrub    Hold Shift  slow\nClick a reading word  edit there    Ctrl+A  full text    Alt+A  typing    ` / ~  fullscreen\nEsc  close panel / app    Hold F1  controls",
        Foreground=Brushes.LightSteelBlue,Background=new SolidColorBrush(Color.FromArgb(235,17,23,35)),
        Padding=new Thickness(20),HorizontalAlignment=HorizontalAlignment.Center,
        VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(20),Visibility=Visibility.Collapsed,
        IsHitTestVisible=false,FontSize=15
    };
    readonly TextBlock feedback=new TextBlock {
        Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(220,17,23,35)),
        Padding=new Thickness(9),Margin=new Thickness(14),HorizontalAlignment=HorizontalAlignment.Right,
        VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Collapsed,IsHitTestVisible=false
    };
    readonly System.Windows.Shapes.Line marker=new System.Windows.Shapes.Line {
        Stroke=new SolidColorBrush(Color.FromArgb(160,85,109,143)),StrokeThickness=1,
        Visibility=Visibility.Collapsed,IsHitTestVisible=false
    };
    readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly WordCraftAudio sound;
    readonly WordCraftCharacterCaret characterCaret;
    readonly WordCraftWindSurface windSurface;
    readonly WordCraftQuality quality;
    WordFlowView flow;
    FlowSmokeOverlay readingView;
    string readerText;
    bool reading,closed,fullscreen,volumeDrag,scrubbing,scrubWasReading,scrubFullEditor,volumeDirty,finishWhenRead;
    int soundedAnchor=-1,scrubResume,scrubOriginal,lastSeek=-1;
    int scrubSelectionStart,scrubSelectionLength;
    double startY,startVolume,nextScrub;
    double nextSmokeDeposit;
    double? pendingScrub;
    string audioPath;
    WordCraftSettings settings;
    int viewTransition;
    readonly TranslateTransform readingCoast=new TranslateTransform();
    readonly List<KineticReader.LineWake> readingWakes=new List<KineticReader.LineWake>();
    double lastScrollVelocity;

    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [STAThread]static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID("Dmitr.WordCraft.Desktop");
        Forms.Application.EnableVisualStyles();
        var application=new Application();ErrorLog.Watch(application);application.Run(new WordCraftEditor());
    }
    internal WordCraftEditor(bool enableAudio=true)
    {
        sound=new WordCraftAudio(enableAudio);
        characterCaret=new WordCraftCharacterCaret(editor);
        windSurface=new WordCraftWindSurface(editor);
        quality=new WordCraftQuality(()=>scene.RefreshQuality());
        audioPath=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"..","Audio-Settings.json"));
        LoadVolume();
        Title="WordCraft";Width=1100;Height=760;MinWidth=380;MinHeight=220;
        using(var icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("WordCraft.ico"))
            Icon=BitmapFrame.Create(icon,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
        editor.Opacity=0;editor.IsHitTestVisible=false;
        canvas.Children.Add(scene);canvas.Children.Add(windSurface);canvas.Children.Add(marker);canvas.Children.Add(feedback);canvas.Children.Add(help);
        Content=canvas;
        Loaded+=(s,e)=>{ToggleFullscreen();editor.Focus();};
        timer.Tick+=(s,e)=>Tick();
        editor.PreviewTextInput+=(s,e)=>
        {
            if(settings!=null){e.Handled=true;return;}
            if(String.IsNullOrEmpty(e.Text))return;
            if(reading)ShowEditor();
            sound.PlayTyped(e.Text,clock.Elapsed.TotalSeconds);
            if(!String.IsNullOrWhiteSpace(e.Text))
            {
                string text=editor.Text;int start=editor.SelectionStart,end=start+editor.SelectionLength,left=start,right=end;
                while(left>0&&!Char.IsWhiteSpace(text[left-1]))left--;
                while(right<text.Length&&!Char.IsWhiteSpace(text[right]))right++;
                scene.DepositTypedVersion(text.Substring(left,start-left),e.Text,text.Substring(end,right-end));
            }
        };
        editor.TextChanged+=(s,e)=>{if(!reading){scene.BeginDraftInput();UpdateDraftWord();}};
        editor.SelectionChanged+=(s,e)=>{if(!reading)UpdateDraftWord();};
        scene.Colors.Changed+=ApplyReaderColor;
        StateChanged+=(s,e)=>{scene.SetSuspended(WindowState==WindowState.Minimized);sound.SetSuspended(WindowState==WindowState.Minimized);windSurface.SetSuspended(WindowState==WindowState.Minimized);characterCaret.Refresh(WindowState!=WindowState.Minimized&&editor.IsKeyboardFocused);};
        DataObject.AddPastingHandler(editor,(s,e)=>
        {
            string text=e.DataObject.GetData(DataFormats.UnicodeText) as string ?? e.DataObject.GetData(DataFormats.Text) as string;
            e.CancelCommand();if(text!=null)PasteText(text);
        });
        Deactivated+=(s,e)=>{EndGesture(true,false);help.Visibility=Visibility.Collapsed;HideFeedback();};
        canvas.LostMouseCapture+=(s,e)=>{if((volumeDrag||scrubbing)&&Mouse.Captured!=canvas)EndGesture(true,false);};
        MouseLeave+=(s,e)=>{if(!volumeDrag&&!scrubbing)HideFeedback();};
    }

    void EnsureReader()
    {
        if(flow!=null)return;
        string area=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..",".."));
        flow=new WordFlowView(new WindowInteropHelper(this).Handle,null,area,false);
        flow.Reader.SetFontFamily("Yu Mincho");flow.Reader.SetTuning("circleVisible",0);
        flow.Reader.ReadSlowPlaybackModifier=()=>IsActive&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0;
        flow.Reader.Navigated+=p=>{SoundLetter();if(reading)timer.Start();};
        flow.Reader.PlaybackChanged+=()=>{
            if(reading&&finishWhenRead&&!scrubbing&&flow.Reader.PlaybackDirection==0&&flow.Reader.AtBufferedEnd)
                scene.BeginReadingColorExit();
            sound.SetReading(flow.Reader.PlaybackDirection!=0);
            if(flow.Reader.PlaybackDirection==0)sound.ClearPending();
            SoundLetter();if(reading)timer.Start();
        };
        flow.WordClicked+=word=>ShowEditor(word);
        readingView=new FlowSmokeOverlay(flow,()=>{if(reading)timer.Start();},scene){Visibility=Visibility.Collapsed,RenderTransform=readingCoast};
        canvas.Children.Insert(2,readingView);ApplyReaderColor();
    }
    void ApplyReaderColor()
    {
        if(flow==null)return;Color ink=scene.Colors.Get(2,"text");
        flow.Reader.SingleLineInk=System.Drawing.Color.FromArgb(ink.R,ink.G,ink.B);
        if(readingView!=null)readingView.UpdateFrame();
    }
    internal void ToggleSettings()
    {
        if(settings!=null){CloseSettings();return;}
        EndGesture(true,false);help.Visibility=Visibility.Collapsed;
        settings=new WordCraftSettings(scene,sound,characterCaret,windSurface,quality,()=>volumeDirty=true,CloseSettings);
        Panel.SetZIndex(settings,100);canvas.Children.Add(settings);canvas.UpdateLayout();settings.FocusEditor();
    }
    internal void CloseSettings()
    {
        if(settings==null||!settings.Save()||!SaveVolume())return;
        canvas.Children.Remove(settings);settings=null;scene.PreviewColors(null);ApplyReaderColor();editor.Focus();
    }
    void UpdateDraftWord()
    {
        string text=editor.Text;int left=Math.Max(0,Math.Min(editor.CaretIndex,text.Length)),right=left;
        while(left>0&&!Char.IsWhiteSpace(text[left-1]))left--;
        while(right<text.Length&&!Char.IsWhiteSpace(text[right]))right++;
        scene.ShowWord(text.Substring(left,right-left));
    }
    internal void PasteText(string text)
    {
        if(text==null||text.Length==0)return;
        if(reading)ShowEditor();
        int start=editor.SelectionStart;
        editor.BeginChange();try{editor.SelectedText=text;editor.CaretIndex=start+text.Length;}finally{editor.EndChange();}
        ShowReader(true,true);
    }
    internal void ShowReader(bool autoplay,bool restart)
    {
        if(String.IsNullOrWhiteSpace(editor.Text))return;
        windSurface.Show(false,false);
        characterCaret.Show(false);
        CancelViewTransition();
        lastScrollVelocity=0;
        EnsureReader();flow.Reader.PausePlayback();sound.ClearPending();sound.EndDraft();
        if(readerText!=editor.Text)
        {
            flow.Reader.PausePlayback();
            flow.Open(editor.Text,0,false);readerText=editor.Text;readingView.ClearFrame();restart=true;
        }
        else if(restart)flow.Reader.Seek(0);
        soundedAnchor=-1;reading=true;finishWhenRead=autoplay;
        nextSmokeDeposit=0;
        scene.ResetReadingColorExit();scene.BeginReply();
        editor.Opacity=0;editor.IsHitTestVisible=false;
        readingView.IsHitTestVisible=true;readingView.Visibility=Visibility.Visible;canvas.UpdateLayout();readingView.UpdateFrame();editor.Focus();
        readingView.Opacity=1;
        readingView.BeginAnimation(OpacityProperty,ModeFade(0,1));
        if(autoplay)flow.Reader.StartPlayback(1);
        timer.Start();
    }
    internal void ShowEditor(int word=-1)
    {EnterTyping(word,true);}
    void CancelViewTransition()
    {
        ++viewTransition;
        readingCoast.BeginAnimation(TranslateTransform.XProperty,null);readingCoast.X=0;
        editor.BeginAnimation(OpacityProperty,null);
        if(readingView!=null){readingView.BeginAnimation(OpacityProperty,null);readingView.Visibility=Visibility.Collapsed;}
    }
    void EnterTyping(int word,bool animate)
    {
        windSurface.Show(false,animate);
        characterCaret.Show(false);
        bool wasReading=reading;
        bool coast=wasReading&&flow!=null&&(flow.Reader.PlaybackDirection>0||finishWhenRead&&flow.Reader.AtBufferedEnd);
        double coastVelocity=lastScrollVelocity;
        if(coast&&coastVelocity>=-10)
            coastVelocity=-Math.Max(20,flow.Reader.FocusLetterWidth*flow.Reader.EffectiveLettersPerSecond/VisualTreeHelper.GetDpi(this).DpiScaleX);
        coastVelocity=Math.Max(-Math.Max(80,canvas.ActualWidth*3),coastVelocity);
        double textOpacity=editor.Opacity,readOpacity=readingView==null?0:readingView.Opacity;
        CancelViewTransition();
        sound.ClearPending();
        if(reading&&flow!=null)
        {
            flow.Reader.PausePlayback();sound.ClearPending();
            var words=Regex.Matches(editor.Text,@"\S+");
            int index=word>=0?word:flow.CurrentWord;
            if(words.Count>0){var current=words[Math.Max(0,Math.Min(words.Count-1,index))];editor.CaretIndex=current.Index+current.Length;}
        }
        reading=false;finishWhenRead=false;timer.Stop();
        if(readingView!=null)
        {
            readingView.IsHitTestVisible=false;
            if(animate&&wasReading&&readOpacity>0)
            {
                int transition=viewTransition;
                readingView.Opacity=0;readingView.Visibility=Visibility.Visible;
                var fade=ModeFade(readOpacity,0);
                fade.Completed+=(s,e)=>{if(transition==viewTransition&&!reading)readingView.Visibility=Visibility.Collapsed;};
                readingView.BeginAnimation(OpacityProperty,fade);
                if(coast)readingCoast.BeginAnimation(TranslateTransform.XProperty,
                    ModeFade(0,coastVelocity*ModeFadeMilliseconds/1000));
            }
        }
        scene.KeepReplyPigment();
        editor.Opacity=0;editor.IsHitTestVisible=false;editor.Focus();UpdateDraftWord();
        if(animate&&!wasReading&&textOpacity>0)
            editor.BeginAnimation(OpacityProperty,ModeFade(textOpacity,0));
        editor.ScrollToLine(Math.Max(0,editor.GetLineIndexFromCharacterIndex(editor.CaretIndex)));
    }
    internal void ReturnToTyping()
    {
        bool fullText=!reading&&editor.IsHitTestVisible;
        int caret=editor.SelectionStart+editor.SelectionLength;
        ShowEditor();
        if(fullText){editor.Select(caret,0);UpdateDraftWord();}
    }
    internal bool HandleTypingShortcut(Key key,ModifierKeys modifiers,bool repeat)
    {
        if(key!=Key.A||modifiers!=ModifierKeys.Alt)return false;
        if(!repeat)ReturnToTyping();return true;
    }
    internal bool HandleReplayShortcut(Key key,ModifierKeys modifiers,bool repeat)
    {
        if(key!=Key.Space||modifiers!=(ModifierKeys.Control|ModifierKeys.Shift))return false;
        if(!repeat&&!String.IsNullOrWhiteSpace(editor.Text))
        {
            if(volumeDrag||scrubbing)EndGesture(true,false);
            ShowReader(true,true);
            if(settings!=null)settings.FocusEditor();
        }
        return true;
    }
    internal void ShowFullEditor()
    {
        EnterTyping(-1,false);editor.Opacity=1;editor.IsHitTestVisible=true;characterCaret.Show(true);windSurface.Show(true);
    }
    void SoundLetter()
    {
        if(!reading||scrubbing||flow.Reader.PlaybackDirection==0)return;
        if(flow.Reader.PlaybackDirection>0)finishWhenRead=true;
        var r=flow.Reader;if(soundedAnchor==r.TextAnchor)return;soundedAnchor=r.TextAnchor;
        scene.QueueReplyLetter(r.FocusedWordStart,r.FocusedWordPrefix,r.FocusedGrapheme);
        sound.PlayFocused(r.FocusedWordText,r.TextAnchor-r.FocusedWordStart,r.FocusedGrapheme,1/r.EffectiveLettersPerSecond);
    }
    void Tick()
    {
        if(scrubbing&&pendingScrub.HasValue&&clock.Elapsed.TotalSeconds>=nextScrub)
        {double x=pendingScrub.Value;pendingScrub=null;nextScrub=clock.Elapsed.TotalSeconds+.033;SeekScrub(x);}
        if(!reading){timer.Stop();return;}
        FlushReadingSmoke();
        readingView.UpdateFrame();
        ObserveReadingMotion();
        // Completion returns to the draft. Tab or Ctrl+Shift+Space can reopen the same text.
        if(TryFinishReading())return;
        else if(!scrubbing&&!flow.Reader.Animating&&!scene.ReplySmokePending)timer.Stop();
    }
    void ObserveReadingMotion()
    {
        if(flow.Reader.PlaybackDirection<=0)return;
        flow.Reader.SampleLineWakes(readingWakes);
        double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach(var wake in readingWakes)
        {
            double velocity=wake.Speed*flow.Reader.Width/dpi;
            if(wake.Priority==0||velocity>=-10)continue;
            lastScrollVelocity=lastScrollVelocity<0?lastScrollVelocity*.75+velocity*.25:velocity;
            break;
        }
    }
    internal bool TryFinishReading()
    {
        if(!reading||!finishWhenRead||scrubbing||scene.ReplySmokePending||flow==null||
            flow.Reader.PlaybackDirection!=0||!flow.Reader.AtBufferedEnd)return false;
        // The last letter has played. Do not hold it on screen for spring motion to settle.
        EnterTyping(-1,true);return true;
    }
    internal void ToggleReadingPlayback()
    {
        if(flow.Reader.PlaybackDirection!=0)finishWhenRead=false;
        flow.Reader.TogglePlayback();timer.Start();
    }
    internal void FlushReadingSmoke()
    {
        if(!scene.ReplySmokePending||clock.Elapsed.TotalSeconds<nextSmokeDeposit)return;
        scene.FlushReplySmoke();
        // Cooldown starts after the expensive deposit, so it cannot chain into the next one.
        if(!scene.ReplySmokePending)nextSmokeDeposit=clock.Elapsed.TotalSeconds+.050;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var modifiers=Keyboard.Modifiers;
        var key=e.Key==Key.System?e.SystemKey:e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
        if(HandleReplayShortcut(key,modifiers,e.IsRepeat)){e.Handled=true;return;}
        if(e.Key==Key.Capital){e.Handled=true;if(!e.IsRepeat)ToggleSettings();return;}
        if(settings!=null)
        {if(e.Key==Key.Escape){e.Handled=true;if(!e.IsRepeat)CloseSettings();}return;}
        if(HandleTypingShortcut(key,modifiers,e.IsRepeat)){e.Handled=true;return;}
        if(e.Key==Key.Oem3){e.Handled=true;if(!e.IsRepeat)ToggleFullscreen();return;}
        if(e.Key==Key.Escape)
        {e.Handled=true;if(!e.IsRepeat){if(volumeDrag||scrubbing)EndGesture(true,true);else Close();}return;}
        if((volumeDrag||scrubbing)&&e.Key!=Key.LeftShift&&e.Key!=Key.RightShift&&e.Key!=Key.LeftCtrl&&e.Key!=Key.RightCtrl)
            EndGesture(true,true);
        if(e.Key==Key.Tab)
        {
            e.Handled=true;
            if((modifiers&ModifierKeys.Control)!=0){if(!e.IsRepeat){if(reading)ShowEditor();else ShowReader(false,false);}}
            else if(!e.IsRepeat){if(reading)ShowEditor();else ShowReader(true,false);}
            return;
        }
        if(e.Key==Key.F1){help.Visibility=Visibility.Visible;e.Handled=true;return;}
        if(e.Key==Key.V&&modifiers==ModifierKeys.Control)
        {e.Handled=true;try{if(Clipboard.ContainsText())PasteText(Clipboard.GetText());}catch(ExternalException){}return;}
        if(e.Key==Key.N&&modifiers==ModifierKeys.Control)
        {e.Handled=true;if(!e.IsRepeat){ShowEditor();editor.Clear();readerText=null;}return;}
        if(e.Key==Key.A&&modifiers==ModifierKeys.Control)
        {ShowFullEditor();editor.SelectAll();e.Handled=true;return;}
        if(reading&&e.Key==Key.C&&modifiers==ModifierKeys.Control)
        {try{Clipboard.SetText(editor.Text);}catch(ExternalException){}e.Handled=true;return;}
        if(e.Key==Key.Enter&&(modifiers==ModifierKeys.None||modifiers==ModifierKeys.Control))
        {e.Handled=true;if(!e.IsRepeat){if(reading)ShowEditor();else ShowReader(true,true);}return;}
        if(reading&&e.Key==Key.Space)
        {
            e.Handled=true;if(e.IsRepeat)return;
            ToggleReadingPlayback();return;
        }
        if(reading&&(e.Key==Key.Back||e.Key==Key.Delete||e.Key==Key.Enter))ShowEditor();
        if(reading&&(e.Key==Key.Z||e.Key==Key.X)&&modifiers==ModifierKeys.Control)ShowEditor();
        if(reading&&(e.Key==Key.Left||e.Key==Key.Right||e.Key==Key.Home||e.Key==Key.End))
        {finishWhenRead=false;e.Handled=flow.HandleKey((Forms.Keys)KeyInterop.VirtualKeyFromKey(e.Key));timer.Start();}
        if(!reading&&e.Key==Key.Space&&(modifiers==ModifierKeys.None||modifiers==ModifierKeys.Shift)&&!e.Handled)
        {
            scene.CommitWord();
        }
    }
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if(e.Key==Key.F1)help.Visibility=Visibility.Collapsed;
        if((volumeDrag&&(e.Key==Key.LeftCtrl||e.Key==Key.RightCtrl))||
            (scrubbing&&(e.Key==Key.LeftShift||e.Key==Key.RightShift)))EndGesture(false,true);
        if(e.Key==Key.LeftShift||e.Key==Key.RightShift)HideFeedback();
    }
    void ToggleFullscreen()
    {
        if(fullscreen){WindowState=WindowState.Normal;WindowStyle=WindowStyle.SingleBorderWindow;ResizeMode=ResizeMode.CanResize;fullscreen=false;}
        else{WindowState=WindowState.Normal;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;WindowState=WindowState.Maximized;fullscreen=true;}
    }
    void ShowFeedback(double x,string text)
    {
        feedback.Text=text;feedback.Visibility=Visibility.Visible;
        marker.Visibility=x>=0?Visibility.Visible:Visibility.Collapsed;
        marker.X1=marker.X2=Math.Max(0,Math.Min(canvas.ActualWidth,x));marker.Y1=0;marker.Y2=canvas.ActualHeight;
    }
    void HideFeedback(){feedback.Visibility=marker.Visibility=Visibility.Collapsed;}
    internal void BeginScrub()
    {
        bool wasReading=reading;
        scrubFullEditor=!reading&&editor.Opacity>0;
        scrubSelectionStart=editor.SelectionStart;scrubSelectionLength=editor.SelectionLength;
        if(flow==null||readerText!=editor.Text)ShowReader(false,false);
        if(flow==null)return;
        scrubWasReading=wasReading;scrubResume=wasReading?flow.Reader.PlaybackDirection:0;scrubOriginal=flow.Reader.TextAnchor;
        scrubbing=true;pendingScrub=null;lastSeek=-1;nextScrub=0;
        finishWhenRead=false;
        flow.Reader.PausePlayback();sound.ClearPending();
        scene.ClearReplySmoke();
        if(!reading)ShowReader(false,false);
        timer.Start();
    }
    internal void SeekScrub(double x)
    {
        if(!scrubbing)return;
        pendingScrub=null;
        double fraction=Math.Max(0,Math.Min(1,x/Math.Max(1,canvas.ActualWidth)));
        int target=(int)Math.Round(fraction*Math.Max(0,flow.TextLength-1));
        if(target!=lastSeek&&target!=flow.Reader.TextAnchor)flow.Reader.Seek(target);
        lastSeek=target;soundedAnchor=-1;readingView.UpdateFrame();
        ShowFeedback(x,"Read "+Math.Round(fraction*100)+"%");
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if(settings!=null)return;
        if(volumeDrag||scrubbing)return;
        var p=e.GetPosition(canvas);
        if(Keyboard.Modifiers==ModifierKeys.Control){volumeDrag=true;startY=p.Y;startVolume=sound.Volume;}
        else if(Keyboard.Modifiers==ModifierKeys.Shift&&!String.IsNullOrWhiteSpace(editor.Text)){BeginScrub();SeekScrub(p.X);}
        else {if(!editor.IsHitTestVisible)editor.Focus();return;}
        Mouse.Capture(canvas,CaptureMode.SubTree);e.Handled=true;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);var p=e.GetPosition(canvas);
        if(volumeDrag)
        {
            if(Math.Abs(startY-p.Y)<3)return;
            sound.Volume=Math.Max(0,Math.Min(1,startVolume+(startY-p.Y)/200));volumeDirty=true;
            ShowFeedback(-1,"Volume "+Math.Round(sound.Volume*100)+"%");e.Handled=true;
        }
        else if(scrubbing){pendingScrub=p.X;timer.Start();e.Handled=true;}
        else if(flow!=null&&Keyboard.Modifiers==ModifierKeys.Shift)
            ShowFeedback(p.X,"Read "+Math.Round(Math.Max(0,Math.Min(1,p.X/Math.Max(1,canvas.ActualWidth)))*100)+"%");
        else HideFeedback();
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {base.OnPreviewMouseLeftButtonUp(e);if(volumeDrag||scrubbing){if(scrubbing)SeekScrub(e.GetPosition(canvas).X);EndGesture(false,true);e.Handled=true;}}
    internal void EndGesture(bool cancel,bool resume)
    {
        bool wasScrub=scrubbing,wasVolume=volumeDrag;
        if(wasScrub&&!cancel&&pendingScrub.HasValue)SeekScrub(pendingScrub.Value);
        scrubbing=volumeDrag=false;pendingScrub=null;
        if(Mouse.Captured==canvas)Mouse.Capture(null);HideFeedback();
        if(wasVolume){if(cancel)sound.Volume=startVolume;SaveVolume();}
        if(wasScrub&&!closed)
        {
            if(cancel)flow.Reader.Seek(scrubOriginal);
            if(cancel&&!scrubWasReading){if(scrubFullEditor)ShowFullEditor();else ShowEditor();editor.Select(scrubSelectionStart,scrubSelectionLength);}
            else if(resume&&scrubResume!=0){flow.Reader.StartPlayback(scrubResume);timer.Start();}
        }
    }
    void LoadVolume()
    {
        try{if(File.Exists(audioPath)){
            var values=new JavaScriptSerializer().Deserialize<Dictionary<string,double>>(File.ReadAllText(audioPath));
            double value;if(values==null)return;
            bool darkPalette=values.TryGetValue("noisePalette",out value)&&value>=2;
            if(values.TryGetValue("volume",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.Volume=value;
            if(values.TryGetValue("morseVolume",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.MorseVolume=value;
            if(values.TryGetValue("noiseVolume",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.NoiseVolume=value;
            if(values.TryGetValue("noiseKind",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.NoiseKind=darkPalette?(int)Math.Max(0,Math.Min(2,value)):0;
            if(values.TryGetValue("secondaryNoiseKind",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.SecondaryNoiseKind=darkPalette?(int)Math.Max(0,Math.Min(2,value)):0;
            if(values.TryGetValue("secondaryNoiseVolume",out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value))sound.SecondaryNoiseVolume=value;
            if(values.TryGetValue("noiseSecondaryWhileReading",out value))sound.NoiseSecondaryWhileReading=value!=0;
            if(values.TryGetValue("noisePlaybackOnly",out value))sound.NoisePlaybackOnly=value!=0;
        }}
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
    }
    bool SaveVolume()
    {
        if(!volumeDirty)return true;
        try{
            string temp=audioPath+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(new { volume=sound.Volume,morseVolume=sound.MorseVolume,noiseVolume=sound.NoiseVolume,noiseKind=sound.NoiseKind,noisePlaybackOnly=sound.NoisePlaybackOnly?1:0,secondaryNoiseKind=sound.SecondaryNoiseKind,secondaryNoiseVolume=sound.SecondaryNoiseVolume,noiseSecondaryWhileReading=sound.NoiseSecondaryWhileReading?1:0,noisePalette=2 }));
            if(File.Exists(audioPath))File.Replace(temp,audioPath,null);else File.Move(temp,audioPath);
            volumeDirty=false;return true;
        }
        catch(IOException error){ShowFeedback(-1,"Audio settings: "+error.Message);return false;}
        catch(UnauthorizedAccessException error){ShowFeedback(-1,"Audio settings: "+error.Message);return false;}
    }
    protected override void OnClosed(EventArgs e)
    {closed=true;EndGesture(true,false);if(settings!=null)settings.Save();characterCaret.Save();characterCaret.Dispose();windSurface.Save();windSurface.Dispose();quality.Save();SaveVolume();timer.Stop();sound.Dispose();if(flow!=null)flow.Dispose();scene.Dispose();base.OnClosed(e);}
}
