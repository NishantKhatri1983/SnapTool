using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SnapTool;

/// <summary>
/// Gives memory back to Windows after big, short-lived work (captures, scrolling sessions, editor windows).
/// .NET otherwise keeps freed large buffers reserved, so an idle tray app would look hundreds of MB big.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    static DispatcherOperation? _pending;

    static DispatcherTimer? _followUp;

    /// <summary>
    /// Releases memory shortly after the current work settles (coalesces repeated calls), and once more a few seconds
    /// later: the GC hands freed regions back to Windows over its next collections, and native image buffers are only
    /// freed after their finalizers ran in the first pass.
    /// </summary>
    public static void Soon()
    {
        if (_pending is { Status: DispatcherOperationStatus.Pending }) return;
        _pending = Dispatcher.CurrentDispatcher.BeginInvoke(Now, DispatcherPriority.ApplicationIdle);
        _followUp ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.ApplicationIdle, (_, _) => { _followUp!.Stop(); Now(); },
                                          Dispatcher.CurrentDispatcher);
        _followUp.Stop();
        _followUp.Start();
    }

    public static void Now()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var self = Process.GetCurrentProcess();
        EmptyWorkingSet(self.Handle);
        var info = GC.GetGCMemoryInfo();
        self.Refresh();
        Log.Write($"Memory released: GC heap {GC.GetTotalMemory(false) / 1048576} MB, GC committed {info.TotalCommittedBytes / 1048576} MB, " +
                  $"process private {self.PrivateMemorySize64 / 1048576} MB");
    }
}
