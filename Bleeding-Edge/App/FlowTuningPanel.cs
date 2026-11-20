using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

sealed class FlowSetting {
 public readonly string Key,Label;
 public readonly int Min,Max,Default,InputMin,InputMax;
 public FlowSetting(string key,string label,int min,int max,int value,int? inputMin=null,int? inputMax=null){
  Key=key;Label=label;Min=min;Max=max;Default=value;
  InputMin=inputMin??min;InputMax=inputMax??max;
 }
 public int ClampInput(double value){return (int)Math.Max(InputMin,Math.Min(InputMax,value));}
 public int SliderPosition(int value){return Math.Max(Min,Math.Min(Max,value));}
}
sealed class FlowTuning {
 public static readonly FlowSetting ReadingSpeed=new FlowSetting("lettersPerSecond","Reading speed (letters/s)",1,120,21,1,480);
 public static readonly FlowSetting TextSize=new FlowSetting("textZoomPercent","Text size (%)",80,400,136,25,800);
 public static readonly FlowSetting LineSpacing=new FlowSetting("lineSpacing","Vertical line spacing (%)",50,200,25,10,1000);
 public static readonly FlowSetting LavaRadius=new FlowSetting("radius","Lava radius",20,900,541,1,5000);
 public static readonly FlowSetting CircleRadius=new FlowSetting("circleRadius","Base outer radius (pixels)",12,500,150,1,5000);
 public static readonly FlowSetting[] Settings={
  LineSpacing,
  LavaRadius,new FlowSetting("force","Lava force",0,500,136,0,5000),
  new FlowSetting("spring","Spring",10,500,343,1,5000),new FlowSetting("damping","Damping",1,60,46,1,150),
  new FlowSetting("rotation","Rotation limit (degrees)",0,180,100,0,3600),new FlowSetting("rotationResponse","Rotation response (%)",0,200,119,0,2000),
  new FlowSetting("verticalReach","Vertical reach (%)",10,200,200,1,2000),new FlowSetting("wordReach","Whole-word reach (%)",0,200,115,0,2000),
  new FlowSetting("heatTint","Heat tint",0,1,0),
  new FlowSetting("waveReach","Letter growth reach (%)",25,300,70,1,5000),new FlowSetting("peakPercent","Peak letter size (%)",100,250,250,25,400),new FlowSetting("growthEase","Growth smoothing (response)",1,100,69,1,1000),
  new FlowSetting("circleVisible","Dotted rings",0,1,1),new FlowSetting("circleSpeed","Ring speed (degrees/s)",0,90,62,-3600,3600),
  CircleRadius,new FlowSetting("circleOpacity","Ring brightness",0,255,90),
  new FlowSetting("narrationBass","Bass · 180 Hz (dB)",-12,12,0),
  new FlowSetting("narrationMids","Mids · 1.2 kHz (dB)",-12,12,0),
  new FlowSetting("narrationTreble","Treble · 4.5 kHz (dB)",-12,12,0),
  new FlowSetting("narrationComma","Comma pause (ms)",-1,800,-1,-1,2000),
  new FlowSetting("narrationSentence","Sentence pause (ms)",-1,1200,-1,-1,3000)
 };
 readonly Dictionary<string,int> values=new Dictionary<string,int>();
 public FlowTuning(){foreach(var s in Settings)values[s.Key]=s.Default;}
 public int Get(string key){return values[key];}
 public void Set(string key,double value){foreach(var s in Settings)if(s.Key==key){values[key]=s.ClampInput(value);return;}}
 public int Radius{get{return Get("radius");}}public int Force{get{return Get("force");}}public int Spring{get{return Get("spring");}}public int Damping{get{return Get("damping");}}public int Rotation{get{return Get("rotation");}}public bool HeatTint{get{return Get("heatTint")!=0;}}
}
// Small owned native window. Native numeric entry and sliders update the reader live.
sealed class FlowTuningPanel : Form {
 sealed class TuningScrollPanel : FlowLayoutPanel {
  public void ScrollVertically(int delta){
   if(VerticalScroll.Visible)
    OnMouseWheel(new HandledMouseEventArgs(MouseButtons.None,0,0,0,delta));
  }
 }
 readonly TuningScrollPanel body=new TuningScrollPanel();
 readonly Dictionary<string,Action<int>> inputs=new Dictionary<string,Action<int>>();
 readonly Dictionary<string,Action> refresh=new Dictionary<string,Action>();
 readonly ComboBox fontFamily=new ComboBox();
 readonly KineticReader reader;
 readonly Keys toggleKey;
 public Action HiddenByUser;
 public Action FullscreenRequested;
 public FlowTuningPanel(KineticReader reader,Func<float> zoom,Action<float> setZoom,Action changed,Func<bool> saveDefaults,Func<bool> restoreDefaults,Keys toggleKey=Keys.T){
  this.reader=reader;
  this.toggleKey=toggleKey;
  Text="PageCraft tuning  ·  T to hide";ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.SizableToolWindow;
  BackColor=Color.FromArgb(20,27,34);ForeColor=Color.FromArgb(191,213,228);Font=new Font("Segoe UI",9);ClientSize=new Size(356,680);MinimumSize=new Size(340,360);StartPosition=FormStartPosition.Manual;
  body.Dock=DockStyle.Fill;body.FlowDirection=FlowDirection.TopDown;body.WrapContents=false;body.AutoScroll=true;body.Padding=new Padding(12);Controls.Add(body);
  AddHeading("READING");AddNumber(FlowTuning.ReadingSpeed,()=>reader.LettersPerSecond,v=>{reader.SetSpeed(v);changed();});
  AddNumber(FlowTuning.TextSize,()=>(int)Math.Round(zoom()*100),v=>setZoom(v/100f));
  body.Controls.Add(new Label{Text="Font family",Width=304,Height=22});
  fontFamily.Width=304;fontFamily.DropDownStyle=ComboBoxStyle.DropDownList;
  var names=new List<string>();
  foreach(var family in FontFamily.Families)using(family)if(family.IsStyleAvailable(FontStyle.Regular))names.Add(family.Name);
  names.Sort(StringComparer.CurrentCultureIgnoreCase);fontFamily.Items.AddRange(names.ToArray());
  fontFamily.SelectedItem=reader.FontFamilyName;
  fontFamily.SelectedIndexChanged+=(o,e)=>{if(fontFamily.SelectedItem!=null&&reader.SetFontFamily((string)fontFamily.SelectedItem))changed();};
  refresh["fontFamily"]=()=>fontFamily.SelectedItem=reader.FontFamilyName;
  body.Controls.Add(fontFamily);
  AddSetting(FlowTuning.LineSpacing,reader,changed);
  var play=new Button{Text="Play / pause",Width=304,Height=30,FlatStyle=FlatStyle.Flat};play.Click+=(o,e)=>reader.TogglePlayback();body.Controls.Add(play);
  AddHeading("NARRATION");
  var narration=new CheckBox{Name="narrationEnabled",Text="Phonon narration (31M)",Width=304,Height=28,Checked=reader.NarrationEnabled};
  var prepare=new Button{Name="prepareNarration",Text="Prepare narration (C)",Width=304,Height=30,FlatStyle=FlatStyle.Flat};
  var narrationStatus=new Label{Width=304,Height=49,ForeColor=Color.FromArgb(133,175,200)};
  Action refreshNarration=()=>{
   narration.Checked=reader.NarrationEnabled;
   prepare.Enabled=reader.NarrationEnabled;
   prepare.Text=reader.NarrationPreparing?"Cancel preparation":"Prepare narration (C)";
   narrationStatus.Text=reader.NarrationStatus??(reader.NarrationEnabled?
    "Space prepares and plays the next passage.\nThe audio sets the pace.":"Enable for English read-along.");
  };
  narration.CheckedChanged+=(o,e)=>{if(narration.Checked==reader.NarrationEnabled)return;reader.SetNarration(narration.Checked);changed();};
  prepare.Click+=(o,e)=>reader.ToggleNarrationPreparation();
  body.Controls.Add(narration);body.Controls.Add(prepare);body.Controls.Add(narrationStatus);
  reader.NarrationChanged+=refreshNarration;
  Disposed+=(o,e)=>reader.NarrationChanged-=refreshNarration;
  refreshNarration();
  var defaultsRow=new Panel{Width=304,Height=36};
  var save=new Button{Name="saveDefaults",Text="Save Defaults",Bounds=new Rectangle(0,0,148,32),FlatStyle=FlatStyle.Flat};
  var reset=new Button{Name="resetDefaults",Text="Reset Defaults",Bounds=new Rectangle(156,0,148,32),FlatStyle=FlatStyle.Flat};
  var status=new Label{Width=304,Height=20,ForeColor=Color.FromArgb(133,175,200)};
  save.Click+=(o,e)=>{status.Text=saveDefaults()?"Defaults saved.":"";};
  reset.Click+=(o,e)=>{if(restoreDefaults()){RefreshValues();status.Text="Defaults loaded.";}};
  defaultsRow.Controls.Add(save);defaultsRow.Controls.Add(reset);body.Controls.Add(defaultsRow);body.Controls.Add(status);
  AddHeading("NARRATION EQ · LIVE");
  foreach(var setting in FlowTuning.Settings)if(setting.Key=="narrationBass"||setting.Key=="narrationMids"||setting.Key=="narrationTreble")
   AddSetting(setting,reader,changed);
  AddHeading("NARRATION PAUSES");
  body.Controls.Add(new Label{Text="−1 keeps the natural pause. Changes apply when the next clip starts.",Width=304,Height=36});
  foreach(var setting in FlowTuning.Settings)if(setting.Key=="narrationComma"||setting.Key=="narrationSentence")
   AddSetting(setting,reader,changed);
  AddHeading("FOCUS RINGS");
  foreach(string key in new[]{"circleRadius","circleSpeed","circleOpacity","circleVisible"})foreach(var setting in FlowTuning.Settings)if(setting.Key==key)AddSetting(setting,reader,changed);
  AddHeading("LAVA");
  foreach(var setting in FlowTuning.Settings){if(setting.Key.StartsWith("circle")||setting.Key.StartsWith("narration")||setting.Key=="lineSpacing")continue;if(setting.Key=="waveReach")AddHeading("LETTER GROWTH");AddSetting(setting,reader,changed);}
  string toggleLabel=toggleKey==Keys.CapsLock?"Caps Lock":"T";
  var note=new Label{Text="Focused word stays joined and clear of lava.\n0 ring speed holds the dots still. "+toggleLabel+" / Esc hides.",Width=304,Height=50};body.Controls.Add(note);
  FormClosing+=(o,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();if(HiddenByUser!=null)HiddenByUser();}};
 }
 void AddSetting(FlowSetting s,KineticReader reader,Action changed){
  if(s.Max==1){var box=new CheckBox{Text=s.Label,Width=304,Height=30,Checked=reader.Tuning.Get(s.Key)!=0};box.CheckedChanged+=(o,e)=>{reader.SetTuning(s.Key,box.Checked?1:0);changed();};body.Controls.Add(box);refresh[s.Key]=()=>box.Checked=reader.Tuning.Get(s.Key)!=0;inputs[s.Key]=v=>box.Checked=v!=0;}
  else AddNumber(s,()=>reader.Tuning.Get(s.Key),v=>{reader.SetTuning(s.Key,v);changed();});
 }
 void AddHeading(string text){body.Controls.Add(new Label{Text=text,Width=304,Height=30,Padding=new Padding(0,10,0,0),ForeColor=Color.FromArgb(133,175,200)});}
 void AddNumber(FlowSetting setting,Func<int> get,Action<int> set){
  var row=new Panel{Width=304,Height=62};var name=new Label{Text=setting.Label,AutoSize=false,Bounds=new Rectangle(0,0,219,24)};
  var number=new NumericUpDown{Name=setting.Key,Minimum=setting.InputMin,Maximum=setting.InputMax,Value=setting.ClampInput(get()),Bounds=new Rectangle(224,0,78,24),BackColor=Color.FromArgb(32,42,51),ForeColor=ForeColor};
  var slider=new TrackBar{Minimum=setting.Min,Maximum=setting.Max,Value=setting.SliderPosition((int)number.Value),TickStyle=TickStyle.None,Bounds=new Rectangle(-6,26,314,30),SmallChange=1,LargeChange=Math.Max(1,(setting.Max-setting.Min)/20)};bool sync=false;
  number.ValueChanged+=(o,e)=>{if(sync)return;sync=true;slider.Value=setting.SliderPosition((int)number.Value);sync=false;set((int)number.Value);};
  slider.ValueChanged+=(o,e)=>{if(sync)return;sync=true;number.Value=slider.Value;sync=false;set(slider.Value);};
  row.Controls.Add(name);row.Controls.Add(number);row.Controls.Add(slider);body.Controls.Add(row);
  inputs[setting.Key]=v=>number.Value=setting.ClampInput(v);
  refresh[setting.Key]=()=>{sync=true;number.Value=setting.ClampInput(get());slider.Value=setting.SliderPosition((int)number.Value);sync=false;};
 }
 public void RefreshValues(){foreach(var a in refresh.Values)a();}
 public void SetControlValue(string key,int value){inputs[key](value);}
 public void SetFontChoice(string name){fontFamily.SelectedItem=name;}
 public void SavePreview(string path,bool bottom){body.AutoScrollPosition=new Point(0,bottom?body.DisplayRectangle.Height:0);using(var image=new Bitmap(Width,Height)){DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path);}}
 public bool HasSetting(string key){return refresh.ContainsKey(key);}
 public bool HandleVerticalWheel(Message message){
  if(!Visible||message.Msg!=0x020a)return false;
  Control target=Control.FromChildHandle(message.HWnd);
  if(target==null||target.FindForm()!=this)return false;
  if(target==fontFamily&&fontFamily.DroppedDown)return false;
  body.ScrollVertically(unchecked((short)(message.WParam.ToInt64()>>16)));
  return true;
 }
 protected override bool ProcessCmdKey(ref Message msg,Keys key){
  bool repeat=(msg.LParam.ToInt64()&0x40000000)!=0;
  if(key==Keys.C&&reader.NarrationEnabled&&!fontFamily.ContainsFocus){if(!repeat)reader.PrepareNextNarration();return true;}
  if(FullscreenRequested!=null&&(key==Keys.Oemtilde||key==(Keys.Shift|Keys.Oemtilde)||key==(Keys.Control|Keys.F11))){
   if(!repeat)FullscreenRequested();return true;
  }
  if(key==toggleKey||key==Keys.Escape){if(!repeat){Hide();if(HiddenByUser!=null)HiddenByUser();}return true;}return base.ProcessCmdKey(ref msg,key);
 }
}
