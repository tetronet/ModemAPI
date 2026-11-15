namespace ModemAPI
{
    /// <summary>
    /// The window of an SRTP sender, that follows the path instead of always being full. More packets in flight than the path
    /// holds (its delivery rate times its round trip) don't arrive faster: they wait in queues on the way (of the modems, of
    /// the address machines, of the network), and long queues cost round trip, memory, garbage collection and, once full, losses.
    /// So the window is twice what the path holds, as BBR does: the highest delivery rate of the last rounds times the smallest
    /// round trip measured lately, doubled, plus room for the acknowledgements, that come for every 16 packets. The sender's own
    /// choice: nothing changes on the wire, and the receiver doesn't depend on it.
    /// <para>
    /// A round ends when a packet sent at its start is acknowledged. The rate of a round is the packets acknowledged in it per
    /// its duration. While the window is what limits the sender, doubling it doubles the rate of the next round, so it grows
    /// fast until the path is full. Every 10 s the window drops to the minimum for two rounds to measure the round trip without
    /// a queue again (the route may change, and a queue that never drains would look like the round trip itself).
    /// </para>
    /// </summary>
    internal sealed class AdaptiveWindow
    {
        /// <summary>
        /// Not smaller: a receiver acknowledges every 16 delivered packets (<see cref="SRTPClient.AckEveryPackets"/>), fewer
        /// in flight would be acknowledged only by its delay timer.
        /// </summary>
        internal const int MinimumWindow = 32;
        // how many times what the path holds may be in flight
        private const double Gain = 2;
        // the delivery rate is the highest of this many last rounds: one slow round doesn't shrink the window
        private const int RateRounds = 10;
        internal static readonly long ProbeIntervalTicks = TimeSpan.FromSeconds(10).Ticks;
        private long MinRoundTrip = long.MaxValue;
        private long MinRoundTripMeasuredAt = 0;
        private long RoundEnd = 0;
        private long RoundStartedAt = -1;
        private long RoundAcknowledged = 0;
        private readonly double[] Rates = new double[RateRounds]; // packets per tick
        private int RateIndex = 0;
        // 0: normal, 1: draining the queue before a probe, 2: measuring the round trip without a queue
        private int ProbePhase = 0;
        private int Current = MinimumWindow;

        /// <summary>
        /// The window now: how many packets may be in flight, not more than the given maximum.
        /// </summary>
        internal int Get(int maximum)
        {
            return Math.Min(Volatile.Read(ref Current), maximum);
        }

        /// <summary>
        /// Takes an acknowledgement. Called under one lock (the acknowledgement lock of the client).
        /// </summary>
        /// <param name="roundTripTicks">The smallest round trip of the packets it acknowledged, that weren't retransmitted (not used: the round itself is measured)</param>
        /// <param name="acknowledgedId">The highest sequential number it acknowledged</param>
        /// <param name="acknowledgedCount">How many packets it acknowledged</param>
        /// <param name="nextId">The number the next sent packet will get</param>
        /// <param name="maximum">The largest window allowed (MaxUnackedPackets)</param>
        /// <param name="nowTicks">Now, in TimeSpan ticks of any monotonic clock</param>
        internal void Sample(long roundTripTicks, long acknowledgedId, int acknowledgedCount, long nextId, int maximum, long nowTicks)
        {
            if (RoundStartedAt < 0)
            {
                RoundStartedAt = nowTicks;
            }
            RoundAcknowledged += acknowledgedCount;
            if (acknowledgedId < RoundEnd)
            {
                return;
            }
            // a round is complete: the packet sent at its start is acknowledged, so the round lasted its whole round trip,
            // with the wait for the acknowledgement of a batch (the round trip of the single fastest packet is shorter than that)
            long duration = nowTicks - RoundStartedAt;
            if (duration > 0 && ProbePhase != 1)
            {
                if (duration <= MinRoundTrip)
                {
                    MinRoundTrip = duration;
                    MinRoundTripMeasuredAt = nowTicks;
                }
                if (ProbePhase == 0)
                {
                    Rates[RateIndex] = (double)RoundAcknowledged / duration;
                    RateIndex = (RateIndex + 1) % RateRounds;
                }
            }
            switch (ProbePhase)
            {
                case 1:
                    // the queue of the old window is gone; the next round measures the round trip of the small window
                    ProbePhase = 2;
                    MinRoundTrip = long.MaxValue;
                    break;
                case 2:
                    ProbePhase = 0;
                    break;
                default:
                    if (nowTicks - MinRoundTripMeasuredAt > ProbeIntervalTicks)
                    {
                        ProbePhase = 1;
                    }
                    break;
            }
            RoundStartedAt = nowTicks;
            RoundAcknowledged = 0;
            RoundEnd = nextId;
            Volatile.Write(ref Current, ProbePhase == 0 ? Window(maximum) : MinimumWindow);
        }

        private int Window(int maximum)
        {
            double rate = Rates.Max();
            // room for two acknowledgements of 16 packets each on top of what the path holds
            double window = Gain * rate * MinRoundTrip + 2 * 16;
            return (int)Math.Clamp(window, MinimumWindow, Math.Max(maximum, MinimumWindow));
        }
    }
}
