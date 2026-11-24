using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Grably.Tests;

/// <summary>Runs test code on an STA thread with a WPF dispatcher (WPF imaging and windows need it).</summary>
internal static class StaRunner
{
    public static void Run(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
