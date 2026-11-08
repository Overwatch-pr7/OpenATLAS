using System;
using System.Diagnostics;
using System.Windows.Threading;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Models;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench.Plotting
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.3: Decoupled UI Render Loop
    /// Ingestion threads (ACC shared memory / simulation replay) only push snapshots into the
    /// ring buffer; they never touch the UI. This loop runs on the UI thread at a fixed 60 Hz,
    /// copies the latest window out of the buffer, refreshes the trace panes and the sidebar
    /// readouts, then redraws once. Packet rate and frame rate are therefore independent.
    ///
    /// Why the timer doesn't simply use a 16.67 ms interval: DispatcherTimer is rounded to the
    /// Windows clock tick (~15.6 ms), so a 16.67 ms interval fires every 2 ticks = ~31 Hz.
    /// Instead it polls every 5 ms and a Stopwatch deadline lets through exactly one frame
    /// per 1/60 s (measured ~60 fps on this machine).
    /// </summary>
    public sealed class LiveRenderLoop : IDisposable
    {
        public static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 60.0);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

        private readonly CircularBuffer<LiveDeltaSnapshot> _buffer;
        private readonly TracePanes _panes;
        private readonly WorkbenchViewModel _viewModel;
        private readonly Action _redraw;
        private readonly DispatcherTimer _timer;

        // Pre-allocated copy target, reused every frame (no per-frame allocations)
        private readonly LiveDeltaSnapshot[] _frame;

        // Buffer version drawn last frame; -1 forces the first tick to draw
        private long _lastVersion = -1;

        // Frame pacing: time at which the next frame is due
        private readonly Stopwatch _clock = new();
        private TimeSpan _nextFrameDue;

        /// <param name="buffer">Ring buffer filled by the ingestion threads.</param>
        /// <param name="panes">Trace panes whose live scatter buffers get refilled.</param>
        /// <param name="viewModel">Receives the newest snapshot for the sidebar readouts.</param>
        /// <param name="redraw">Calls WpfPlot.Refresh() on each plot control.</param>
        public LiveRenderLoop(
            CircularBuffer<LiveDeltaSnapshot> buffer,
            TracePanes panes,
            WorkbenchViewModel viewModel,
            Action redraw)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _panes = panes ?? throw new ArgumentNullException(nameof(panes));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _redraw = redraw ?? throw new ArgumentNullException(nameof(redraw));

            _frame = new LiveDeltaSnapshot[_panes.LiveCapacity];

            // Render priority: ticks are processed alongside WPF's own layout/render work
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PollInterval };
            _timer.Tick += OnTimerTick;
        }

        public bool IsRunning => _timer.IsEnabled;

        public void Start()
        {
            _nextFrameDue = TimeSpan.Zero;
            _clock.Restart();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _clock.Stop();
        }

        // Fires every ~5 ms; only lets a frame through once per 1/60 s
        private void OnTimerTick(object? sender, EventArgs e)
        {
            TimeSpan now = _clock.Elapsed;
            if (now < _nextFrameDue) return;

            // Advance by exactly one frame to keep a steady average of 60 fps.
            // If we fell far behind (e.g. window was dragged), resync instead of bursting.
            _nextFrameDue += FrameInterval;
            if (now - _nextFrameDue > FrameInterval) _nextFrameDue = now + FrameInterval;

            Tick();
        }

        /// <summary>
        /// One UI frame. Returns true when something was redrawn, false when the buffer
        /// hasn't changed since the last frame (idle frames cost almost nothing).
        /// </summary>
        public bool Tick()
        {
            long version = _buffer.Version;
            if (version == _lastVersion) return false;
            _lastVersion = version;

            // Copy the newest samples (oldest -> newest) under the buffer's lock, then
            // work on our private copy so ingestion threads are never blocked by rendering.
            int count = _buffer.CopyLatest(_frame);

            if (count == 0)
            {
                // Buffer was cleared (lap reset / new reference): hide the live traces
                _panes.ClearLive();
            }
            else
            {
                _panes.UpdateLive(_frame.AsSpan(0, count));
                _viewModel.UpdateSnapshot(_frame[count - 1]);
            }

            _redraw();
            return true;
        }

        public void Dispose() => Stop();
    }
}
