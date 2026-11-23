using System.Threading.Channels;

namespace ModemAPI
{
    /// <summary>
    /// Wakes the sender thread of a transmission queue, when a packet is queued while the sender waits.
    /// Waiting with <c>ChannelReader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()</c> cost a <see cref="Task"/> for every
    /// wake-up and a hop through the thread pool (the channel completes its waiter there, and that thread wakes the sender): with
    /// packets coming one by one, that is two thread switches per packet. Here the writer wakes the sender itself, and only when
    /// the sender is idle, so a busy sender costs the writer one interlocked operation per packet.
    /// </summary>
    internal sealed class SenderWakeup
    {
        // 1 while the sender has found the queue empty and waits, or is about to
        private int Idle = 0;
        // without spinning: SemaphoreSlim spins before it sleeps, and a sender, that found the queue empty, rarely gets the next
        // packet within that spin. Two calling modems sending 5-45k packets/s spent 8-12 points of a core less on a Linux server
        private readonly ManualResetEventSlim Signal = new(false, 0);

        /// <summary>
        /// Called by a writer after it queued a packet.
        /// </summary>
        public void Notify()
        {
            // only the writer, that takes the flag, signals: one wake-up for every time the sender becomes idle
            if (Interlocked.CompareExchange(ref Idle, 0, 1) == 1)
            {
                Signal.Set();
            }
        }

        /// <summary>
        /// Called by the sender, when it has sent everything queued: returns once something may be queued again.
        /// </summary>
        public void Wait<T>(ChannelReader<T> queue)
        {
            Interlocked.Exchange(ref Idle, 1);
            // a packet queued after the sender's last read, but before it became idle, didn't signal: the sender looks once more
            if (queue.TryPeek(out _))
            {
                if (Interlocked.Exchange(ref Idle, 0) == 1)
                {
                    return;
                }
                // a writer has taken the flag: its signal is consumed here, or it would wake the sender later for nothing
            }
            Signal.Wait();
            // reset before the sender can become idle again: the next signal belongs to the next idle time
            Signal.Reset();
        }
    }
}
