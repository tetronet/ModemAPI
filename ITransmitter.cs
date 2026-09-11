using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public partial interface ITransmitter
    {
        IModem Modem { get; }
        void Transmit(string data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0);
        void Transmit(byte[] data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0);
        void AttachReceiveEvent(Action<DataBlock, Action> onDataReceived);
    }
}
