using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

// Native selection/scrolling stays in RichEdit; the paper ruling is display-only.
sealed class RuledReader : RichTextBox {
 public bool Ruled;
 public int NativePaints;
 public event Action<float> ZoomRequested;
 protected override void OnMouseWheel(MouseEventArgs e){if((ModifierKeys&Keys.Control)!=0&&ZoomRequested!=null){var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;ZoomRequested(ZoomFactor+(e.Delta/120f)*.1f);return;}base.OnMouseWheel(e);}
 public static readonly Color RuleColor=Color.FromArgb(198,221,242);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wp,IntPtr lp);
 public int TopCharacter{get{int line=SendMessage(Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32();return Math.Max(0,GetFirstCharIndexFromLine(line));}}
 public void ScrollToCharacter(int index){int wanted=GetLineFromCharIndex(Math.Max(0,Math.Min(index,TextLength)));bool redraw=Visible;if(redraw)SendMessage(Handle,0xB,IntPtr.Zero,IntPtr.Zero);try{for(int pass=0;pass<4;pass++){int actual=SendMessage(Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32();if(actual==wanted)break;SendMessage(Handle,0xB6,IntPtr.Zero,new IntPtr(wanted-actual));}}finally{if(redraw){SendMessage(Handle,0xB,new IntPtr(1),IntPtr.Zero);Invalidate();}}}
 public void DrawRules(Graphics graphics,Rectangle bounds,Point origin){
  if(!Ruled||!IsHandleCreated)return;
  float scale=graphics.DpiY/96f*ZoomFactor,step=32*scale;
  int line=SendMessage(Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32(),index=SendMessage(Handle,0xBB,new IntPtr(line),IntPtr.Zero).ToInt32();
  float y=origin.Y+GetPositionFromCharIndex(Math.Max(0,index)).Y+30*scale;
  while(y>bounds.Top)y-=step;while(y<bounds.Top)y+=step;
  var state=graphics.Save();graphics.SetClip(bounds,System.Drawing.Drawing2D.CombineMode.Intersect);using(var pen=new Pen(RuleColor,1)){for(;y<bounds.Bottom;y+=step)graphics.DrawLine(pen,bounds.Left,y,bounds.Right-1,y);}graphics.Restore(state);
 }
 protected override void WndProc(ref Message message){base.WndProc(ref message);if(message.Msg==0xF)NativePaints++;if(message.Msg==0xF&&Ruled&&IsHandleCreated)using(var g=Graphics.FromHwnd(Handle))DrawRules(g,ClientRectangle,Point.Empty);}
 protected override void OnVScroll(EventArgs e){base.OnVScroll(e);if(Ruled&&Parent!=null)Parent.Invalidate();}
}

