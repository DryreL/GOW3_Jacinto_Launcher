using System;
namespace JacintoLauncher;
internal sealed class LaunchLifecycle
{
    private DateTime deadline;
    private bool pending;
    internal bool Busy { get; private set; }
    internal void Begin(DateTime now) { pending = true; deadline = now.AddSeconds(30); Busy = true; }
    internal void Failed() { pending = false; Busy = false; }
    internal bool Observe(bool gameRunning, DateTime now)
    {
        if (gameRunning) pending = false;
        if (pending && now >= deadline) pending = false;
        Busy = gameRunning || pending;
        return Busy;
    }
}
