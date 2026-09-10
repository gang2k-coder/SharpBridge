// Stop marker for the v2 capture tests: a breakpoint here fires after the
// capture loop has finished all iterations, so the test can read snapshots
// before the process exits (blocked-in-ReadLine processes don't respond to
// pause reliably).
public static class LoopEnd
{
    public static void Signal()
    {
        GC.KeepAlive(0);
    }
}
