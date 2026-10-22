using System.Collections.Concurrent;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// SRTP over a link, that loses, repeats and reorders packets or goes down. Every scenario must end in one of two ways:
    /// all messages are delivered once and in order and the sender sees them acknowledged, or the sender reports an error.
    /// Hanging is a failure. Scenarios run with v2 (batched and selective acknowledgements) and with v1, which both sides
    /// fall back to when the metadata doesn't get through.
    /// </summary>
    public class SrtpFaultTests
    {
        private const int RetransmitTimeoutTicks = 2_000_000; // 200 ms
        private static readonly TimeSpan TransferLimit = TimeSpan.FromSeconds(60);

        private sealed record Transfer(List<byte[]> Received, Exception? SenderError);

        /// <summary>
        /// Sends messages from A to B and waits until the sender sees all of them acknowledged.
        /// </summary>
        private static Transfer Run(FakeLink link, List<byte[]> messages, Action<SRTPClient>? configureSender = null, Action<SRTPClient, int>? afterEachMessage = null)
        {
            SRTPClient sender = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, RetransmitTimeoutTicks);
            SRTPClient receiver = new(link.B, link.A.LocalModemAddress!, "srtp-test", 7, RetransmitTimeoutTicks);
            List<byte[]> received = [];
            receiver.OnMessageReceived = (client, data) => { lock (received) received.Add(data); };
            configureSender?.Invoke(sender);
            try
            {
                Exception? error = Guard.RunWithin(TransferLimit, () =>
                {
                    for (int i = 0; i < messages.Count; i++)
                    {
                        sender.Transmit(messages[i]);
                        afterEachMessage?.Invoke(receiver, i);
                    }
                    sender.WaitForAcknowledgements();
                });
                // the receiver may still be delivering the last messages
                if (error == null)
                {
                    SpinWait.SpinUntil(() => { lock (received) return received.Count >= messages.Count; }, TimeSpan.FromSeconds(5));
                }
                lock (received) return new Transfer([.. received], error);
            }
            finally
            {
                sender.Close();
                receiver.Close();
            }
        }

        private static List<byte[]> Messages(int count, int seed, int maxLength = 3000)
        {
            Random random = new(seed);
            List<byte[]> messages = [];
            for (int i = 0; i < count; i++)
            {
                byte[] data = new byte[random.Next(1, maxLength)];
                random.NextBytes(data);
                messages.Add(data);
            }
            return messages;
        }

        private static void AssertDeliveredExactlyOnceInOrder(List<byte[]> sent, Transfer transfer)
        {
            Assert.Null(transfer.SenderError);
            Assert.Equal(sent.Count, transfer.Received.Count);
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.True(sent[i].AsSpan().SequenceEqual(transfer.Received[i]), $"message {i} differs");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NoFaults(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            List<byte[]> messages = Messages(500, 1);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AcknowledgementLoss(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            SharedRandom random = new(2);
            link.BToA = p => FakeLink.IsAck(p) && random.NextDouble() < 0.3 ? Fault.Drop : Fault.Deliver;
            List<byte[]> messages = Messages(500, 2);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RepeatedLossOfTheSameMessage(bool v1)
        {
            // the first three copies of these messages are lost, retransmissions included
            using FakeLink link = new() { StripMetadata = v1 };
            long[] unlucky = [0, 57, 58, 199];
            ConcurrentDictionary<long, int> copies = new();
            link.AToB = p =>
            {
                if (!FakeLink.IsData(p) || !unlucky.Contains(FakeLink.SequenceOf(p))) return Fault.Deliver;
                return copies.AddOrUpdate(FakeLink.SequenceOf(p), 1, (_, n) => n + 1) <= 3 ? Fault.Drop : Fault.Deliver;
            };
            List<byte[]> messages = Messages(200, 3);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
            Assert.All(unlucky, id => Assert.True(copies[id] >= 4));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reordering(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            SharedRandom random = new(4);
            link.AToB = p => random.NextDouble() < 0.2 ? Fault.Swap : Fault.Deliver;
            link.BToA = p => random.NextDouble() < 0.2 ? Fault.Swap : Fault.Deliver;
            List<byte[]> messages = Messages(500, 4);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Duplication(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            SharedRandom random = new(5);
            link.AToB = p => random.NextDouble() < 0.2 ? Fault.Duplicate : Fault.Deliver;
            link.BToA = p => random.NextDouble() < 0.2 ? Fault.Duplicate : Fault.Deliver;
            List<byte[]> messages = Messages(500, 5);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
        }

        [Theory]
        [InlineData(false, 6)]
        [InlineData(false, 7)]
        [InlineData(false, 8)]
        [InlineData(true, 6)]
        [InlineData(true, 7)]
        public void EverythingAtOnce(bool v1, int seed)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            SharedRandom random = new(seed);
            Fault Chaos(Packet p)
            {
                double roll = random.NextDouble();
                return roll < 0.1 ? Fault.Drop : roll < 0.15 ? Fault.Duplicate : roll < 0.2 ? Fault.Swap : Fault.Deliver;
            }
            link.AToB = Chaos;
            link.BToA = Chaos;
            List<byte[]> messages = Messages(400, seed);
            AssertDeliveredExactlyOnceInOrder(messages, Run(link, messages));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LinkGoesDownInTheMiddle(bool v1)
        {
            using FakeLink link = new() { StripMetadata = v1 };
            List<byte[]> messages = Messages(3000, 9);
            Transfer transfer = Run(link, messages,
                sender => sender.RetransmissionsBeforeConsideringRemoteSideDead = 5,
                (receiver, i) => { if (i == 300) link.Down = true; });
            Assert.NotNull(transfer.SenderError);
            Assert.True(transfer.SenderError is OperationCanceledException or TimeoutException or InvalidOperationException, transfer.SenderError.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AcknowledgementsNeverArrive(bool v1)
        {
            // the data gets through, but no acknowledgement does: the sender must not report success
            using FakeLink link = new() { StripMetadata = v1 };
            link.BToA = p => FakeLink.IsAck(p) ? Fault.Drop : Fault.Deliver;
            List<byte[]> messages = Messages(20, 10);
            Transfer transfer = Run(link, messages, sender => sender.RetransmissionsBeforeConsideringRemoteSideDead = 5);
            Assert.NotNull(transfer.SenderError);
            Assert.True(transfer.SenderError is OperationCanceledException or TimeoutException or InvalidOperationException, transfer.SenderError.ToString());
        }

        [Fact]
        public void ReceiverClosesInTheMiddle()
        {
            using FakeLink link = new();
            List<byte[]> messages = Messages(3000, 11);
            Transfer transfer = Run(link, messages,
                sender => sender.RetransmissionsBeforeConsideringRemoteSideDead = 5,
                (receiver, i) => { if (i == 300) receiver.Close(); });
            Assert.NotNull(transfer.SenderError);
        }

        [Fact]
        public void TransmitTimesOutWhenTheWindowNeverOpens()
        {
            // with retransmissions effectively unlimited, the window timeout is what ends the wait
            using FakeLink link = new() { Down = true };
            List<byte[]> messages = Messages(50, 12);
            Transfer transfer = Run(link, messages, sender =>
            {
                sender.MaxUnackedPackets = 8;
                sender.RetransmissionsBeforeConsideringRemoteSideDead = int.MaxValue;
                sender.TransmitTimeOutInTicks = 20_000_000; // 2 s
            });
            Assert.IsType<TimeoutException>(transfer.SenderError);
        }

        [Fact]
        public void ClosedClientUnsubscribesFromTheModem()
        {
            using FakeLink link = new();
            int before = link.A.SubscriberCount;
            SRTPClient client = new(link.A, link.B.LocalModemAddress!, "srtp-test", 7, RetransmitTimeoutTicks);
            Assert.Equal(before + 1, link.A.SubscriberCount);
            client.Close();
            client.Close(); // closing twice is allowed
            Assert.Equal(before, link.A.SubscriberCount);
        }
    }
}
