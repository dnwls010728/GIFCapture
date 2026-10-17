using System.Runtime.InteropServices;

namespace GifCapture;

/// <summary>Copies a fixed-size block of the screen into a reusable 32bpp DIB via GDI BitBlt.</summary>
sealed class ScreenGrabber : IDisposable
{
    readonly int _w, _h;
    readonly IntPtr _screenDc, _memDc, _bitmap, _oldBitmap, _bits;

    public ScreenGrabber(int width, int height)
    {
        _w = width;
        _h = height;
        _screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(_screenDc);

        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
        {
            Native.DeleteDC(_memDc);
            Native.ReleaseDC(IntPtr.Zero, _screenDc);
            throw new InvalidOperationException(L.F("캡처 버퍼를 만들 수 없습니다 ({0} × {1}).", width, height));
        }
        _oldBitmap = Native.SelectObject(_memDc, _bitmap);
    }

    /// <returns>BGRA pixels of the block whose top-left is <paramref name="origin"/>.</returns>
    public byte[] Grab(Point origin, bool drawCursor, InputOverlay? overlay = null)
    {
        if (!Native.BitBlt(_memDc, 0, 0, _w, _h, _screenDc, origin.X, origin.Y, Native.SRCCOPY))
            throw new InvalidOperationException(L.F("화면 복사 실패 (Win32 오류 {0}).", Marshal.GetLastPInvokeError()));
        if (overlay is { IsActive: true })
        {
            using var g = Graphics.FromHdc(_memDc);
            overlay.Draw(g, origin, new Size(_w, _h));
        }
        if (drawCursor) Native.DrawCursor(_memDc, origin);
        Native.GdiFlush();

        var buf = new byte[_w * _h * 4];
        Marshal.Copy(_bits, buf, 0, buf.Length);
        return buf;
    }

    public void Dispose()
    {
        Native.SelectObject(_memDc, _oldBitmap);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
