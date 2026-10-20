using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BroadcastPlayout.Engine;

/// <summary>
/// Windows real-time helpers for the Program path: 1 ms system timer resolution for the playout
/// process, MMCSS registration for time-critical threads, and a precise frame clock.
/// </summary>
public static class RealtimeScheduling
{
    private static int _timerResolutionRaised;

    /// <summary>
    /// Raises the system timer resolution to 1 ms once per process (default 15.6 ms), so sleeps and
    /// timers in the decode/emit pumps wake on time. Called only by a process that actually plays out.
    /// </summary>
    public static void EnsureTimerResolution()
    {
        if (Interlocked.Exchange(ref _timerResolutionRaised, 1) != 0) return;
        try
        {
            timeBeginPeriod(1);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { timeEndPeriod(1); } catch { } };
        }
        catch { }
    }

    /// <summary>Registers the calling thread with MMCSS ("Playback") and raises its priority. Dispose to revert.</summary>
    public static IDisposable EnterTimeCriticalThread(ThreadPriority priority = ThreadPriority.Highest)
    {
        var previous = Thread.CurrentThread.Priority;
        try { Thread.CurrentThread.Priority = priority; } catch { }
        var handle = IntPtr.Zero;
        try
        {
            uint taskIndex = 0;
            handle = AvSetMmThreadCharacteristicsW("Playback", ref taskIndex);
        }
        catch { }
        return new Revert(handle, previous);
    }

    private sealed class Revert(IntPtr mmcssHandle, ThreadPriority previous) : IDisposable
    {
        public void Dispose()
        {
            try { if (mmcssHandle != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcssHandle); } catch { }
            try { Thread.CurrentThread.Priority = previous; } catch { }
        }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}

/// <summary>
/// Waits for Stopwatch deadlines with sub-millisecond accuracy and almost no CPU: a high-resolution
/// waitable timer (Windows 10 1803+) sleeps until shortly before the deadline, then the thread yields
/// for the remainder. Falls back to Thread.Sleep(1) where the high-resolution timer is unavailable.
/// One instance per thread.
/// </summary>
public sealed class PrecisionFrameClock : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private static readonly long FinishTicks = Stopwatch.Frequency * 7 / 10000; // 0.7 ms

    private readonly SafeWaitHandle? _timer;
    private readonly EventWaitHandle? _timerWait;

    public PrecisionFrameClock()
    {
        try
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (handle != IntPtr.Zero)
            {
                _timer = new SafeWaitHandle(handle, ownsHandle: true);
                _timerWait = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = _timer };
            }
        }
        catch { _timer = null; }
    }

    public bool HighResolution => _timerWait is not null;

    /// <summary>Returns when <paramref name="deadlineTicks"/> (Stopwatch timestamp) is reached or <paramref name="ct"/> is cancelled.</summary>
    public void WaitUntil(long deadlineTicks, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            if (remaining > FinishTicks)
            {
                var sleepTicks = remaining - FinishTicks;
                if (_timerWait is not null)
                {
                    // Relative due time in 100 ns units (negative = relative).
                    var due = -Math.Max(1, sleepTicks * 10_000_000 / Stopwatch.Frequency);
                    if (SetWaitableTimerEx(_timer!, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    {
                        WaitHandle.WaitAny([_timerWait, ct.WaitHandle]);
                        continue;
                    }
                }
                Thread.Sleep(1);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    public void Dispose()
    {
        _timerWait?.Dispose();
        _timer?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr completionArg, IntPtr wakeContext, uint tolerableDelay);
}
