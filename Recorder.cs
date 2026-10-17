using System.Collections.Concurrent;
using System.Diagnostics;

namespace GifCapture;

/// <summary>Captures a screen region on one thread and stores de-duplicated frames on another.</summary>
sealed class Recorder
{
    readonly record struct Frame(byte[] Pixels, long Ms);

    readonly Size _size;
    readonly IntPtr _trackWindow;
    readonly Size _trackOffset;
    readonly int _fps;
    readonly bool _drawCursor;
    readonly InputOverlay? _overlay;
    readonly BlockingCollection<Frame> _queue = new();
    readonly Stopwatch _clock = new();
    readonly object _regionLock = new();
    readonly List<int> _delays = [];
    Rectangle _region;
    Thread? _captureThread, _storeThread;
    volatile bool _stopRequested;
    volatile byte[]? _latest;
    long _endMs;
    int _captured;

    public FrameStore Frames { get; }
    /// <summary>Duration of each stored frame in milliseconds; complete once <see cref="StopAsync"/> finishes.</summary>
    public IReadOnlyList<int> Delays => _delays;
    public Exception? Error { get; private set; }
    public int CapturedFrames => _captured;
    public TimeSpan Elapsed => _clock.Elapsed;
    public Size FrameSize => _size;
    /// <summary>Most recently captured pixels (BGRA, <see cref="FrameSize"/>), for live preview.</summary>
    public byte[]? LatestFrame => _latest;

    /// <summary>Region currently being captured, in screen coordinates. Moves when following a window.</summary>
    public Rectangle CurrentRegion
    {
        get { lock (_regionLock) return _region; }
        private set { lock (_regionLock) _region = value; }
    }

    /// <param name="trackWindow">When non-zero, the region keeps its offset from this window's top-left as it moves.</param>
    /// <param name="overlay">Click rings / key captions drawn into each frame, if any.</param>
    public Recorder(Rectangle region, IntPtr trackWindow, int fps, bool drawCursor, InputOverlay? overlay = null)
    {
        _region = region;
        _size = region.Size;
        _fps = fps;
        _drawCursor = drawCursor;
        _overlay = overlay;
        Frames = new FrameStore(_size);
        if (trackWindow != IntPtr.Zero && Native.TryGetWindowBounds(trackWindow, out var bounds))
        {
            _trackWindow = trackWindow;
            _trackOffset = new Size(region.X - bounds.X, region.Y - bounds.Y);
        }
    }

    public void Start()
    {
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Capture", Priority = ThreadPriority.AboveNormal };
        _storeThread = new Thread(StoreLoop) { IsBackground = true, Name = "Store" };
        _storeThread.Start();
        _captureThread.Start();
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return Task.Run(() =>
        {
            _captureThread?.Join();
            _storeThread?.Join();
        });
    }

    void CaptureLoop()
    {
        Native.timeBeginPeriod(1);
        try
        {
            using var grabber = new ScreenGrabber(_size.Width, _size.Height);
            double interval = 1000.0 / _fps;
            long tick = 0;
            _clock.Start();

            while (!_stopRequested)
            {
                long now = _clock.ElapsedMilliseconds;
                if (UpdateTrackedRegion())
                {
                    var pixels = grabber.Grab(CurrentRegion.Location, _drawCursor, _overlay);
                    _latest = pixels;
                    _queue.Add(new Frame(pixels, now));
                    _captured++;
                }

                tick++;
                long wait = (long)(tick * interval) - _clock.ElapsedMilliseconds;
                if (wait > 0)
                    Thread.Sleep((int)wait);
                else if (-wait > interval * 3)
                    tick = (long)(_clock.ElapsedMilliseconds / interval); // fell far behind: drop frames instead of bursting
            }
        }
        catch (Exception ex)
        {
            Error ??= ex;
        }
        finally
        {
            _endMs = _clock.ElapsedMilliseconds;
            _clock.Stop();
            Native.timeEndPeriod(1);
            _queue.CompleteAdding();
        }
    }

    /// <returns>False when this tick should not be captured (tracked window minimized); the previous frame then lasts longer.</returns>
    bool UpdateTrackedRegion()
    {
        if (_trackWindow == IntPtr.Zero || !Native.IsWindow(_trackWindow))
            return true;
        if (Native.IsIconic(_trackWindow))
            return false;
        if (Native.TryGetWindowBounds(_trackWindow, out var b))
            CurrentRegion = new Rectangle(b.X + _trackOffset.Width, b.Y + _trackOffset.Height, _size.Width, _size.Height);
        return true;
    }

    void StoreLoop()
    {
        try
        {
            byte[]? prev = null;
            long prevMs = 0;
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                // Identical frames just extend the previous frame's duration.
                if (prev != null && prev.AsSpan().SequenceEqual(frame.Pixels))
                    continue;
                if (prev != null)
                    _delays.Add((int)(frame.Ms - prevMs));
                Frames.Append(frame.Pixels);
                prev = frame.Pixels;
                prevMs = frame.Ms;
            }
            if (prev != null)
                _delays.Add((int)Math.Max(1, _endMs - prevMs));
        }
        catch (Exception ex)
        {
            Error ??= ex;
            _stopRequested = true;
            // Keep draining so the capture thread never blocks on a dead consumer.
            foreach (var _ in _queue.GetConsumingEnumerable()) { }
        }
    }
}
