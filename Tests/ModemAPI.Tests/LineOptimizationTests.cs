using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// OPTIMIZATION.md (UDP bridges): the sender thread of the LowLatency modems sleeps on <see cref="SenderWakeup"/> instead of
    /// a Task for every wake-up, and the UDP line sockets get big buffers.
    /// </summary>
    public class LineOptimizationTests
    {
        private static int FreeUdpPort()
        {
            using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        /// <summary>
        /// A sender loop as in the modems: sends everything queued, then sleeps until the next packet.
        /// </summary>
        private static Thread StartSender(Channel<int> queue, SenderWakeup wakeup, Action<int> onItem)
        {
            Thread sender = new(() =>
            {
                while (true)
                {
                    while (queue.Reader.TryRead(out int item))
                    {
                        onItem(item);
                    }
                    wakeup.Wait(queue.Reader);
                }
            })
            { IsBackground = true };
            sender.Start();
            return sender;
        }

        [Fact]
        public void OnePacketAtATimeIsNeverLeftInTheQueue()
        {
            // the writer waits for every packet to be taken before the next one: a lost wake-up leaves a packet in the queue forever
            Channel<int> queue = Channel.CreateBounded<int>(new BoundedChannelOptions(1024) { SingleReader = true });
            SenderWakeup wakeup = new();
            using SemaphoreSlim taken = new(0);
            StartSender(queue, wakeup, _ => taken.Release());
            for (int i = 0; i < 20000; i++)
            {
                Assert.True(queue.Writer.TryWrite(i));
                wakeup.Notify();
                Assert.True(taken.Wait(TimeSpan.FromSeconds(5)), $"packet {i} stayed in the queue");
            }
        }

        [Fact]
        public void PacketsOfManyWritersAllGoOut()
        {
            Channel<int> queue = Channel.CreateBounded<int>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            SenderWakeup wakeup = new();
            const int Writers = 8;
            const int PerWriter = 20000;
            int sent = 0;
            using ManualResetEventSlim allSent = new(false);
            StartSender(queue, wakeup, _ =>
            {
                if (Interlocked.Increment(ref sent) == Writers * PerWriter)
                {
                    allSent.Set();
                }
            });
            Parallel.For(0, Writers, new ParallelOptions { MaxDegreeOfParallelism = Writers }, w =>
            {
                for (int i = 0; i < PerWriter; i++)
                {
                    // as Transmit with DropPacketsWhenOverloaded = false: waits for space, then wakes the sender
                    if (!queue.Writer.TryWrite(i))
                    {
                        queue.Writer.WriteAsync(i).AsTask().GetAwaiter().GetResult();
                    }
                    wakeup.Notify();
                }
            });
            Assert.True(allSent.Wait(TimeSpan.FromSeconds(10)), $"only {Volatile.Read(ref sent)} of {Writers * PerWriter} packets went out");
        }

        [Fact]
        public void UdpLinesHaveBigBuffers()
        {
            int port = FreeUdpPort();
            LowLatencyAnsweringPhysicalModem answering = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP, "", false);
            LowLatencyPhysicalModem calling = new($"127.0.0.1:{port}", 0, L1Types.L1_INET_UDP);
            foreach (object modem in new object[] { answering, calling })
            {
                Socket socket = UdpSocketOf(modem);
                Assert.True(socket.ReceiveBufferSize >= L1Framing.UdpSocketBufferSize, $"{modem.GetType().Name}: receive buffer {socket.ReceiveBufferSize}");
                Assert.True(socket.SendBufferSize >= L1Framing.UdpSocketBufferSize, $"{modem.GetType().Name}: send buffer {socket.SendBufferSize}");
            }
        }

        private static Socket UdpSocketOf(object modem)
        {
            const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
            object communicator = modem.GetType().GetField("Communicator", Private)!.GetValue(modem)!;
            UdpClient udp = (UdpClient)communicator.GetType().GetField("udp", Private)!.GetValue(communicator)!;
            return udp.Client;
        }
    }
}
