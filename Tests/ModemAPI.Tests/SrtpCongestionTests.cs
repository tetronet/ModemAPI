using System.Reflection;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// The packet loss based congestion control halves the window once per loss event: not again for the losses of packets
    /// sent with the old window, but for every new event, however soon it comes.
    /// </summary>
    public class SrtpCongestionTests
    {
        private static double Power(SRTPClient client) =>
            (double)typeof(SRTPClient).GetField("ThrottlingPower", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

        private static void Lost(SRTPClient client, long id) =>
            typeof(SRTPClient).GetMethod("HalveWindowOnLoss", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client, [id]);

        [Fact]
        public void WindowIsHalvedOncePerLossEvent()
        {
            using FakeLink link = new();
            // a long timeout: halving once per timeout would skip the second event
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 9, 100_000_000) { PacketLossBasedCongestionControl = true };
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "q", 9, 100_000_000);
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    sender.Transmit([(byte)i]);
                }
                sender.WaitForAcknowledgements();

                double before = Power(sender);
                Lost(sender, 3);
                Assert.Equal(before / 2, Power(sender), 12);
                // sent before the halving: the same event
                Lost(sender, 7);
                Lost(sender, 9);
                Assert.Equal(before / 2, Power(sender), 12);

                for (int i = 0; i < 5; i++)
                {
                    sender.Transmit([(byte)i]);
                }
                sender.WaitForAcknowledgements();
                // sent after the halving: a new event, right away
                double grown = Power(sender);
                Lost(sender, 12);
                Assert.Equal(grown / 2, Power(sender), 12);
                Lost(sender, 14);
                Assert.Equal(grown / 2, Power(sender), 12);
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        [Fact]
        public void WithoutCongestionControlTheWindowStays()
        {
            using FakeLink link = new();
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "q", 9, 100_000_000);
            try
            {
                double before = Power(sender);
                Lost(sender, 0);
                Assert.Equal(before, Power(sender));
            }
            finally
            {
                sender.Close();
            }
        }
    }
}
