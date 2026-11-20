using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

// The reader caches native font masks and composites them into a premultiplied frame.
sealed unsafe class ReaderRaster : IDisposable {
 sealed class Mask {
  public byte[] Alpha;
  public int Width,Height,OffsetX,OffsetY;
  public int[] RowStart,RowEnd;
  public Mask Halo;
 }
 readonly Dictionary<string,Mask> regular=new Dictionary<string,Mask>(),bold=new Dictionary<string,Mask>();
 readonly Dictionary<FontStyle,Font> styledRegular=new Dictionary<FontStyle,Font>(),styledFocus=new Dictionary<FontStyle,Font>();
 readonly Dictionary<int,uint[]> inkTables=new Dictionary<int,uint[]>();
 Bitmap frame;BitmapData bits;int width,height,stride,maskBytes;
 uint clearPixel=0xff000000;
 int paintedTop,paintedBottom;
 internal Rectangle PaintedBand { get { return new Rectangle(0,paintedTop,width,Math.Max(0,paintedBottom-paintedTop)); } }
 internal Bitmap Frame { get { return frame; } }
 Font regularFont,boldFont;float regularHeight,boldHeight,dpiX=96,dpiY=96;
 readonly StringFormat format=(StringFormat)StringFormat.GenericTypographic.Clone();
 public int CachedMasks{get{return regular.Count+bold.Count;}}
 public ReaderRaster(){format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces;}
 public void SetFonts(Font normal,Font focus,float normalHeight,float focusHeight,float dx,float dy){ClearStyledFonts();regularFont=normal;boldFont=focus;regularHeight=normalHeight;boldHeight=focusHeight;dpiX=dx;dpiY=dy;regular.Clear();bold.Clear();maskBytes=0;}
 void ClearStyledFonts(){
  foreach(var font in styledRegular.Values)font.Dispose();
  foreach(var font in styledFocus.Values)font.Dispose();
  styledRegular.Clear();styledFocus.Clear();
 }
 internal Font GlyphFont(bool focused,FontStyle style){
  Font basis=focused?boldFont:regularFont;
  if((style&~basis.Style)==0)return basis;
  var cache=focused?styledFocus:styledRegular;
  Font font;
  if(!cache.TryGetValue(style,out font)){
   using(var family=new FontFamily(basis.Name)){
    FontStyle face=(basis.Style|style)&(FontStyle.Bold|FontStyle.Italic);
    if(!family.IsStyleAvailable(face))face=family.IsStyleAvailable(FontStyle.Italic)&&(face&FontStyle.Italic)!=0?FontStyle.Italic:basis.Style;
    cache[style]=font=new Font(family,basis.Size,face|(style&FontStyle.Strikeout),basis.Unit);
   }
  }
  return font;
 }
 public void Begin(int w,int h,bool transparent=false){
  uint background=transparent?0u:0xff000000u;
  bool partial=transparent&&clearPixel==background&&frame!=null&&w==width&&h==height;
  int clearTop=partial?paintedTop:0,clearBottom=partial?paintedBottom:h;
  if(clearPixel!=background){clearPixel=background;inkTables.Clear();}
  if(frame==null||w!=width||h!=height){if(frame!=null)frame.Dispose();frame=new Bitmap(w,h,PixelFormat.Format32bppPArgb);width=w;height=h;}
  bits=frame.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadWrite,PixelFormat.Format32bppPArgb);stride=bits.Stride/4;
  uint* pixels=(uint*)bits.Scan0;for(int y=clearTop;y<clearBottom;y++){uint* line=pixels+y*stride;for(int x=0;x<width;x++)line[x]=background;}
  paintedTop=h;paintedBottom=0;
 }
 public void End(){if(bits!=null){frame.UnlockBits(bits);bits=null;}}
 public void Paint(Graphics g){if(frame!=null){var mode=g.CompositingMode;g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;try{g.DrawImageUnscaled(frame,0,0);}finally{g.CompositingMode=mode;}}}
 Mask Get(string text,bool focused,float advance,FontStyle style){
  var cache=focused?bold:regular;string key=(char)style+text;Mask mask;if(cache.TryGetValue(key,out mask))return mask;
  float h=focused?boldHeight:regularHeight;int pad=(int)Math.Ceiling(h*.23f),w=Math.Max(1,(int)Math.Ceiling(advance))+2*pad,rows=(int)Math.Ceiling(h)+2*pad;
  var coverage=new byte[w*rows];int left=w,right=-1,top=rows,bottom=-1;
  using(var image=new Bitmap(w,rows,PixelFormat.Format32bppPArgb)){
   image.SetResolution(dpiX,dpiY);using(var g=Graphics.FromImage(image)){g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.DrawString(text,GlyphFont(focused,style),Brushes.White,new PointF(pad,pad),format);}
   var data=image.LockBits(new Rectangle(0,0,w,rows),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
   try{byte* source=(byte*)data.Scan0;for(int y=0;y<rows;y++)for(int x=0;x<w;x++){byte a=source[y*data.Stride+x*4+3];coverage[y*w+x]=a;if(a!=0){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}}}finally{image.UnlockBits(data);}
  }
  if(right<left)mask=new Mask{Alpha=new byte[0]};else{
   left=Math.Max(0,left-1);right=Math.Min(w-1,right+1);top=Math.Max(0,top-1);bottom=Math.Min(rows-1,bottom+1);
   int croppedWidth=right-left+1,croppedHeight=bottom-top+1;mask=new Mask{Alpha=new byte[croppedWidth*croppedHeight],Width=croppedWidth,Height=croppedHeight,OffsetX=left-pad,OffsetY=top-pad};
   mask.RowStart=new int[croppedHeight];mask.RowEnd=new int[croppedHeight];
   for(int row=0;row<croppedHeight;row++){
    Array.Copy(coverage,(top+row)*w+left,mask.Alpha,row*croppedWidth,croppedWidth);
    int start=0,end=croppedWidth;
    while(start<end&&mask.Alpha[row*croppedWidth+start]==0)start++;
    while(end>start&&mask.Alpha[row*croppedWidth+end-1]==0)end--;
    mask.RowStart[row]=start;mask.RowEnd[row]=end;
   }
  }
  int bytes=mask.Alpha.Length+mask.Height*8;
  if(maskBytes+bytes<=16*1024*1024&&CachedMasks<4096){cache[key]=mask;maskBytes+=bytes;}return mask;
 }
 static uint Mix(uint old, int alpha, uint redBlue, uint green) {
  uint a = (uint)alpha, inverse = 255 - a;
  // Each 16-bit lane holds one channel. This is exact rounded division by 255.
  uint rb = (old & 0x00ff00ff) * inverse + redBlue * a + 0x00800080;
  uint g = ((old >> 8) & 255) * inverse + green * a + 128;
  rb = (rb + ((rb >> 8) & 0x00ff00ff)) >> 8;
  g = (g + (g >> 8)) >> 8;
  uint coverage = a + ((old >> 24) * inverse + 127) / 255;
  return (coverage << 24) | (rb & 0x00ff00ff) | (g << 8);
 }
 uint[] InkTable(Color ink, uint redBlue, uint green) {
  int key = ink.ToArgb();
  uint[] table;
  if (inkTables.TryGetValue(key, out table)) return table;
  if (inkTables.Count >= 256) return null;
  table = new uint[256];
  for (int a = 0; a < table.Length; a++) table[a] = Mix(clearPixel, a, redBlue, green);
  inkTables.Add(key, table);
  return table;
 }
 // Two box passes per axis give a soft halo. Cache it with the glyph, within the mask budget.
 Mask BloomMask(Mask glyph,float height){
  if(glyph.Halo!=null)return glyph.Halo;
  int radius=Math.Max(3,Math.Min(12,(int)Math.Round(height*.07f))),pad=2*radius+1;
  int w=glyph.Width+2*pad,h=glyph.Height+2*pad;
  var alpha=new byte[w*h];
  var work=new byte[alpha.Length];
  for(int y=0;y<glyph.Height;y++)
   Array.Copy(glyph.Alpha,y*glyph.Width,alpha,(y+pad)*w+pad,glyph.Width);
  for(int pass=0;pass<2;pass++){
   Blur(alpha,work,w,h,1,w,radius);
   Blur(work,alpha,h,w,w,1,radius);
  }
  var halo=new Mask{Alpha=alpha,Width=w,Height=h,OffsetX=glyph.OffsetX-pad,OffsetY=glyph.OffsetY-pad,RowStart=new int[h],RowEnd=new int[h]};
  for(int row=0;row<h;row++){
   int start=0,end=w,offset=row*w;
   while(start<end&&alpha[offset+start]==0)start++;
   while(end>start&&alpha[offset+end-1]==0)end--;
   halo.RowStart[row]=start;halo.RowEnd[row]=end;
  }
  int bytes=alpha.Length+h*8;
  if(maskBytes+bytes<=16*1024*1024){glyph.Halo=halo;maskBytes+=bytes;}
  return halo;
 }
 static void Blur(byte[] source,byte[] target,int length,int lines,int stride,int lineStride,int radius){
  int window=2*radius+1;
  for(int line=0;line<lines;line++){
   int offset=line*lineStride,sum=0;
   for(int i=0;i<Math.Min(length,radius+1);i++)sum+=source[offset+i*stride];
   for(int i=0;i<length;i++){
    target[offset+i*stride]=(byte)((sum+radius)/window);
    if(i>=radius)sum-=source[offset+(i-radius)*stride];
    if(i+radius+1<length)sum+=source[offset+(i+radius+1)*stride];
   }
  }
 }
 public void Glyph(string text,bool focused,float advance,float x,float y,float sx,float sy,float angle,Color ink,int opacity,FontStyle style=FontStyle.Regular,bool bloom=false){
  if(opacity<=0||bits==null||!(sx>0&&sy>0))return;var mask=Get(text,focused,advance,style);if(mask.Width==0)return;float h=focused?boldHeight:regularHeight;
  if(bloom)mask=BloomMask(mask,h);
  uint redBlue=(uint)(ink.ToArgb()&0x00ff00ff),green=ink.G;
  uint[] inkTable=InkTable(ink,redBlue,green);
  int mw=mask.Width,mh=mask.Height;uint* destination=(uint*)bits.Scan0;
  fixed(byte* alpha=mask.Alpha){
   if(angle==0&&Math.Abs(sx-1)<.0002f&&Math.Abs(sy-1)<.0002f){
    int left=(int)Math.Round(x-advance/2)+mask.OffsetX,top=(int)Math.Round(y-h/2)+mask.OffsetY;
    int x0=Math.Max(0,-left),x1=Math.Min(mw,width-left),y0=Math.Max(0,-top),y1=Math.Min(mh,height-top);
    if(x1>x0&&y1>y0){paintedTop=Math.Min(paintedTop,top+y0);paintedBottom=Math.Max(paintedBottom,top+y1);}
    for (int v = y0; v < y1; v++) {
     int start=Math.Max(x0,mask.RowStart[v]),end=Math.Min(x1,mask.RowEnd[v]);
     byte* src = alpha + v * mw + start;
     uint* dst = destination + (top + v) * stride + left + start;
     for (int u = start; u < end; u++, src++, dst++) {
      int a = *src;
      if (a == 0) continue;
      a = (a * opacity + 127) / 255;
      *dst = inkTable != null && *dst == clearPixel ? inkTable[a] : Mix(*dst, a, redBlue, green);
     }
    }
    return;
   }
   double radians=angle*Math.PI/180;float c=(float)Math.Cos(radians),s=(float)Math.Sin(radians),ox=advance/2-mask.OffsetX,oy=h/2-mask.OffsetY;
   // Bound the transformed mask itself, including its offset from the glyph anchor.
   float centerX=(mw*.5f-ox)*sx,centerY=(mh*.5f-oy)*sy;
   float cx=x+c*centerX-s*centerY,cy=y+s*centerX+c*centerY;
   float rx=mw*.5f*sx,ry=mh*.5f*sy,bx=Math.Abs(c)*rx+Math.Abs(s)*ry,by=Math.Abs(s)*rx+Math.Abs(c)*ry;
   int l=Math.Max(0,(int)Math.Floor(cx-bx)),right=Math.Min(width,(int)Math.Ceiling(cx+bx)),t=Math.Max(0,(int)Math.Floor(cy-by)),bottom=Math.Min(height,(int)Math.Ceiling(cy+by));
   if(right>l&&bottom>t){paintedTop=Math.Min(paintedTop,t);paintedBottom=Math.Max(paintedBottom,bottom);}
   float du=c/sx,dv=-s/sy;
   for(int py=t;py<bottom;py++){float dy=py-y,dx=l-x,u=ox+(c*dx+s*dy)/sx,v=oy+(-s*dx+c*dy)/sy;uint* dst=destination+py*stride+l;
    for(int px=l;px<right;px++,dst++,u+=du,v+=dv){if(u<0||v<0||u>=mw-1||v>=mh-1)continue;int ix=(int)u,iy=(int)v;byte* p=alpha+iy*mw+ix;int a0=p[0],a1=p[1],a2=p[mw],a3=p[mw+1];if((a0|a1|a2|a3)==0)continue;
     float fx = u - ix, fy = v - iy;
     int a = (int)((a0 + (a1 - a0) * fx + (a2 - a0 + (a3 - a2 - a1 + a0) * fx) * fy) * opacity / 255 + .5f);
     if (a != 0) *dst = inkTable != null && *dst == clearPixel ? inkTable[a] : Mix(*dst, a, redBlue, green);
    }
   }
  }
 }
 public void Dispose(){End();if(frame!=null){frame.Dispose();frame=null;}ClearStyledFonts();regular.Clear();bold.Clear();inkTables.Clear();format.Dispose();}
}
