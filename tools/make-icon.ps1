# Genera l'icona (icon.ico + icon-1024.png) con System.Drawing. Uso: powershell -File tools\make-icon.ps1
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.IO; using System.Collections.Generic;
public static class IconGen {
  static GraphicsPath Round(RectangleF r, float rad){ var p=new GraphicsPath(); float d=rad*2; p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90); p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p; }
  public static Bitmap Draw(int S){
    var bm=new Bitmap(S,S,PixelFormat.Format32bppArgb); using(var g=Graphics.FromImage(bm)){
      g.SmoothingMode=SmoothingMode.AntiAlias; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
      float s=S/512f;
      var bg=new RectangleF(16*s,16*s,480*s,480*s);
      using(var p=Round(bg,104*s)) using(var br=new LinearGradientBrush(bg,Color.FromArgb(255,20,34,48),Color.FromArgb(255,9,13,19),90f)) g.FillPath(br,p);
      using(var p=Round(bg,104*s)) using(var pen=new Pen(Color.FromArgb(255,38,60,82),6*s)) g.DrawPath(pen,p);
      var cx=256*s; var cy=268*s;
      using(var pen=new Pen(Color.FromArgb(255,34,211,238),14*s)){ pen.StartCap=LineCap.Round; pen.EndCap=LineCap.Round;
        g.DrawArc(pen,cx-170*s,cy-170*s,340*s,340*s,-62,124);
        g.DrawArc(pen,cx-170*s,cy-170*s,340*s,340*s,118,124); }
      using(var pen=new Pen(Color.FromArgb(120,34,211,238),5*s)) g.DrawEllipse(pen,cx-120*s,cy-120*s,240*s,240*s);
      foreach(var a in new[]{-62.0, 118.0}){ double r=a*Math.PI/180; float x=cx+(float)(170*s*Math.Cos(r)), y=cy+(float)(170*s*Math.Sin(r));
        using(var br=new SolidBrush(Color.FromArgb(255,245,158,11))) g.FillEllipse(br,x-24*s,y-24*s,48*s,48*s);
        using(var pen=new Pen(Color.FromArgb(255,13,20,28),7*s)) g.DrawEllipse(pen,x-24*s,y-24*s,48*s,48*s); }
      float pr=64*s, py=cy-28*s, k=0.70711f*pr, tip=py+pr+78*s;
      var pin=new GraphicsPath(); pin.AddArc(cx-pr,py-pr,pr*2,pr*2,135,270); pin.AddLine(cx+k,py+k,cx,tip); pin.AddLine(cx,tip,cx-k,py+k); pin.CloseFigure();
      var sh=(GraphicsPath)pin.Clone(); var m=new Matrix(); m.Translate(0,9*s); sh.Transform(m);
      using(var br=new SolidBrush(Color.FromArgb(80,0,0,0))) g.FillPath(br,sh);
      using(var br=new LinearGradientBrush(new RectangleF(cx-pr,py-pr,pr*2,tip-py+pr),Color.FromArgb(255,255,255,255),Color.FromArgb(255,206,220,234),90f)) g.FillPath(br,pin);
      using(var br=new SolidBrush(Color.FromArgb(255,11,87,208))) g.FillEllipse(br,cx-27*s,py-27*s,54*s,54*s);
    }
    return bm; }
  public static void Run(string dir){
    Directory.CreateDirectory(dir);
    var sizes=new[]{16,24,32,48,64,128,256}; var pngs=new List<byte[]>();
    foreach(var sz in sizes){ using(var b=Draw(sz*4)) using(var d=new Bitmap(sz,sz,PixelFormat.Format32bppArgb)) { using(var g=Graphics.FromImage(d)){ g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.DrawImage(b,0,0,sz,sz);} using(var ms=new MemoryStream()){ d.Save(ms,ImageFormat.Png); pngs.Add(ms.ToArray()); } } }
    using(var big=Draw(1024)) big.Save(Path.Combine(dir,"icon-1024.png"),ImageFormat.Png);
    using(var fs=File.Create(Path.Combine(dir,"icon.ico"))) using(var w=new BinaryWriter(fs)){
      w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); int off=6+16*sizes.Length;
      for(int i=0;i<sizes.Length;i++){ w.Write((byte)(sizes[i]==256?0:sizes[i])); w.Write((byte)(sizes[i]==256?0:sizes[i])); w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(off); off+=pngs[i].Length; }
      foreach(var p in pngs) w.Write(p); } } }
'@
[IconGen]::Run((Join-Path $PSScriptRoot "..\src\GpsBridge.App\Assets\icon"))
Copy-Item (Join-Path $PSScriptRoot "..\src\GpsBridge.App\Assets\icon\icon-1024.png") (Join-Path $PSScriptRoot "..\docs\icon.png") -Force
