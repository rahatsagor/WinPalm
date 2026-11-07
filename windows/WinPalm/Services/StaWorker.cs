using System.Collections.Concurrent;
namespace WinPalm.Services;
/// Dedicated STA with a message pump for clipboard and shell COM objects.
internal sealed class StaWorker : IDisposable
{
  private readonly Thread thread;
  private readonly TaskCompletionSource<Control> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal StaWorker(string name)
  {
    thread = new Thread(() =>
    {
      using var control = new Control(); _ = control.Handle;
      ready.SetResult(control); Application.Run();
    })
    { IsBackground = true, Name = name };
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
  }
  internal T Run<T>(Func<T> work)
  {
    var control = ready.Task.GetAwaiter().GetResult();
    if (!control.InvokeRequired) return work();
    return (T)control.Invoke(work);
  }
  public void Dispose()
  {
    if (ready.Task.IsCompletedSuccessfully) ready.Task.Result.BeginInvoke(Application.ExitThread);
    thread.Join(TimeSpan.FromSeconds(2));
  }
}
