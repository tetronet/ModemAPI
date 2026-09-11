using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace ModemAPI
{
    public class MoreStableSerialPacket : ISerialPacket
    {
        private Stream io;
        private readonly object _writeLock = new object();
        private Action<byte[], Action> OnReceive = delegate { };
        public event Action<L1DropBytesReasons, int> OnDataDropped = delegate { };
        public event Action MalformedPacketReceived = delegate { };
        public event Action<Exception, ErrorEmitter> InternalErrorHappened = delegate { };
        public event Action TextReaderReturnedNull = delegate { };
        public Stream UnderlyingStream { get { return io; } }

        public MoreStableSerialPacket(Stream io)
        {
            this.io = io;
            TextReader textReader = new StreamReader(io);
            Task.Run(delegate ()
            {
                while (true)
                {
                    try
                    {
                        string? base64encoded = textReader.ReadLine();
                        if (base64encoded == null)
                        {
                            TextReaderReturnedNull();
                            break;
                        }
                        byte[] packet = Convert.FromBase64String(base64encoded);
                        ModemAPIDebugger.OutputDebugMessage("Received data MSSP: " + base64encoded);
                        OnReceive(packet, delegate () { throw new NotImplementedException(); });
                    }
                    catch (FormatException fe)
                    {
                        Task.Run(() => MalformedPacketReceived());
                        InternalErrorHappened(fe, ErrorEmitter.Base64Converter);
                    }
                    catch (TimeoutException te)
                    {
                        InternalErrorHappened(te, ErrorEmitter.HardwareSerialPort);
                    }
                    catch (Exception e)
                    {
                        InternalErrorHappened(e, ErrorEmitter.MoreStableSerialPacket);
                    }
                }
            });
        }

        public void TransmitBuffer(byte[] buffer)
        {
            lock (_writeLock)
            {
                io.Write(Encoding.UTF8.GetBytes(Convert.ToBase64String(buffer)));
                io.Write([0x0A]);
                ModemAPIDebugger.OutputDebugMessage($"sent {buffer.Length} bytes of data");
            }
        }

        public void OnBufferReceived(Action<byte[], Action> onReceive)
        {
            ModemAPIDebugger.OutputDebugMessage("Subscribed to the serial buffer");
            OnReceive += onReceive;
        }
    }
}
