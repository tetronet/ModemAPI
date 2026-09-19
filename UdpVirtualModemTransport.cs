using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Stands as a client to replace socket.io in high speed applications.
    /// </summary>
    /// <typeparam name="T">Any serializable object</typeparam>
    internal class UdpVirtualModemTransport : IDisposable
    {
        private ulong Sid = 0;
        private UdpClient Client;
        private long LastKeepaliveReceived = DateTime.Now.Ticks;
        /// <summary>
        /// Gets called for each datagram that flies into this thing.
        /// </summary>
        public Action<byte[]> OnMessageReceived = delegate { };
        /// <summary>
        /// Gets called if something dies. It must return a boolean value. True if you want to continue and false if you want to stop.
        /// </summary>
        public Func<Exception, bool> OnError = delegate { return true; };
        /// <summary>
        /// True, if this <seealso cref="UdpVirtualModemTransport"/> is connected, and false if it's not.
        /// </summary>
        public bool Connected { get; private set; }
        public UdpVirtualModemTransport(IPEndPoint cias)
        {
            Client = new();
            Client.Connect(cias);
            // read udp socket
            Task.Run(async delegate ()
            {
                while (true)
                {
                    try
                    {
                        UdpReceiveResult result = await Client.ReceiveAsync();
                        // data packet handler
                        if (result.Buffer.Length >= 2 && result.Buffer[0] == 0x25 && result.Buffer[1] == 0x50)
                        {
                            OnMessageReceived(result.Buffer[2..]);
                        }
                        // keepalive handler
                        if (result.Buffer.Length >= 2 && result.Buffer[0] == 0x63 && result.Buffer[1] == 0x50)
                        {
                            Client.Send([0x63, 0xFF]);
                        }
                    }
                    catch (Exception e)
                    {
                        if (!OnError(e))
                        {
                            break;
                        }
                    }
                }
            });
        }
        /// <summary>
        /// Writes a message to the network.
        /// </summary>
        /// <param name="data">Message's payload</param>
        public void WriteObject(byte[] data)
        {
            if (!Connected)
            {
                return;
            }
            byte[] tempBuffer = new byte[data.Length + 2];
            data.CopyTo(tempBuffer, 2);
            BinaryPrimitives.WriteUInt16BigEndian(tempBuffer, 0x2550);
            Client.Send(tempBuffer);
        }
        /// <summary>
        /// Makes the server know that we exist. Packet could be lost.
        /// </summary>
        public void Connect()
        {
            // make so that the server will know that we exist
            Span<byte> request = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(request, 0x1536);
            Sid = (ulong)Random.Shared.NextInt64();
            BinaryPrimitives.WriteUInt64BigEndian(request[2..], Sid);
            for (int i = 0; i < 20; i++)
            {
                Client.Send(request);
            }
            Connected = true;
        }
        public void Dispose()
        {
            Span<byte> request = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(request, 0x3615);
            BinaryPrimitives.WriteUInt64BigEndian(request[2..], Sid);
            for (int i = 0; i < 20; i++)
            {
                Client.Send(request);
            }
            Connected = false;
            Client.Dispose();
        }
        /// <summary>
        /// Perfomrs operations to disconnect this client. Packets could be lost.
        /// </summary>
        public void Disconnect()
        {
            Span<byte> request = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(request, 0x3615);
            BinaryPrimitives.WriteUInt64BigEndian(request[2..], Sid);
            for (int i = 0; i < 20; i++)
            {
                Client.Send(request);
            }
            Connected = false;
        }
    }
}
