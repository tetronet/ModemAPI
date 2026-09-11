using System.IO.Ports;
using System.IO.Hashing;
using System.Text;

namespace ModemAPI
{
    public class AnsweringPhysicalModem : IAnsweringModem
    {
        private bool IsPacketReaderReady = true;
        private bool IsPacketWriterReady = true;
        internal SerialPort? CommunicationalSerialPort = null;
        internal int MadeReinitializeAttempts = 0;
        internal Random RandomGenerator = new();
        internal bool IsDataTransmissionLocked = false;
        internal bool AddressWasManuallySet = false;
        internal List<long> NumbersTaken = [];
        internal List<Action<DataBlock, Action, PacketTransmissionDirection>> InterceptEvents = [];
        internal List<Action<DataBlock, Action, PacketTransmissionDirection>> InterceptEventsToRemove = [];
        internal bool? RemoteDeviceType = null;
        internal int SMLALength = 0;
        internal Address LocalAddress = new();
        internal Address RemoteAddress = new();
        internal SerialPacket SerialPacketizer;
        public bool IsModemConnected { get; private set; }
        public int PortSpeed = 0;
        public int LineSpeed = 0;
        public BPTx BitsPerTick = BPTx.BPTx1;
        public string CommunicationalPort = "COM1";
        public PhysicalModemCommandList CommandList = new();
        public PhysicalModemEventList EventList = new();
        public bool UseATCommands = true;
        public int ActualLineSpeed { get; private set; }
        public BPTx ActualLineBPTx { get; private set; }
        public int MaxRetransmissionTimes = 0;
        public int GenericPacketCounter { get; private set; }
        /*public void Answer()
        {
            if (CommunicationalSerialPort != null)
            {
                if (UseATCommands)
                {
                    CommunicationalSerialPort.WriteLine(CommandList.AnswerToIncoming);
                }
                _ = OnDataRead(delegate (byte[] bytesRode, Action stopLoop)
                {
                    bool isConnectionSuccess = true;
                    PortByteBuffer.AddRange(bytesRode);
                    // Decode main CIoCIL phrases (answering):
                    if (Encoding.UTF8.GetString([.. PortByteBuffer]).StartsWith(EventList.Connect))
                    {
                        IsModemConnected = true;
                    }
                    else if (PortByteBuffer.Intersect(CIoCILDefaultPhrases.RequestConnection).Any())
                    {
                        CommunicationalSerialPort.Write(CIoCILDefaultPhrases.LocalAddressRequest, 0, 5);
                    }
                    else if (PortByteBuffer.Take(2).Intersect(CIoCILDefaultPhrases.LocalAddressTransferToNonLocal).Any())
                    {
                        if (Crc32.HashToUInt32(PortByteBuffer.ToArray()) != 0)
                        {
                            isConnectionSuccess = false;
                        }
                        else
                        {
                            string receviedAddress = Encoding.UTF8.GetString((byte[])PortByteBuffer.Skip(3).Take(PortByteBuffer[2]).SkipLast(4));
                            if (LocalAddress.AddressValue == null)
                            {
                                throw new NullReferenceException("AddressValue must except null");
                            }
                            if (receviedAddress == "" || receviedAddress == "0" || !receviedAddress.StartsWith(LocalAddress.AddressValue))
                            {
                                List<byte> addressForTransmitting = new List<byte>();
                                addressForTransmitting.AddRange(CIoCILDefaultPhrases.SetModemLocalAddress);
                                if (SMLALength < 1)
                                {
                                    throw new UnableToCreateAddressException("to create an actual addresss for tetronet, AddressLength must be a number from 1 to 2147483647");
                                }
                                else if (AddressWasManuallySet)
                                {
                                    if (RemoteAddress.AddressValue == null)
                                    {
                                        throw new NullAddressException("address must be not null");
                                    }
                                    string[] addressSplitted = RemoteAddress.AddressValue.Split('-');
                                    string lastAddressDigits = addressSplitted[^1];
                                    addressForTransmitting.AddRange(Encoding.UTF8.GetBytes(LocalAddress + "-" + lastAddressDigits));
                                    CommunicationalSerialPort.Write(addressForTransmitting.ToArray(), 0, addressForTransmitting.Count);
                                    Thread.Sleep(1250);
                                    AddressWasManuallySet = false;
                                }
                                else
                                {
                                    int addressLastDigits = RandomGenerator.Next(0, (int)Math.Pow(10, SMLALength) - 1);
                                    addressForTransmitting.AddRange(Encoding.UTF8.GetBytes(LocalAddress + "-" + addressLastDigits.ToString().PadLeft(SMLALength, '0')));
                                    CommunicationalSerialPort.Write(addressForTransmitting.ToArray(), 0, addressForTransmitting.Count);
                                    Thread.Sleep(1250);
                                }
                            }
                            CommunicationalSerialPort.Write(CIoCILDefaultPhrases.LocalDeviceTypeRequest, 0, 5);
                        }
                    }
                    else if (PortByteBuffer.Take(1).Intersect(CIoCILDefaultPhrases.DeviceTypeTransferToNonLocal).Any())
                    {
                        if (PortByteBuffer.Skip(1).Take(1).Intersect(CIoCILDefaultPhrases.DeviceTypeModem).Any())
                        {
                            // Making the remote device type to be modem.
                            // Modem - false
                            // Address machine - true
                            // By default - null
                            RemoteDeviceType = false;
                        }
                        else if (PortByteBuffer.Skip(1).Take(1).Intersect(CIoCILDefaultPhrases.DeviceTypeAddressMachine).Any())
                        {
                            // Making the remote device type to be modem.
                            // Modem - false
                            // Address machine - true
                            // By default - null
                            RemoteDeviceType = true;
                        }
                        else
                        {
                            isConnectionSuccess = false;
                        }
                    }
                    else
                    {
                        isConnectionSuccess = false;
                    }
                    PortByteBuffer.Clear();
                    // Make modem not connected, if CIoCIL connection failed:
                    if (!isConnectionSuccess)
                    {
                        CommunicationalSerialPort.Write(CIoCILDefaultPhrases.NonLocalFinishInitFailure, 0, 4);
                        IsModemConnected = false;
                        throw new ConnectionFailedException("something went wrong while modems were dealing with each other");
                    }
                    else
                    {
                        CommunicationalSerialPort.Write(CIoCILDefaultPhrases.NonLocalFinishInitSuccess, 0, 4);
                        return;
                    }
                });
            }
        }*/

        public AnsweringPhysicalModem(SerialPort port)
        {
            CommunicationalSerialPort = port;
            SerialPacketizer = new SerialPacket(CommunicationalSerialPort);
        }

        public void Answer()
        {
            ModemAPIDebugger.OutputDebugMessage("answer to the incoming connection");
            if (CommunicationalSerialPort == null)
                throw new InvalidOperationException("Serial port is not initialized");

            

            SerialPacketizer.OnBufferReceived((byte[] data, Action kill) =>
            {
                ModemAPIDebugger.OutputDebugMessage("received incoming data at AnsweringPhysicalModem.cs at line 157");
                ModemAPIDebugger.PrintByteArray(data);
                List<byte> buffer = [.. data];
                byte[] connectBytes = Encoding.UTF8.GetBytes(EventList.Connect);
                if (buffer.Count >= connectBytes.Length)
                {
                    if (buffer.Take(connectBytes.Length).SequenceEqual(connectBytes))
                    {
                        IsModemConnected = true;
                        return;
                    }
                }
                

                // Проверяем передачу адреса
                if (buffer.Count >= 2 &&
                    buffer.Take(2).SequenceEqual(CIoCILDefaultPhrases.LocalAddressTransferToNonLocal))
                {
                    byte[] remoteHash = [ ..buffer.TakeLast(4)];
                    ModemAPIDebugger.OutputDebugMessage("----------");
                    ModemAPIDebugger.OutputDebugMessage("==== [CRC32 in AnsweringPhysicalModem.cs at 179 line] ====");
                    ModemAPIDebugger.OutputDebugMessage("Remote CRC: " + remoteHash.ToString());
                    ModemAPIDebugger.PrintByteArray(buffer.SkipLast(4).ToArray());
                    ModemAPIDebugger.OutputDebugMessage("----------");
                    if (Crc32.Hash(buffer.SkipLast(4).ToArray()).SequenceEqual(remoteHash))
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalFinishInitFailure);
                        IsModemConnected = false;
                        kill();
                        throw new ConnectionFailedException("CRC check failed for address transfer");
                    }

                    // Извлечение адреса
                    string receivedAddress = Encoding.UTF8.GetString(buffer.Skip(3).Take(buffer[2]).SkipLast(4).ToArray());
                    if (LocalAddress.AddressValue == null)
                    {
                        kill();
                        throw new NullAddressException("Cannot SMLA address for slave node without address");
                    }
                    if (string.IsNullOrEmpty(receivedAddress) || !receivedAddress.StartsWith(LocalAddress.AddressValue))
                    {
                        ModemAPIDebugger.OutputDebugMessage("==== [SMLA is going on at AnsweringPhysicalModem.cs at line 198] ====");
                        List<byte> addressForTx = [.. CIoCILDefaultPhrases.SetModemLocalAddress];
                        int lastDigits = RandomGenerator.Next(0, (int)Math.Pow(10, SMLALength) - 1);
                        while (NumbersTaken.Contains(lastDigits))
                        {
                            lastDigits = RandomGenerator.Next(0, (int)Math.Pow(10, SMLALength) - 1);
                        }
                        RemoteAddress = new($"{LocalAddress.AddressValue}-{lastDigits.ToString().PadLeft(SMLALength, '0')}");
                        byte[] addressForTxAddrOnly = Encoding.UTF8.GetBytes(RemoteAddress.AddressValue ?? throw new NullAddressException());

                        
                        addressForTx.Add((byte)addressForTxAddrOnly.Length);

                        addressForTx.AddRange(addressForTxAddrOnly);

                        addressForTx.AddRange(Crc32.Hash([.. addressForTx]));

                        SerialPacketizer.TransmitBuffer([.. addressForTx]);
                        //Thread.Sleep(1250);
                    }

                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalDeviceTypeRequest);
                }

                // Проверяем тип устройства
                if (buffer.Count >= 2 &&
                    buffer.Take(1).SequenceEqual(CIoCILDefaultPhrases.DeviceTypeTransferToNonLocal))
                {
                    if (buffer.Skip(1).Take(1).SequenceEqual(CIoCILDefaultPhrases.DeviceTypeModem))
                        RemoteDeviceType = false;
                    else if (buffer.Skip(1).Take(1).SequenceEqual(CIoCILDefaultPhrases.DeviceTypeAddressMachine))
                        RemoteDeviceType = true;
                    else
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalFinishInitFailure);
                        IsModemConnected = false;
                        throw new ConnectionFailedException("Unknown remote device type");
                    }

                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalFinishInitSuccess);
                    IsModemConnected = true;
                    kill();
                    return;
                }

                // Если буфер слишком большой, обрезаем, чтобы не переполнялся
                if (buffer.Count > 4096)
                    buffer.RemoveRange(0, buffer.Count - 1024);
            });
            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalAddressRequest); // Transmit a packet for the non local modem, that local modems accepts the incoming connection
        }


        public void AttachConnectEvent(Action onConnection)
        {
            SerialPacketizer.OnBufferReceived(delegate (byte[] dataRode, Action kill)
            {
                if (dataRode.Intersect(CIoCILDefaultPhrases.LocalFinishInitSuccess).Any())
                {
                    onConnection();
                    kill();
                }
            });
        }

        public void Drop()
        {
            if (CommunicationalSerialPort == null)
            {
                throw new NullReferenceException("port was null");
            }
            if (UseATCommands)
            {
                Thread.Sleep(2000);
                foreach (char c in CommandList.ExitDataMode)
                {
                    CommunicationalSerialPort.Write(c.ToString());
                }
                Thread.Sleep(2000);
                CommunicationalSerialPort.WriteLine(CommandList.ConnectionAbort);
                _ = OnDataRead(delegate (byte[] dataRode, Action stopCycle)
                {
                    if (Encoding.UTF8.GetString(dataRode) == EventList.CommandExecSuccess)
                    {
                        Thread.Sleep(1000);
                        stopCycle();
                    }
                });
            }
            CommunicationalSerialPort.Close();
            IsModemConnected = false;
        }

        public void Intercept(Action<DataBlock, Action, PacketTransmissionDirection> onPacketIntercepted)
        {
            DataBlock prepare = new DataBlock();
            SerialPacketizer.OnBufferReceived(delegate (byte[] dataRode, Action kill)
            {
                if (dataRode.Take(4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader))
                {
                    Packet receivedData = PacketConverter.BytesToPacket(dataRode);
                    prepare.DataBytes.AddRange(receivedData.DataBytes);
                    if (receivedData.IsLastInSequence)
                    {
                        prepare.ConnectionID = receivedData.ConnectionID;
                        prepare.Transmitter = receivedData.Transmitter;
                        prepare.Receiver = receivedData.Receiver;
                        prepare.QueryType = receivedData.QueryType;
                        prepare.Metadata = receivedData.Metadata;
                        onPacketIntercepted(prepare, delegate () { }, PacketTransmissionDirection.FromLowerToUpper);
                        prepare = new DataBlock();
                        kill();
                    }
                }
            });
        }

        public void TransmitToSubscriber(byte[] data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            if (LocalAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            for (int i = 0; i < data.Length; i += packetSize)
            {
                Packet packet = new();
                packet.DataBytes = data.Skip(i).Take(packetSize).ToList();
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = RemoteAddress;
                packet.Transmitter = LocalAddress;
                TransmitToSubscriber(packet);
            }
        }

        public void TransmitToSubscriber(string data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            if (LocalAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            if (IsDataTransmissionLocked)
            {
                throw new DataTransmissionLockedException("cannot transmit because data transmission is locked");
            }
            for (int i = 0; i < data.Length; i += packetSize)
            {
                Packet packet = new();
                packet.DataBytes = Encoding.UTF8.GetBytes(new string((char[])data.ToCharArray().Skip(i).Take(packetSize)).ToArray()).ToList();
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = RemoteAddress;
                packet.Transmitter = LocalAddress;
                TransmitToSubscriber(packet);
            }
            foreach (Action<DataBlock, Action, PacketTransmissionDirection> interceptEvent in InterceptEvents)
            {
                interceptEvent(                                         // Trigger the user event 
                    new DataBlock(                                      // Init new instance of DataBlock
                        LocalAddress,                                   // Enter transmitter (local address)
                        RemoteAddress,                                  // Enter receiver (remote address)
                        Encoding.UTF8.GetBytes(data).ToList(),          // Set data as bytes
                        data,                                           // Set data as string
                        metadata,                                       // Set metadata for DataBlock
                        queryType,                                      // Enter query type
                        connectionId                                    // Enter ConnectionID for DataBlock
                    ),
                    delegate ()                                         // Set stopCycle() as an delegate with cycle destroyer
                    {
                        InterceptEvents.Remove(interceptEvent);         // Remove event from intercept event list
                        InterceptEventsToRemove.Add(interceptEvent);    // Add event to intercept event to remove list
                    },
                    PacketTransmissionDirection.FromUpperToLower        // Set packet transmission direction as FromUpperToLower
                );
            }
        }

        public void LockDataTransfer()
        {
            IsDataTransmissionLocked = true;
        }

        public void UnlockDataTransfer()
        {
            IsDataTransmissionLocked = false;
        }

        public Address GetSubscriberAddress()
        {
            return RemoteAddress;
        }

        public bool SetSubscriberAddress(Address subscriberAddress)
        {
            if (IsModemConnected)
            {
                return false;
            }
            else
            {
                AddressWasManuallySet = true;
                RemoteAddress = subscriberAddress;
                return true;
            }
        }

        public void AttachIncomingEvent(Action onIncoming)
        {
            ModemAPIDebugger.OutputDebugMessage("waiting for an incoming connection..");
            ModemAPIDebugger.OutputDebugMessage($"Packetizer instance (in modem): {SerialPacketizer.GetHashCode()}");
            SerialPacketizer.OnBufferReceived(delegate (byte[] bytesRode, Action kill)
            {
                ModemAPIDebugger.OutputDebugMessage("data received");
                ModemAPIDebugger.PrintByteArray([.. bytesRode]);
                ModemAPIDebugger.PrintByteArray(CIoCILDefaultPhrases.RequestConnection);
                /*if (Encoding.UTF8.GetString(bytesRode) == EventList.IncomingConnection && UseATCommands)
                {
                    onIncoming(destroyCycle);
                }*/
                if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.RequestConnection))
                {
                    onIncoming();
                    kill();
                    ModemAPIDebugger.OutputDebugMessage("I'm just over there!");
                }
            });
        }
        internal void TransmitToSubscriber(Packet packet)
        {
            if (IsDataTransmissionLocked)
            {
                throw new DataTransmissionLockedException(
                    "to receive, transmit or transfer packets, data transmission must be unlocked");
            }

            if (CommunicationalSerialPort == null)
            {
                throw new InvalidOperationException(
                    "communication with modem was not set while transmitting");
            }

            int packageRetransmissionTimes = 0;
            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.RequestForTransmittingPacket);

            IsPacketWriterReady = false;
            TransmitState state = TransmitState.WaitingForImmediateAnswer;
            SerialPacketizer.OnBufferReceived((dataRode, kill) =>
            {
                switch (state)
                {
                    case TransmitState.WaitingForImmediateAnswer:
                        if (dataRode.SequenceEqual(CIoCILDefaultPhrases.ImmediateAnswerFromNonLocal))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Received ImmediateAnswerFromNonLocal");
                            state = TransmitState.WaitingForNonLocalReady;
                            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalIsReadyForTransmit);
                        }
                        break;

                    case TransmitState.WaitingForNonLocalReady:
                        if (dataRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalIsAcceptingReadinness))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Received NonLocalIsAcceptingReadinness");
                            state = TransmitState.WaitingForPacketTransmission;
                            byte[] compiledPacket = PacketConverter.PacketToBytes(packet);
                            SerialPacketizer.TransmitBuffer(compiledPacket);
                        }
                        break;

                    case TransmitState.WaitingForPacketTransmission:
                        if (dataRode.SequenceEqual(CIoCILDefaultPhrases.PacketRetransmittionRequired))
                        {
                            if (packageRetransmissionTimes >= MaxRetransmissionTimes)
                            {
                                state = TransmitState.Done;
                                kill();
                                throw new TooManyAttempsOfTransferringException(
                                    "Was not able to transfer the packet after maximum attempts");
                            }

                            ModemAPIDebugger.OutputDebugMessage("Received PacketRetransmittionRequired");
                            byte[] compiledPacket = PacketConverter.PacketToBytes(packet);
                            SerialPacketizer.TransmitBuffer(compiledPacket);
                            packageRetransmissionTimes++;
                        }
                        else if (dataRode.SequenceEqual(CIoCILDefaultPhrases.PacketTransmittionSuccess))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Received PacketTransmittionSuccess");
                            state = TransmitState.WaitingForTransmitComplete;
                            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.TransmittionCompleteRequest);
                        }
                        break;

                    case TransmitState.WaitingForTransmitComplete:
                        if (dataRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalAcceptsPacketSuccessTransmittion))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Received NonLocalAcceptsPacketSuccessTransmittion");
                            GenericPacketCounter++;
                            state = TransmitState.Done;
                            IsPacketWriterReady = true;
                            kill();
                        }
                        break;
                }
            });

            // Ждём завершения передачи
            while (state != TransmitState.Done)
            {
                Thread.Sleep(3);
            }
        }


        private async Task OnDataRead(Action<byte[], Action> onBufferReadAvailable)
        {
            using var cts = new CancellationTokenSource();
            void stopWaiting() => cts.Cancel();

            await Task.Run(() =>
            {
                var token = cts.Token;
                while (!token.IsCancellationRequested && CommunicationalSerialPort?.IsOpen == true)
                {
                    int bytesAvailable = CommunicationalSerialPort.BytesToRead;
                    if (bytesAvailable > 0)
                    {
                        byte[] buffer = new byte[bytesAvailable];
                        CommunicationalSerialPort.Read(buffer, 0, bytesAvailable);
                        onBufferReadAvailable(buffer, stopWaiting);
                    }
                    Thread.Sleep(1);
                }
            }, cts.Token);
        }


        internal void InternalAttachReceiveEvent(Action<Packet> onReceive)
        {
            if (CommunicationalSerialPort == null)
            {
                throw new InvalidOperationException("communication with modem was not set while applying receiving event");
            }
            IsPacketReaderReady = true;
            SerialPacketizer.OnBufferReceived(delegate (byte[] dataRode, Action kill)
            {
                // Main code for receiving data from CI:
                ModemAPIDebugger.OutputDebugMessage("read data: ");
                ModemAPIDebugger.PrintByteArray(dataRode);
                if (CommunicationalSerialPort == null)
                {
                    kill();
                    throw new InvalidOperationException("communication with modem was not set while receiving the packet");
                }
                if (dataRode.SequenceEqual(CIoCILDefaultPhrases.RequestForTransmittingPacket) && IsPacketReaderReady)
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ImmediateAnswerFromNonLocal);
                    ModemAPIDebugger.OutputDebugMessage("AnsweringPhysicalModem.cs: CIoCILDefaultPhrases.ImmediateAnswerFromNonLocal");
                    IsPacketReaderReady = false;
                }
                if (dataRode.SequenceEqual(CIoCILDefaultPhrases.LocalIsReadyForTransmit))
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalIsAcceptingReadinness);
                    ModemAPIDebugger.OutputDebugMessage("AnsweringPhysicalModem.cs: CIoCILDefaultPhrases.NonLocalIsAcceptingReadinness");
                }
                if (dataRode.Take(4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader))
                {
                    Packet packet = PacketConverter.BytesToPacket(dataRode);
                    ModemAPIDebugger.PrintOutPacket(packet);
                    if (packet.IsErrorWhileReading)
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.PacketRetransmittionRequired);
                    }
                    else
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.PacketTransmittionSuccess);
                        onReceive(packet);
                    }
                }
                if (dataRode.SequenceEqual(CIoCILDefaultPhrases.TransmittionCompleteRequest))
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalAcceptsPacketSuccessTransmittion);
                    GenericPacketCounter++;
                    IsPacketReaderReady = true;
                }
            });
        }
    }
}
