using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;

namespace ModemAPI
{
    public class SerialPacket : ISerialPacket
    {
        SerialNetworkInterface io;
        private readonly TimeSpan BetweenByteReceiveTimeout = TimeSpan.FromSeconds(1);
        
        private readonly object _lock = new object();
        public event Action<L1DropBytesReasons, int> OnDataDropped = delegate { };
        public Stream UnderlyingStream { get { return io.Port.BaseStream; } }

        public SerialPacket(SerialPort io)
        {
            try
            {
                this.io = new SerialNetworkInterface(io);
            }
            catch (InvalidOperationException)
            {
                this.io = NetworkInterfaceLibrary.NetworkInterfaces.GetValueOrDefault(io.PortName) ?? throw new NullReferenceException("error while SerialPacket was init");
            }
        }

        public void TransmitBuffer(byte[] buffer)
        {
            
            if (buffer.Length > 0xFFFFFF)
            {
                throw new InvalidDataException("buffer length mustn't exceed 16777215 bytes");
            }

            // uint24 length (big-endian)
            byte len0 = (byte)((buffer.Length >> 16) & 0xFF);
            byte len1 = (byte)((buffer.Length >> 8) & 0xFF);
            byte len2 = (byte)(buffer.Length & 0xFF);

            List<byte> toTransmit = new()
            {
                0x55,
                len0,
                len1,
                len2
            };
            toTransmit.AddRange(buffer);
            //var t0 = Stopwatch.GetTimestamp();
            io.Write(toTransmit.ToArray());
            ModemAPIDebugger.OutputDebugMessage("trmit");
            ModemAPIDebugger.PrintByteArray([.. toTransmit]);
            //Console.WriteLine($"sent packet in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds}ms");
            //ModemAPIDebugger.PrintByteArray(buffer);
        }

        public void OnBufferReceived(Action<byte[], Action> onReceive)
        {
            List<byte> incomingBuffer = [];
            int incomingBufferLastSize = 0;
            Stopwatch receiveTimer = new();
            ModemAPIDebugger.OutputDebugMessage("Subscribed to the serial buffer");
            io.OnDataReceived(delegate (byte[] data)
            {
                lock (_lock)
                {
                    incomingBuffer.AddRange(data);
                    // the timeout thing
                    
                    // we got new data, so timeout resets
                    
                    ModemAPIDebugger.OutputDebugMessage($"RECEIVED {data.Length} BYTES OF DATA");
                    bool killerBool = false;
                    void killer()
                    {
                        killerBool = true;
                    }
                    ModemAPIDebugger.OutputDebugMessage($"1: INCOMING BUFFER COUNTS {incomingBuffer.Count} BYTES");
                    if (killerBool)
                    {
                        return;
                    }
                    //var t0 = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        //Console.WriteLine($"incoming buffer size: {incomingBuffer.Count}, growth: {incomingBuffer.Count - incomingBufferLastSize}");
                        incomingBufferLastSize = incomingBuffer.Count;
                        // atleast 4 bytes (sync and length)
                        if (incomingBuffer.Count < 4)
                        {
                            break;
                        }
                        receiveTimer.Restart();
                        if (incomingBuffer.Count > 0 &&
                            receiveTimer.Elapsed > BetweenByteReceiveTimeout)
                        {
                            ModemAPIDebugger.OutputDebugMessage(
                                $"Receive timeout ({BetweenByteReceiveTimeout.TotalSeconds}s, elapsed {receiveTimer.Elapsed}). Dropping {incomingBuffer.Count} buffered bytes.");
                            Task.Run(() => OnDataDropped(L1DropBytesReasons.DataTimedOut, incomingBuffer.Count));
                            incomingBuffer.Clear();
                        }
                        int syncIndex = incomingBuffer.IndexOf(0x55);
                        if (syncIndex == -1)
                        {
                            // no sync byte, drop everything cause it's shit
                            ModemAPIDebugger.OutputDebugMessage($"No sync byte, clearing {incomingBuffer.Count} bytes");
                            Task.Run(() => OnDataDropped(L1DropBytesReasons.NoHeader, incomingBuffer.Count));
                            incomingBuffer.Clear();
                            break;
                        }
                        if (syncIndex > 0)
                        {
                            // remove everything before sync
                            incomingBuffer.RemoveRange(0, syncIndex);
                            Task.Run(() => OnDataDropped(L1DropBytesReasons.FindingHeaders, syncIndex));
                        }
                        int targetDataLength =
                            (incomingBuffer[1] << 16) |
                            (incomingBuffer[2] << 8) |
                            incomingBuffer[3];

                        ModemAPIDebugger.OutputDebugMessage($"SerialPacket.cs received {incomingBuffer.Count} of {targetDataLength} (Length bytes: {incomingBuffer[1]}, {incomingBuffer[2]}, {incomingBuffer[3]})");
                        ModemAPIDebugger.PrintByteArray([.. incomingBuffer]);

                        // check if everything is received
                        if (incomingBuffer.Count < targetDataLength + 4)
                        {
                            break;
                        }

                        byte[] packet = incomingBuffer
                            .GetRange(4, targetDataLength)
                            .ToArray();

                        incomingBuffer.RemoveRange(0, targetDataLength + 4);
                        //incomingBuffer.Clear();
                        ModemAPIDebugger.OutputDebugMessage($"2: INCOMING BUFFER COUNTS {incomingBuffer.Count} BYTES");

                        receiveTimer.Reset();
                        onReceive(packet, killer);
                        ModemAPIDebugger.OutputDebugMessage("recv");
                        //ModemAPIDebugger.PrintByteArray(incomingBuffer.ToArray());
                    }
                    //Console.WriteLine($"process packets in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds}ms");
                }
                
            });
        }
        private int CalculateTimeout(int packetSize, int baudRate)
        {
            // B/s speed
            double bytesPerSecond = baudRate / 8.0;

            // time
            double transferTime = packetSize / bytesPerSecond;

            // +10% extra
            double timeoutSeconds = transferTime * 1.1;

            // add 100ms to prevent mistimeouts
            timeoutSeconds += 0.1;

            // convert to ms
            int timeoutMs = (int)Math.Ceiling(timeoutSeconds * 1000);

            ModemAPIDebugger.OutputDebugMessage($"Timeout for {packetSize} bytes at {baudRate} bps: {timeoutMs}ms");

            return timeoutMs;
        }
    }
}
