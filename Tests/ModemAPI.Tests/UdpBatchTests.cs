using System.Net;
using System.Net.Sockets;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// recvmmsg and sendmmsg of the UDP lines. They only exist on 64-bit Linux: elsewhere the modems read and write one datagram
    /// per call, and these tests have nothing to check.
    /// </summary>
    public class UdpBatchTests
    {
        private static byte[] Payload(int index, int length)
        {
            byte[] bytes = new byte[length];
            new Random(index).NextBytes(bytes);
            return bytes;
        }

        private static Socket Bound()
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        [Fact]
        public void DatagramsOfEverySizeComeWholeInOrderAndFromTheSender()
        {
            if (!UdpBatch.IsSupported)
            {
                return;
            }
            using Socket rx = Bound();
            using Socket tx = Bound();
            // in groups, that fit a receive buffer cut to 208 KB (net.core.rmem_max by default): the biggest datagram, several,
            // that overflow the 256 KB buffer of the sender together, more datagrams than the 32 slots of one sendmmsg
            int[][] groups = [[1, 2, 1400, 65507, 7], [60000, 60000], [60000, 60000], [.. Enumerable.Range(0, 70).Select(i => 100 + i * 23)]];
            UdpBatch.Sender sender = new();
            UdpBatch.Receiver receiver = new(withSources: true);
            SocketAddress to = rx.LocalEndPoint!.Serialize();
            SocketAddress txAddress = tx.LocalEndPoint!.Serialize();
            SocketAddress from = new(AddressFamily.InterNetwork);
            List<Exception> errors = [];
            int index = 0;
            foreach (int[] group in groups)
            {
                int first = index;
                foreach (int size in group)
                {
                    Span<byte> room = sender.Reserve(size);
                    if (room.IsEmpty)
                    {
                        sender.Send(tx, to, errors.Add);
                        room = sender.Reserve(size);
                    }
                    Payload(index, size).CopyTo(room);
                    sender.Commit(size);
                    index++;
                }
                sender.Send(tx, to, errors.Add);
                int expected = first;
                while (expected < index)
                {
                    Assert.True(rx.Poll(2_000_000, SelectMode.SelectRead), $"datagram {expected} didn't come");
                    int count = receiver.Receive(rx);
                    for (int i = 0; i < count; i++, expected++)
                    {
                        Assert.True(receiver.Datagram(i).SequenceEqual(Payload(expected, group[expected - first])), $"datagram {expected} is damaged");
                        receiver.CopySource(i, from);
                        Assert.Equal(txAddress, from);
                    }
                }
            }
            Assert.Empty(errors);
        }

        [Fact]
        public void DisposeWakesABlockedReceiverAndItStops()
        {
            if (!UdpBatch.IsSupported)
            {
                return;
            }
            Socket socket = Bound();
            UdpBatch.Receiver receiver = new(withSources: false);
            Exception? thrown = null;
            int count = -1;
            int length = -1;
            Thread reader = new(() =>
            {
                try
                {
                    count = receiver.Receive(socket);
                    length = count == 1 ? receiver.Datagram(0).Length : -1;
                    receiver.Receive(socket);
                }
                catch (Exception e)
                {
                    thrown = e;
                }
            });
            reader.Start();
            Thread.Sleep(200);
            socket.Dispose();
            Assert.True(reader.Join(3000), "the reader stays blocked after Dispose");
            // Dispose shuts the socket down first: the blocked call gets one empty datagram, and the next one finds it closed
            Assert.IsType<ObjectDisposedException>(thrown);
            Assert.True(count == -1 || (count == 1 && length == 0), $"Receive returned {count} datagrams of {length} bytes");
        }

        [Fact]
        public void PortUnreachableIsAnErrorOfOneCallAndReceivingGoesOn()
        {
            if (!UdpBatch.IsSupported)
            {
                return;
            }
            Socket closed = Bound();
            EndPoint deadEnd = closed.LocalEndPoint!;
            closed.Dispose();
            using Socket socket = Bound();
            socket.Connect(deadEnd);
            UdpBatch.Sender sender = new();
            sender.Reserve(10);
            sender.Commit(10);
            List<Exception> errors = [];
            sender.Send(socket, null, errors.Add);
            Thread.Sleep(100);
            UdpBatch.Receiver receiver = new(withSources: false);
            SocketException error = Assert.Throws<SocketException>(() => receiver.Receive(socket));
            Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
            using Socket peer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            peer.Bind(deadEnd);
            peer.SendTo([1, 2, 3], socket.LocalEndPoint!);
            Assert.True(socket.Poll(2_000_000, SelectMode.SelectRead));
            Assert.Equal(1, receiver.Receive(socket));
            Assert.True(receiver.Datagram(0).SequenceEqual(new byte[] { 1, 2, 3 }));
        }
    }
}
