using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class Transmitter(TransmitterTypes type, IModem modem)
    {
        private ITransmitter? InnerTransmitter; 
        protected IModem Modem = modem;
        protected TransmitterTypes Type = type;
        public long TransmitterTimeout
        {
            get
            {
                if (InnerTransmitter == null)
                {
                    throw new NullTransmitterException("InnerTransmitter was null");
                }
                return InnerTransmitter.TransmitterTimeout;
            }
            set
            {
                if (InnerTransmitter == null)
                {
                    throw new NullTransmitterException("InnerTransmitter was null");
                }
                InnerTransmitter.TransmitterTimeout = value;
            }
        }

        public void EnableTransmitter()
        {
            switch (Type)
            {
                case TransmitterTypes.None:
                    throw new InvalidOperationException("transmitter enable error: TransmitterTypes.None cannot be used as a transmitter type");

                case TransmitterTypes.TNET_RAW:
                    ModemAPIDebugger.OutputDebugMessage("set type as tnet raw");
                    InnerTransmitter = new TransmitterRaw(Modem);
                    break;

                case TransmitterTypes.TNET_ACK:
                    ModemAPIDebugger.OutputDebugMessage("set type as tnet ack");
                    InnerTransmitter = new TransmitterAcked(Modem);
                    break;
            }
        }

        public void Transmit(string data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            if (InnerTransmitter == null)
            {
                throw new NullTransmitterException("InnerTransmitter was null");
            }
            InnerTransmitter.Transmit(data, receiver, queryType, connectid, metadata, transmissionDelay);
        }

        public void Transmit(byte[] data, Address receiver, string queryType, uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            if (InnerTransmitter == null)
            {
                throw new NullTransmitterException("InnerTransmitter was null");
            }
            InnerTransmitter.Transmit(data, receiver, queryType, connectid, metadata, transmissionDelay);
        }

        public void AttachReceiveEvent(Action<DataBlock, Action> onRx)
        {
            if (InnerTransmitter == null)
            {
                throw new NullTransmitterException("InnerTransmitter was null");
            }
            InnerTransmitter.AttachReceiveEvent(onRx);
        }
    }
}
