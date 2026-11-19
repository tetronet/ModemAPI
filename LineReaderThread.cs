namespace ModemAPI
{
    /// <summary>
    /// Marks the thread, that reads a line of a modem (a LowLatency line, or the RAWWS receive loop of a VirtualModem), while it
    /// hands a received packet to the subscribers and the slaves.
    /// Such a thread must never wait for a line to take a packet: when the other side stops reading, the sender of this modem
    /// stops, the queue fills, and a reader waiting for it would stop reading too. Both sides doing so is a deadlock, and even one
    /// side stops all of its receiving (MODEM LOCKDOWN). So a full queue drops the packet for the reader thread (an SRTP
    /// acknowledgement is resent anyway), as with DropPacketsWhenOverloaded.
    /// </summary>
    internal static class LineReaderThread
    {
        [ThreadStatic]
        private static int Depth;

        public static bool IsCurrent => Depth > 0;

        public static void Enter() => Depth++;

        public static void Exit() => Depth--;
    }
}
