// Marker reached after the capture loop completes.

public static class LoopEnd
{
    public static void Signal()
    {
        GC.KeepAlive(0);
    }
}
