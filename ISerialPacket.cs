using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Base interface for implementing Serial Packetizer machines.
    /// </summary>
    public interface ISerialPacket
    {
        Stream UnderlyingStream { get; }
        /// <summary>
        /// Sends a buffer to the serial connection.
        /// </summary>
        /// <param name="buffer">Byte array, that may contain arbitrary many bytes with value from 0 to 255</param>
        void TransmitBuffer(byte[] buffer);
        /// <summary>
        /// Adds an event, that will listen to any incoming serial packets.
        /// </summary>
        /// <param name="onReceive">Delegate, that must be called once all symbols from the sent buffer are fully received</param>
        void OnBufferReceived(Action<byte[], Action> onReceive);
    }
}
