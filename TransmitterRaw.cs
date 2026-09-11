using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Stateless tetronet transmitter
    /// </summary>
    public class TransmitterRaw : ITransmitter
    {
        /// <summary>
        /// Inner modem for that transmitter
        /// </summary>
        public IModem Modem { get; }
        public long TransmitterTimeout { get; set; }

        public TransmitterRaw(IModem modem)
        {
            Modem = modem;
        }

        /// <summary>
        /// Attaches a new delegate event when a data packet is received by the modem
        /// </summary>
        /// <param name="onDataReceived">Delegate event</param>

        public void AttachReceiveEvent(Action<DataBlock, Action> onDataReceived)
        {
            Modem.AttachReceiveEvent(onDataReceived);
        }
        /// <summary>
        /// Transmits data to the tetronet
        /// </summary>
        /// <param name="data">Data for transmitting to the tetronet</param>
        /// <param name="receiver">Destination tetronet address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectid">Tetronet Connection ID (like ports in IP)</param>
        /// <param name="metadata">Packet metadata</param>
        /// <param name="transmissionDelay">Delay between transmitting packets of this block</param>
        public void Transmit(string data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            Modem.Transmit(data, receiver, queryType, connectid, metadata, 1024, transmissionDelay);
        }
        /// <summary>
        /// Transmits data to the tetronet
        /// </summary>
        /// <param name="data">Data for transmitting to the tetronet</param>
        /// <param name="receiver">Destination tetronet address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectid">Tetronet Connection ID (like ports in IP)</param>
        /// <param name="metadata">Packet metadata</param>
        /// <param name="transmissionDelay">Delay between transmitting packets of this block</param>
        public void Transmit(byte[] data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            Modem.Transmit(data, receiver, queryType, connectid, metadata, 1024, transmissionDelay);
        }
    }
}
