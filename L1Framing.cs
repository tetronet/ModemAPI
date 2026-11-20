using System.Net.Sockets;

namespace ModemAPI
{
    /// <summary>
    /// Frames of the TCP level one wire of the low latency modems: 3 bytes of big-endian length, then the data.
    /// One implementation for both modems, so reading and writing can't disagree.
    /// The UDP wire has no frames: every datagram is one whole packet.
    /// </summary>
    internal static class L1Framing
    {
        public const int HeaderLength = 3;
        public const int MaxFrameLength = 0xFFFFFF;
        /// <summary>
        /// The most data one IPv4 UDP datagram can carry (65535 - 20 bytes of IP header - 8 bytes of UDP header).
        /// A tetronet packet of the biggest SRTP message is about 60 KB, so it fits.
        /// </summary>
        public const int MaxDatagramLength = 65507;
        /// <summary>
        /// Receive and send buffers of a UDP line socket. The default (64 KB on Windows) holds about 45 datagrams of 1400 bytes:
        /// a burst overflows it while the reader thread waits for the core, and the datagrams are lost (13% at 300 Mbit/s with
        /// one core for each bridge; 0.1% with these buffers). On Linux the kernel cuts it to net.core.rmem_max and
        /// net.core.wmem_max (about 208 KB by default) without an error, so a busy server raises those with sysctl.
        /// </summary>
        public const int UdpSocketBufferSize = 16 * 1024 * 1024;

        /// <summary>
        /// Sets the buffers of a UDP line socket to <see cref="UdpSocketBufferSize"/>. A system, that refuses so big buffers
        /// (macOS above kern.ipc.maxsockbuf), keeps its own: the line works anyway, only bursts are lost sooner.
        /// </summary>
        public static void SetUdpSocketBuffers(Socket socket)
        {
            try
            {
                socket.ReceiveBufferSize = UdpSocketBufferSize;
            }
            catch (SocketException)
            {
            }
            try
            {
                socket.SendBufferSize = UdpSocketBufferSize;
            }
            catch (SocketException)
            {
            }
        }

        /// <summary>
        /// Builds a frame: the length header followed by the data.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The data is longer than <see cref="MaxFrameLength"/></exception>
        public static byte[] Frame(byte[] data)
        {
            if (data.Length > MaxFrameLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), $"buffer bigger than {MaxFrameLength} bytes");
            }
            byte[] frame = new byte[HeaderLength + data.Length];
            frame[0] = (byte)(data.Length >> 16);
            frame[1] = (byte)(data.Length >> 8);
            frame[2] = (byte)data.Length;
            data.CopyTo(frame, HeaderLength);
            return frame;
        }

        /// <summary>
        /// Takes every complete frame from the start of the buffer, leaving an incomplete one there for the next read.
        /// An exception of onFrame is passed to onError and doesn't stop the other frames.
        /// </summary>
        public static void ExtractFrames(List<byte> buffer, Action<byte[]> onFrame, Action<Exception> onError)
        {
            while (buffer.Count >= HeaderLength)
            {
                int length = buffer[0] << 16 | buffer[1] << 8 | buffer[2];
                if (buffer.Count < HeaderLength + length)
                {
                    return;
                }
                byte[] data = buffer.GetRange(HeaderLength, length).ToArray();
                buffer.RemoveRange(0, HeaderLength + length);
                try
                {
                    onFrame(data);
                }
                catch (Exception e)
                {
                    onError(e);
                }
            }
        }
    }
}
