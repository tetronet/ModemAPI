using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ModemAPI
{
    /// <summary>
    /// Many datagrams per system call on 64-bit Linux: recvmmsg and sendmmsg. The datagrams on the line are the same as with
    /// Socket.Receive and Socket.Send, only fewer calls into the kernel carry them. A system call is expensive there: on an old
    /// Xeon with PTI an address machine of two answering modems on one core spent 23-25% less CPU on 15-45k packets/s of 1400
    /// bytes, and carried 60k packets/s instead of choking at 56k. The reader takes every datagram, that has come, with one
    /// call; the sender thread sends what it found queued with one call. Other systems don't have these calls, so
    /// <see cref="IsSupported"/> is false and the modems read and write one datagram per call.
    /// </summary>
    internal static class UdpBatch
    {
        // struct iovec, struct msghdr and struct mmsghdr of 64-bit Linux (x64 and arm64 have the same layouts)
        [StructLayout(LayoutKind.Sequential)]
        private struct IoVec
        {
            public IntPtr Base;
            public nuint Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsgHdr
        {
            public IntPtr Name;
            public uint NameLength;
            public IntPtr Iov;
            public nuint IovLength;
            public IntPtr Control;
            public nuint ControlLength;
            public int Flags;
        }

        // the length is after the padded msghdr (offset 56), so msghdr is a struct of its own, not fields
        [StructLayout(LayoutKind.Sequential)]
        private struct MMsgHdr
        {
            public MsgHdr Header;
            // received or sent bytes of this message
            public uint Length;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int recvmmsg(int fd, IntPtr msgs, uint count, int flags, IntPtr timeout);

        [DllImport("libc", SetLastError = true)]
        private static extern int sendmmsg(int fd, IntPtr msgs, uint count, int flags);

        // return as soon as one datagram is there, with all the others, that are already there
        private const int MSG_WAITFORONE = 0x10000;
        private const int EINTR = 4;
        // sockaddr_storage: room for the address of any family
        private const int NameSize = 128;

        // the layouts above are those of 64-bit Linux: struct mmsghdr is 64 bytes there
        public static bool IsSupported { get; } = OperatingSystem.IsLinux() && Environment.Is64BitProcess && Marshal.SizeOf<MMsgHdr>() == 64;

        /// <summary>
        /// The exception, that Socket.Receive or Socket.Send throws for that errno: SocketException takes the codes of Windows
        /// (10061 and so on), and a raw errno there gives a wrong <see cref="SocketException.SocketErrorCode"/>.
        /// </summary>
        private static SocketException Error(int errno)
        {
            SocketError code = errno switch
            {
                11 => SocketError.WouldBlock, // EAGAIN
                22 => SocketError.InvalidArgument, // EINVAL
                90 => SocketError.MessageSize, // EMSGSIZE
                100 => SocketError.NetworkDown, // ENETDOWN
                101 => SocketError.NetworkUnreachable, // ENETUNREACH
                104 => SocketError.ConnectionReset, // ECONNRESET
                105 => SocketError.NoBufferSpaceAvailable, // ENOBUFS
                110 => SocketError.TimedOut, // ETIMEDOUT
                111 => SocketError.ConnectionRefused, // ECONNREFUSED: ICMP "port unreachable" of an earlier datagram
                112 => SocketError.HostDown, // EHOSTDOWN
                113 => SocketError.HostUnreachable, // EHOSTUNREACH
                _ => SocketError.SocketError,
            };
            return new SocketException((int)code, $"{Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
        }

        private static IntPtr AddressOf<T>(T[] pinned, int index) where T : struct
        {
            return Marshal.UnsafeAddrOfPinnedArrayElement(pinned, index);
        }

        /// <summary>
        /// recvmmsg on the descriptor of the socket. The descriptor can't be closed and reused for another socket during the call
        /// (Socket.Dispose during a blocking call shuts the socket down first, so the call returns, and closes it after).
        /// </summary>
        /// <exception cref="ObjectDisposedException">The socket is closed</exception>
        private static int ReceiveMessages(Socket socket, IntPtr messages, uint count)
        {
            SafeSocketHandle handle = socket.SafeHandle;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                return recvmmsg((int)handle.DangerousGetHandle(), messages, count, MSG_WAITFORONE, IntPtr.Zero);
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }

        /// <summary>
        /// sendmmsg on the descriptor of the socket, kept from being closed during the call as in <see cref="ReceiveMessages"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The socket is closed</exception>
        private static int SendMessages(Socket socket, IntPtr messages, uint count)
        {
            SafeSocketHandle handle = socket.SafeHandle;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                return sendmmsg((int)handle.DangerousGetHandle(), messages, count, 0);
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }

        /// <summary>
        /// Receives up to <see cref="Slots"/> datagrams per call into buffers of its own; one reader thread uses it.
        /// </summary>
        public sealed class Receiver
        {
            private const int Slots = 16;
            // a datagram can't be longer, so it is never cut
            private const int SlotSize = ushort.MaxValue + 1;
            private readonly byte[] Buffer = GC.AllocateUninitializedArray<byte>(Slots * SlotSize, pinned: true);
            private readonly IoVec[] Iov = GC.AllocateArray<IoVec>(Slots, pinned: true);
            private readonly MMsgHdr[] Messages = GC.AllocateArray<MMsgHdr>(Slots, pinned: true);
            private readonly byte[]? Names;

            /// <param name="withSources">Whether the source address of every datagram is needed (an unconnected socket)</param>
            public Receiver(bool withSources)
            {
                if (withSources)
                {
                    Names = GC.AllocateUninitializedArray<byte>(Slots * NameSize, pinned: true);
                }
                for (int i = 0; i < Slots; i++)
                {
                    Iov[i].Base = AddressOf(Buffer, i * SlotSize);
                    Iov[i].Length = SlotSize;
                    Messages[i].Header.Iov = AddressOf(Iov, i);
                    Messages[i].Header.IovLength = 1;
                }
            }

            /// <summary>
            /// Waits for a datagram and takes it together with every other one, that has already come.
            /// </summary>
            /// <returns>How many datagrams were received. Socket.Dispose shuts a blocked socket down first: then it is one empty
            /// datagram, and the next call throws <see cref="ObjectDisposedException"/></returns>
            /// <exception cref="SocketException">For example ICMP "port unreachable" of an earlier datagram; the socket still works</exception>
            /// <exception cref="ObjectDisposedException">The socket is closed</exception>
            public int Receive(Socket socket)
            {
                for (int i = 0; i < Slots; i++)
                {
                    if (Names != null)
                    {
                        Messages[i].Header.Name = AddressOf(Names, i * NameSize);
                        Messages[i].Header.NameLength = NameSize;
                    }
                    Messages[i].Header.Flags = 0;
                }
                while (true)
                {
                    int received = ReceiveMessages(socket, AddressOf(Messages, 0), Slots);
                    if (received >= 0)
                    {
                        return received;
                    }
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EINTR)
                    {
                        throw Error(error);
                    }
                }
            }

            /// <summary>
            /// The datagram number <paramref name="index"/> of the last <see cref="Receive"/>, valid until the next one.
            /// </summary>
            public ReadOnlySpan<byte> Datagram(int index)
            {
                return Buffer.AsSpan(index * SlotSize, (int)Messages[index].Length);
            }

            /// <summary>
            /// Writes the source of the datagram number <paramref name="index"/> into the address (a receiver with sources only).
            /// </summary>
            public void CopySource(int index, SocketAddress to)
            {
                int length = (int)Math.Min(Messages[index].Header.NameLength, (uint)to.Buffer.Length);
                Names.AsSpan(index * NameSize, length).CopyTo(to.Buffer.Span);
                to.Size = length;
            }
        }

        /// <summary>
        /// Collects datagrams and sends them with as few calls as possible; one sender thread uses it.
        /// </summary>
        public sealed class Sender
        {
            private const int Slots = 32;
            // a whole datagram of the biggest size always fits into an empty buffer
            private const int BufferSize = 256 * 1024;
            private readonly byte[] Buffer = GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
            private readonly IoVec[] Iov = GC.AllocateArray<IoVec>(Slots, pinned: true);
            private readonly MMsgHdr[] Messages = GC.AllocateArray<MMsgHdr>(Slots, pinned: true);
            private readonly byte[] Name = GC.AllocateUninitializedArray<byte>(NameSize, pinned: true);
            private int Count = 0;
            private int Used = 0;

            public Sender()
            {
                for (int i = 0; i < Slots; i++)
                {
                    Messages[i].Header.Iov = AddressOf(Iov, i);
                    Messages[i].Header.IovLength = 1;
                }
            }

            public bool IsEmpty => Count == 0;

            /// <summary>
            /// Forgets the collected datagrams without sending them.
            /// </summary>
            public void Clear()
            {
                Count = 0;
                Used = 0;
            }

            /// <summary>
            /// Room for the next datagram of that size, or an empty span, when the collected ones must be sent first.
            /// </summary>
            public Span<byte> Reserve(int length)
            {
                if (Count == Slots || BufferSize - Used < length)
                {
                    return [];
                }
                return Buffer.AsSpan(Used, length);
            }

            /// <summary>
            /// Adds the datagram written into the last <see cref="Reserve"/>.
            /// </summary>
            public void Commit(int length)
            {
                Iov[Count].Base = AddressOf(Buffer, Used);
                Iov[Count].Length = (nuint)length;
                Count++;
                Used += length;
            }

            /// <summary>
            /// Sends every collected datagram, to the address or, without one, where the socket is connected. A datagram, that the
            /// system refuses, is reported and the next ones are sent anyway, as when every datagram had a Socket.Send of its own.
            /// </summary>
            public void Send(Socket socket, SocketAddress? to, Action<Exception> onError)
            {
                uint nameLength = 0;
                if (to != null)
                {
                    nameLength = (uint)Math.Min(to.Size, NameSize);
                    to.Buffer.Span[..(int)nameLength].CopyTo(Name);
                }
                for (int i = 0; i < Count; i++)
                {
                    Messages[i].Header.Name = to != null ? AddressOf(Name, 0) : IntPtr.Zero;
                    Messages[i].Header.NameLength = nameLength;
                    Messages[i].Header.Flags = 0;
                }
                int done = 0;
                try
                {
                    while (done < Count)
                    {
                        int sent = SendMessages(socket, AddressOf(Messages, done), (uint)(Count - done));
                        if (sent > 0)
                        {
                            done += sent;
                            continue;
                        }
                        int error = Marshal.GetLastPInvokeError();
                        if (sent < 0 && error == EINTR)
                        {
                            continue;
                        }
                        // the first datagram of the rest can't be sent: it is lost, the others still go
                        onError(Error(error));
                        done++;
                    }
                }
                finally
                {
                    Clear();
                }
            }
        }
    }
}
