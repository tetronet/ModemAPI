using System.Reflection;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// The adaptive window of the SRTP sender on a model path: a bottleneck of some rate behind a queue, and a round trip.
    /// </summary>
    public class AdaptiveWindowTests
    {
        private static readonly long Millisecond = TimeSpan.FromMilliseconds(1).Ticks;

        /// <summary>
        /// A sender, that always has data: every round it sends the whole window, and the round lasts the round trip, or longer,
        /// when the window holds more than the bottleneck passes in a round trip (the rest waits in the queue).
        /// </summary>
        private sealed class Path(double packetsPerMillisecond, long roundTripTicks)
        {
            public readonly AdaptiveWindow Window = new();
            public double Rate = packetsPerMillisecond / Millisecond;
            public long RoundTrip = roundTripTicks;
            public int Maximum = 1024;
            // big packets by default: the cushion for small packets is then less than 10 packets
            public int PacketBytes = 60000;
            public long Now = Millisecond * 1000;
            private long NextId = 0;

            public int Round()
            {
                int window = Window.Get(Maximum);
                for (int i = 0; i < Math.Min(window, 64); i++)
                {
                    Window.PacketSent(PacketBytes);
                }
                long first = NextId;
                NextId += window;
                Now += Math.Max(RoundTrip, (long)(window / Rate));
                Window.Sample(first + window - 1, window, NextId, Maximum, Now);
                return window;
            }
        }

        [Fact]
        public void FastPathOpensTheWholeWindowQuickly()
        {
            Path path = new(packetsPerMillisecond: 1_000_000, roundTripTicks: Millisecond);
            Assert.Equal(AdaptiveWindow.MinimumWindow, path.Window.Get(1024));
            int rounds = 0;
            while (path.Window.Get(1024) < 1024)
            {
                path.Round();
                Assert.True(++rounds < 10, "the window grows too slowly");
            }
        }

        [Fact]
        public void WindowStaysAtTwiceWhatThePathHolds()
        {
            // 100 packets per ms for 2 ms: the path holds 200 packets
            Path path = new(packetsPerMillisecond: 100, roundTripTicks: 2 * Millisecond);
            for (int i = 0; i < 100; i++)
            {
                path.Round();
            }
            int window = path.Window.Get(1024);
            Assert.InRange(window, 2 * 200 + 32, 2 * 200 + 32 + 10);
            // a slower path: the window shrinks after the faster rounds leave the rate memory
            path.Rate /= 4;
            for (int i = 0; i < 20; i++)
            {
                path.Round();
            }
            Assert.InRange(path.Window.Get(1024), 2 * 50 + 32, 2 * 50 + 32 + 10);
        }

        [Fact]
        public void RoundTripIsMeasuredAgainEveryTenSeconds()
        {
            Path path = new(packetsPerMillisecond: 100, roundTripTicks: 2 * Millisecond);
            for (int i = 0; i < 100; i++)
            {
                path.Round();
            }
            // the route got longer: the window still uses the old round trip until the probe
            path.RoundTrip = 4 * Millisecond;
            List<int> windows = [];
            long probeBy = path.Now + AdaptiveWindow.ProbeIntervalTicks + 100 * Millisecond;
            while (path.Now < probeBy)
            {
                windows.Add(path.Round());
            }
            for (int i = 0; i < 20; i++)
            {
                windows.Add(path.Round());
            }
            // two rounds of the minimum window, then twice what the longer path holds (400 packets)
            int probeStart = windows.IndexOf(AdaptiveWindow.MinimumWindow);
            Assert.True(probeStart > 0, "no probe");
            Assert.Equal(AdaptiveWindow.MinimumWindow, windows[probeStart + 1]);
            Assert.NotEqual(AdaptiveWindow.MinimumWindow, windows[probeStart + 2]);
            Assert.InRange(windows[^1], 2 * 400 + 32, 2 * 400 + 32 + 10);
        }

        [Fact]
        public void SmallPacketsGetACushionButNotOnASlowLine()
        {
            // 1 KB packets, 100 per ms for 2 ms: twice the 200 packets the path holds, plus 5 ms of the rate (500 packets, less than 512 KB)
            Path fast = new(packetsPerMillisecond: 100, roundTripTicks: 2 * Millisecond) { PacketBytes = 1024 };
            for (int i = 0; i < 100; i++)
            {
                fast.Round();
            }
            Assert.InRange(fast.Window.Get(2048), 2 * 200 + 32 + 495, 2 * 200 + 32 + 505);
            // a faster path: the cushion stops at 512 KB
            Path faster = new(packetsPerMillisecond: 1000, roundTripTicks: Millisecond / 10) { PacketBytes = 1024, Maximum = 4096 };
            for (int i = 0; i < 100; i++)
            {
                faster.Round();
            }
            Assert.InRange(faster.Window.Get(4096), 2 * 100 + 32 + 505, 2 * 100 + 32 + 520);
            // 2 Mbit/s with 1 KB packets (0.25 per ms) and 200 ms (the path holds 50): 5 ms of the rate is about one packet
            Path slow = new(packetsPerMillisecond: 0.25, roundTripTicks: 200 * Millisecond) { PacketBytes = 1024 };
            for (int i = 0; i < 100; i++)
            {
                slow.Round();
            }
            Assert.InRange(slow.Window.Get(1024), 2 * 50 + 32, 2 * 50 + 32 + 2);
        }

        [Fact]
        public void WindowStaysWithinTheMaximum()
        {
            Path path = new(packetsPerMillisecond: 1_000_000, roundTripTicks: Millisecond) { Maximum = 100 };
            for (int i = 0; i < 30; i++)
            {
                Assert.InRange(path.Round(), 1, 100);
            }
            Assert.Equal(10, path.Window.Get(10));
        }

        [Fact]
        public void PeerAcknowledgingEveryPacketGetsTheWholeWindow()
        {
            using FakeLink link = new() { StripMetadata = true }; // the receiver takes the messages for v1 ones
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 9, 10_000_000);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "q", 9, 10_000_000);
            try
            {
                PropertyInfo window = typeof(SRTPClient).GetProperty("CurrentWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.Equal(AdaptiveWindow.MinimumWindow, window.GetValue(sender));
                sender.Transmit([1]);
                sender.WaitForAcknowledgements();
                Assert.Equal(sender.MaxUnackedPackets, window.GetValue(sender));
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        [Fact]
        public void ClientTellsTheWindowTheSizeOfItsPackets()
        {
            using FakeLink link = new();
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 9, 10_000_000);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "q", 9, 10_000_000);
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    sender.Transmit(new byte[50_000]);
                }
                AdaptiveWindow window = (AdaptiveWindow)typeof(SRTPClient).GetField("DelayWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sender)!;
                int average = (int)typeof(AdaptiveWindow).GetField("AveragePacketBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Assert.InRange(average, 49_000, 50_100);
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        [Fact]
        public void ClientStartsWithTheMinimumWindow()
        {
            using FakeLink link = new();
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "q", 9, 10_000_000);
            SRTPClient small = new(link.A, link.B.LocalModemAddress!, "q", 10, 10_000_000) { MaxUnackedPackets = 8 };
            try
            {
                PropertyInfo window = typeof(SRTPClient).GetProperty("CurrentWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.Equal(AdaptiveWindow.MinimumWindow, window.GetValue(client));
                Assert.Equal(8, window.GetValue(small));
            }
            finally
            {
                client.Close();
                small.Close();
            }
        }
    }
}
