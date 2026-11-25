using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Mason.WindowHost;

internal static class WindowPixels
{
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct Point32 {public int X,Y;}
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr hwnd,Point32 point,uint flags);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Point32 point,uint count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int pid);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    internal static object Run(Process process,Request request)
    {
        SetProcessDPIAware();
        var root=process.MainWindowHandle;
        if(!GetClientRect(root,out var r) || r.Right<2 || r.Bottom<2 || r.Right>8192 || r.Bottom>8192 || (long)r.Right*r.Bottom>16000000)
            throw new InvalidOperationException("The attached client area cannot be captured.");
        using var original=new Bitmap(r.Right,r.Bottom,PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(original)){
            var dc=g.GetHdc();
            try{if(!PrintWindow(root,dc,3))throw new InvalidOperationException("Window capture unavailable.");}
            finally{g.ReleaseHdc(dc);}
        }
        double ratio=Math.Min(1,Math.Min(1024.0/r.Right,768.0/r.Bottom));
        int w=(int)(r.Right*ratio),h=(int)(r.Bottom*ratio);
        using var image=new Bitmap(original,new Size(w,h));
        using var data=new MemoryStream();image.Save(data,ImageFormat.Jpeg);
        var bytes=data.ToArray();
        using var sha=SHA256.Create();
        string hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        if(request.operation=="frame")return new{Width=w,Height=h,Hash=hash,Image=Convert.ToBase64String(bytes)};
        if(request.hash!=hash || request.width!=w || request.height!=h || request.operation!="pixel_click" && request.operation!="pixel_drag")
            throw new InvalidOperationException("The observed window frame changed. Observe again before acting.");
        Point32 Point(int x,int y){
            if(x<0||x>=w||y<0||y>=h)throw new ArgumentException("Visual action is outside the attached client area.");
            return new Point32{X=x*r.Right/w,Y=y*r.Bottom/h};
        }
        var first=Point(request.x,request.y);
        var last=request.operation=="pixel_drag"?Point(request.endX,request.endY):first;
        var target=root;
        for(int i=0;i<12;i++){
            var pt=first;MapWindowPoints(root,target,ref pt,1);
            var child=ChildWindowFromPointEx(target,pt,7);
            if(child==IntPtr.Zero||child==target)break;
            target=child;
        }
        void Send(uint msg,Point32 pt,bool down){
            GetWindowThreadProcessId(target,out var owner);
            if(owner!=process.Id || GetAncestor(target,2)!=root || process.MainWindowHandle!=root)throw new InvalidOperationException("Attached input target changed.");
            MapWindowPoints(root,target,ref pt,1);
            if(SendMessageTimeout(target,msg,new IntPtr(down?1:0),new IntPtr((pt.Y<<16)|(pt.X&65535)),2,1200,out _)==IntPtr.Zero)
                throw new InvalidOperationException("The target did not acknowledge the mouse action.");
        }
        Send(0x201,first,true);
        try{if(request.operation=="pixel_drag")for(int i=1;i<=8;i++)Send(0x200,new Point32{X=first.X+(last.X-first.X)*i/8,Y=first.Y+(last.Y-first.Y)*i/8},true);}
        finally{Send(0x202,last,false);}
        return new{Applied=true};
    }
}
