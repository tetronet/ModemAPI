using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.Ports;
using System.Text;

namespace ModemAPI
{
    [Obsolete("CIoCIL is deprecated. Consider switching to LowLatencyPhysicalModem. This call will not receive anny updates")]
    public class PhysicalModem : IModem, IAnswerHost
    {
        private PhysicalModemParams ModemParams = new();
        private SerialPort? CommunicationalSerialPort;
        private SerialPacket? SerialPacketizer;
        private Dictionary<string, AnsweringPhysicalModem> SlavePhysicalModems = new();
        private Dictionary<Address, string> AddressPortPairs = new();
        private int MadeReinitializeAttempts = 0;
        private readonly Random RandomGenerator = new();
        private Packet[] PacketBuffer = new Packet[256];
        private int PacketBufferActualLength = 0;
        private string MessageBuffer = "";
        private byte[] MessageBufferBytes = new byte[1048576];
        private int MessageBufferBytesCursor = 0;
        private bool AddressWasSetByUpperNode = false;
        private bool IsPacketWriterReady = true;
        private bool IsProcessingMessage = false; // Flag for preventing processing message twice.
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        /// <summary>
        /// Use AT-commands not no?
        /// If you're using direct cable connection (like RS232 or UART), set it to false.
        /// For Dial-up modems set it to true
        /// </summary>
        public bool UseATCommands = true;
        /// <summary>
        /// How many digits will slave AnsweringPhysicalModem create to the remote device?
        /// </summary>
        public int? AddressLength = null;
        /// <summary>
        /// Local address for current modem, that will be used to connect to the Tetronet
        /// </summary>
        public Address? LocalModemAddress { get; set; }
        /// <summary>
        /// How many packets was received or transmitted by this exact modem.
        /// </summary>
        public long GenericPacketCounter { get; private set; }
        /// <summary>
        /// Allows you to get is modem connected, true, if it connected, or answered, false if it's waiting for the incoming connection, or isn't dialing with the line
        /// </summary>
        public bool IsModemConnected { get; set; }
        /// <summary>
        /// How many times packet will get retransmitted if it has got corrupted in line.
        /// </summary>
        public int MaxRetransmittionTimes = 1048576;
        /// <summary>
        /// This value determinates how many attempts will be made to connect
        /// to the tetronet, if this number will be surpassed, modem will automatically
        /// disconnect from the line.
        /// </summary>
        public int MaxReinitializeAttempts { get; set; }
        /// <summary>
        /// Get an actual value of current bitrate.
        /// Will be null if certain conditions happen:
        ///     Modem isn't connected, or it disconnected from the network.
        /// </summary>
        public int? ActualLineSpeed { get; private set; }
        /// <summary>
        /// Get an actual BPTx protocol.
        /// Will be null if certain conditions happen:
        ///     Modem isn't connected, or it disconnected from the network.
        /// </summary>
        public BPTx? ActualLineBPTx { get; private set; }
        /// <summary>
        /// Creates a new instance of PhysicalModem, where all of the settings will get transferred through DTO.
        /// </summary>
        /// <param name="modemInitalParams">DTO, that contains all of the settings for new instance of this modem</param>
        /// <param name="initCommands">What commands to run when modem initializes?</param>
        /// <param name="delay">How many time will be waiten between two commands, recommended is 1-3 seconds</param>
        /// <exception cref="ArgumentNullException"></exception>
        public PhysicalModem(PhysicalModemParams modemInitalParams, bool useAT, string[] initCommands, int delay)
        {
            if (modemInitalParams == null)
            {
                throw new ArgumentNullException("too bad, modemInitialParams is null");
            }
            UseATCommands = useAT;
            ModemParams = modemInitalParams;
            Exception? setupModem = ModemInitialSetup(modemInitalParams);
            if (setupModem != null)
            {
                throw setupModem;
            }
            if (CommunicationalSerialPort == null)
            {
                throw new NullReferenceException("communicational serial port must except null");
            }
            foreach (string command in initCommands)
            {
                CommunicationalSerialPort.WriteLine(command);
                Thread.Sleep(delay);
            }
        }
        private Exception? ModemInitialSetup(PhysicalModemParams modemInitalParams)
        {
            try
            {
                CommunicationalSerialPort = new SerialPort((string)modemInitalParams.GetModemParams()[3], (int)modemInitalParams.GetModemParams()[0]);
                PhysicalModemCommandList cmdList = (PhysicalModemCommandList)modemInitalParams.GetModemParams()[4];
                if (!CommunicationalSerialPort.IsOpen)
                {

                    CommunicationalSerialPort.Parity = Parity.None;
                    CommunicationalSerialPort.StopBits = StopBits.One;
                    CommunicationalSerialPort.DataBits = 8;
                    CommunicationalSerialPort.Handshake = Handshake.None;
                    CommunicationalSerialPort.DtrEnable = true;
                    CommunicationalSerialPort.RtsEnable = true;
                    CommunicationalSerialPort.Open();
                }
                SerialPacketizer = new SerialPacket(CommunicationalSerialPort);
                if (UseATCommands)
                {
                    CommunicationalSerialPort.WriteLine(cmdList.ModemTest);
                    _ = OnDataRead(delegate (byte[] dataRode, Action stopLoop)
                    {
                        string potentialModemAnswer = Encoding.UTF8.GetString(dataRode);
                        if (potentialModemAnswer != ((PhysicalModemEventList)ModemParams.GetModemParams()[5]).CommandExecSuccess)
                        {
                            throw new NotACopybookModemException("There aren't a copybook modem on selected communicational port! Modem returned: " + potentialModemAnswer.Replace("\r", "\\r").Replace("\n", "\\n"));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return ex;
            }
            return null;
        }

        /// <summary>
        /// Setups the communicational line, that your modem is using, sends required sequences of bytes, to non-local
        /// modem will regognize them as connection initialization data. Then you will able to transfer messages using
        /// Transmit method of this modem.
        /// 
        /// Also there is a way to get BPTx and bitrate, that are used in line by using following variables:
        ///     int? ActualLineSpeed
        ///     BPTx? ActualLineBPTx
        /// These are defined after Dial(), and before Drop()!
        /// </summary>
        /// <exception cref="LineNotPresentedException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="ModemAlreadyInitedException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="UnableToConnectToTheTetronetException"></exception>
        /// <exception cref="LocalAddressTooLongException"></exception>
        /// <exception cref="IncorrectModemException"></exception>
        public void Dial()
        {
            LineParams innerLineParams = (LineParams)ModemParams.GetModemParams()[6];
            int bitrate = (int)ModemParams.GetModemParams()[1];
            BPTx bitsPerTick = (BPTx)ModemParams.GetModemParams()[2];
            if (IsModemConnected)
            {
                throw new ModemAlreadyInitedException("this modem is already connected to the tetronet!");
            }
            ModemAPIDebugger.OutputDebugMessage("Dial entered");
            ModemAPIDebugger.OutputDebugMessage("UseATCommands = " + UseATCommands);
            ModemAPIDebugger.OutputDebugMessage("innerLineParams null? " + (innerLineParams == null));
            ModemAPIDebugger.OutputDebugMessage("bitrate = " + bitrate);
            ModemAPIDebugger.OutputDebugMessage("port null? " + (CommunicationalSerialPort == null));

            if (innerLineParams != null && bitrate != 0 && CommunicationalSerialPort != null)
            {
                CommunicationalSerialPort.BaudRate = (int)ModemParams.GetModemParams()[0];
                CommunicationalSerialPort.Parity = Parity.None;
                CommunicationalSerialPort.StopBits = StopBits.One;
                if (!CommunicationalSerialPort.IsOpen)
                {
                    CommunicationalSerialPort.Open();
                }
                if (UseATCommands)
                {
                    Thread.Sleep(2500);
                    CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).ChangeBitrate.Replace("%{arg1}", bitrate.ToString()));
                    Thread.Sleep(1500);
                    if (bitsPerTick == BPTx.BPTx1)
                    {
                        CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).SetBPTx1);
                    }
                    else if (bitsPerTick == BPTx.BPTx2)
                    {
                        CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).SetBPTx2);
                    }
                    else if (bitsPerTick == BPTx.BPTx4)
                    {
                        CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).SetBPTx4);
                    }
                    else if (bitsPerTick == BPTx.BPTx8)
                    {
                        CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).SetBPTx8);
                    }
                    else
                    {
                        throw new ArgumentException("BPTx.None is wrong");
                    }
                    Thread.Sleep(1500);
                    CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).Dial);
                    _ = OnDataRead(delegate (byte[] dataRode, Action stopLoop)
                    {
                        string answer = CommunicationalSerialPort.ReadLine();
                        PhysicalModemEventList eventList = (PhysicalModemEventList)ModemParams.GetModemParams()[5];
                        if (answer == eventList.NoLine)
                        {
                            throw new LineNotPresentedException("line is not connected to the modem");
                        }
                        else if (answer.StartsWith(eventList.Connect))
                        {
                            ConnectToTheTetronet(answer);
                            stopLoop();
                        }
                    });
                }
                else
                {
                    ConnectToTheTetronet("CONNECT " + CommunicationalSerialPort.BaudRate.ToString());
                }
                IsModemConnected = true;
            }
            else
            {
                throw new InvalidOperationException("modemapi -- user caused an invalid operation (unable to connect to the network)");
            }
        }

        private void ConnectToTheTetronet(string modemAnswer)
        {
            // Inner-method: connecting to the tetronet after preparing the communicational line.
            ModemAPIDebugger.OutputDebugMessage("ConnectToTheTetronet entered");
            string? potentialBitrate = null;
            try
            {
                potentialBitrate = modemAnswer.Split(' ')[1].Split(',')[0];
            }
            catch (Exception)
            {
                potentialBitrate = "0";
            }
            string? potentialBPTx = null;
            try
            {
                potentialBPTx = modemAnswer.Split(' ')[1].Split(',')[1];
            }
            catch (Exception)
            {
                potentialBPTx = "BPTx_unavailable";
            }
            if (potentialBitrate != null && potentialBPTx != null && SerialPacketizer != null)
            {
                // Connecting to the Tetronet by using protocol CIoCIL:
                ActualLineSpeed = int.Parse(potentialBitrate);
                if (potentialBPTx == "BPTx1")
                {
                    ActualLineBPTx = BPTx.BPTx1;
                }
                else if (potentialBPTx == "BPTx2")
                {
                    ActualLineBPTx = BPTx.BPTx2;
                }
                else if (potentialBPTx == "BPTx4")
                {
                    ActualLineBPTx = BPTx.BPTx4;
                }
                else if (potentialBPTx == "BPTx8")
                {
                    ActualLineBPTx = BPTx.BPTx8;
                }
                else
                {
                    ActualLineBPTx = BPTx.None;
                }

                if (int.Parse(potentialBitrate) != 0)
                {
                    ActualLineSpeed = int.Parse(potentialBitrate);
                }
                SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.RequestConnection);
                SerialPacketizer.OnBufferReceived(delegate (byte[] bytesRode, Action kill)
                {
                    ModemAPIDebugger.PrintByteArray(bytesRode);
                    if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.LocalAddressRequest))
                    {
                        LineParams lineParams = (LineParams)ModemParams.GetModemParams()[6];
                        List<byte> address = new();
                        string? addressRaw = lineParams.ModemLocalAddress.AddressValue;
                        addressRaw ??= "";
                        if (addressRaw.Length > 255)
                        {
                            kill();
                            throw new LocalAddressTooLongException("unable to connect to the tetronet because of local address was too long");
                        }
                        address.AddRange(CIoCILDefaultPhrases.LocalAddressTransferToNonLocal);
                        address.Add((byte)addressRaw.Length);
                        for (int i = 0; i < addressRaw.Length; i++)
                        {
                            address.Add((byte)addressRaw[i]);
                        }
                        byte[] addressCrc = Crc32.Hash(address.ToArray());
                        address.AddRange(addressCrc);
                        ModemAPIDebugger.PrintByteArray(address.ToArray());
                        List<byte> testArray = [];
                        foreach (byte gjios in address)
                        {
                            if (gjios >= 127)
                            {
                                testArray.Add(127);
                            }
                            else
                            {
                                testArray.Add(gjios);
                            }
                        }
                        SerialPacketizer.TransmitBuffer(testArray.ToArray());
                    }
                    else if (bytesRode.Take(4).SequenceEqual(CIoCILDefaultPhrases.SetModemLocalAddress))
                    {
                        ModemAPIDebugger.OutputDebugMessage("==== [Received SMLA at PhysicalModem.cs at line 300] ====");
                        uint remoteHash = BitConverter.ToUInt32([.. bytesRode.TakeLast(4)], 0);
                        if (Crc32.HashToUInt32([.. bytesRode.SkipLast(4)]) == remoteHash)
                        {
                            byte[] addressAsBytes = bytesRode.Skip(5).Take(bytesRode[4]).ToArray();         // Take Address from recevied data.
                            ModemAPIDebugger.OutputDebugMessage("==== [Next array printout is the recevied address info]");
                            ModemAPIDebugger.PrintByteArray(addressAsBytes);
                            string addressAsString = Encoding.ASCII.GetString(addressAsBytes);              // Convert the Address from byte[] to string as ASCII.
                            ModemAPIDebugger.OutputDebugMessage("Address after byte[]->string conversion: " + addressAsString);
                            LocalModemAddress = new(addressAsString);                                       // Insert recevied Address from non-local modem.
                            AddressWasSetByUpperNode = true;                                                // Local address was set by the address machine
                        }
                        else
                        {
                            ModemAPIDebugger.OutputDebugMessage ("hash is not 0 in SMLA");
                            kill();
                            throw new UnableToConnectToTheTetronetException("hash error");
                        }
                    }
                    else if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.LocalDeviceTypeRequest))
                    {
                        ModemAPIDebugger.OutputDebugMessage("==== [Device type was requested at PhysicalModem.cs at line 319] ====");
                        if (SlavePhysicalModems.Count == 0)
                        {
                            SerialPacketizer.TransmitBuffer([.. CIoCILDefaultPhrases.DeviceTypeTransferToNonLocal, .. CIoCILDefaultPhrases.DeviceTypeModem]);
                        }
                        else
                        {
                            SerialPacketizer.TransmitBuffer([.. CIoCILDefaultPhrases.DeviceTypeTransferToNonLocal, .. CIoCILDefaultPhrases.DeviceTypeAddressMachine]);
                        }
                    }
                    else if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalFinishInitSuccess))
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalFinishInitSuccess);
                        IsModemConnected = true;
                        kill();
                    }
                    else if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalFinishInitFailure))
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalFinishInitFailure);
                        MadeReinitializeAttempts++;
                        if (MadeReinitializeAttempts >= MaxReinitializeAttempts)
                        {
                            MadeReinitializeAttempts = 0;
                            Drop(false);
                            throw new UnableToConnectToTheTetronetException("ModemAPI was unable to connect to the tetronet: max reinit attempts was surpassed");
                        }
                        else
                        {
                            ConnectToTheTetronet(modemAnswer);
                        }
                        kill();
                    }
                });
            }
            else
            {
                throw new UnableToConnectToTheTetronetException("ModemAPI was unable to connect to the tetronet because of a potential bitrate being null, or potential BPTx was nulled.");
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

        /// <summary>
        /// Disconnects the modem from the line, and destroys the active connection.
        /// By default disconnection reason will be set as "ReasonGracefulDisconnect".
        /// </summary>
        /// <param name="carefulMode">
        /// When set to true: disconnects by transferring disconnection phrases, that are defined in CIoCIL protocol, and afterwards exits data mode, and sends disconnect command from command list.
        /// When set to false: disconnects by exiting data mode, and sending disconnect command from command list.
        /// </param>
        public void Drop(bool carefulMode)
        {
            Drop(carefulMode, DisconnectionReasons.ReasonGracefulDisconnect);
        }
        public void Drop(bool carefulMode, DisconnectionReasons reason)
        {
            if (carefulMode)
            {
                if (IsModemConnected)
                {
                    if (SerialPacketizer != null)
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.RequestDisconnection);
                        SerialPacketizer.OnBufferReceived(delegate (byte[] bytesRode, Action kill)
                        {
                            if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalModemRequestsDisconnectionReason))
                            {
                                switch (reason)
                                {
                                    case DisconnectionReasons.ReasonUnspecified:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonUnspecified);
                                        break;
                                    case DisconnectionReasons.ReasonGracefulDisconnect:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonGracefulDisconnect);
                                        break;
                                    case DisconnectionReasons.ReasonReconnectAsAnAddressMachine:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonReconnectAsAnAddressMachine);
                                        break;
                                    case DisconnectionReasons.ReasonLineOverload:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonLineOverload);
                                        break;
                                    case DisconnectionReasons.ReasonUnexpectedDisconnect:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonUnexpectedDisconnect);
                                        break;
                                    case DisconnectionReasons.ReasonBPTxOrBitrateUnacceptable:
                                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ReasonBPTxOrBitrateUnacceptable);
                                        break;
                                }
                            }
                            else if (bytesRode.SequenceEqual(CIoCILDefaultPhrases.NonLocalSideReasonAccept))
                            {
                                SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalSideAcceptDisconnection);
                                Drop(false);
                            }
                            kill();
                        });
                    }
                }
                else
                {
                    throw new ModemIsNotInitedException("cannot disconnect the modem carefully because it is not connected yet, " +
                        "when there are no established connections, you may disconnect not carefully");
                }
            }
            else
            {
                if (CommunicationalSerialPort != null)
                {
                    if (UseATCommands) {
                        Thread.Sleep(2000);
                        foreach (char c in ((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).ExitDataMode)
                        {
                            CommunicationalSerialPort.Write(c.ToString());
                            Thread.Sleep(250);
                        }
                        Thread.Sleep(2000);
                        _ = OnDataRead(delegate (byte[] bytesRode, Action stopCycle)
                        {
                            if (Encoding.UTF8.GetString(bytesRode) == "OK")
                            {
                                CommunicationalSerialPort.WriteLine(((PhysicalModemCommandList)ModemParams.GetModemParams()[4]).ConnectionAbort);
                                stopCycle();
                            }
                        });
                    }
                    CommunicationalSerialPort.Close();
                    IsModemConnected = false;
                }
                else
                {
                    throw new NullReferenceException("CommunicationalSerialPort is null");
                }
            }
        }

        public Dictionary<string, AnsweringPhysicalModem> GetAnsweringAllModems()
        {
            return SlavePhysicalModems;
        }

        public AnsweringPhysicalModem GetAnsweringModem(string portNumber)
        {
            return SlavePhysicalModems[portNumber];
        }

        public void Transmit(byte[] data, Address recevierAddress, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            for (int i = 0; i < data.Length; i += packetSize)
            {
                Packet packet = new();
                packet.DataBytes = [.. data.Skip(i).Take(packetSize)];
                //packet.DataString = Encoding.UTF8.GetString(packet.DataBytes.ToArray());
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = recevierAddress;
                packet.Transmitter = LocalModemAddress;
                packet.PacketNo = (byte)(i / packetSize);
                Transmit(packet);
            }
        }
        public void Transmit(string data, Address recevierAddress, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            for (int i = 0; i < data.Length; i += packetSize)
            {
                Packet packet = new();
                packet.DataBytes = [.. Encoding.UTF8.GetBytes(new string(data.ToCharArray().Skip(i).Take(packetSize).ToArray()).ToArray())];
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                ModemAPIDebugger.OutputDebugMessage(connectionId.ToString());
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = recevierAddress;
                packet.Transmitter = LocalModemAddress;
                packet.PacketNo = (byte)(i / packetSize);
                Transmit(packet);
            }
        }

        internal void Transmit(Packet packet)
        {
            if (SerialPacketizer == null)
            {
                throw new InvalidOperationException("communication with modem was not set while transmitting");
            }
            int packageRetransmitTimes = 0;
            
            IsPacketWriterReady = false;
            TransmitState state = TransmitState.WaitingForImmediateAnswer;
            SerialPacketizer.OnBufferReceived((data, kill) =>
            {
                switch (state)
                {
                    case TransmitState.WaitingForImmediateAnswer:
                        if (data.SequenceEqual(CIoCILDefaultPhrases.ImmediateAnswerFromNonLocal))
                        {
                            state = TransmitState.WaitingForNonLocalReady;
                            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.LocalIsReadyForTransmit);
                        }
                        break;

                    case TransmitState.WaitingForNonLocalReady:
                        if (data.SequenceEqual(CIoCILDefaultPhrases.NonLocalIsAcceptingReadinness))
                        {
                            state = TransmitState.WaitingForPacketTransmission;
                            SerialPacketizer.TransmitBuffer(PacketConverter.PacketToBytes(packet));
                        }
                        break;

                    case TransmitState.WaitingForPacketTransmission:
                        if (data.SequenceEqual(CIoCILDefaultPhrases.PacketRetransmittionRequired) && packageRetransmitTimes < MaxRetransmittionTimes)
                        {
                            SerialPacketizer.TransmitBuffer(PacketConverter.PacketToBytes(packet));
                            packageRetransmitTimes++;
                        }
                        else if (data.SequenceEqual(CIoCILDefaultPhrases.PacketTransmittionSuccess))
                        {
                            state = TransmitState.WaitingForTransmitComplete;
                            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.TransmittionCompleteRequest);
                        }

                        if (packageRetransmitTimes >= MaxRetransmittionTimes)
                        {
                            state = TransmitState.Error;
                            kill();
                        }
                        break;

                    case TransmitState.WaitingForTransmitComplete:
                        if (data.SequenceEqual(CIoCILDefaultPhrases.NonLocalAcceptsPacketSuccessTransmittion))
                        {
                            state = TransmitState.Done;
                            kill();
                        }
                        break;
                }
            });
            SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.RequestForTransmittingPacket);
            while (state != TransmitState.Done)
            {
                if (state == TransmitState.Error)
                {
                    throw new TooManyAttempsOfTransferringException("transmission failed");
                }
            }
        }

        internal void InternalAttachReceiveEvent(Action<Packet, Action> onReceive)
        {
            if (SerialPacketizer == null)
            {
                throw new InvalidOperationException("communication with modem was not set while applying receiving event");
            }
            bool isReaderReady = true;
            SerialPacketizer.OnBufferReceived(delegate (byte[] dataRode, Action kill)
            {
                // Main code for receiving data from CI:
                Packet packet = new();
                if (SerialPacketizer == null)
                {
                    kill();
                    throw new InvalidOperationException("communication with modem was not set while receiving the packet");
                }
                else if (dataRode.SequenceEqual(CIoCILDefaultPhrases.RequestForTransmittingPacket) && isReaderReady)
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.ImmediateAnswerFromNonLocal);
                    isReaderReady = false;
                }
                else if (dataRode.SequenceEqual(CIoCILDefaultPhrases.LocalIsReadyForTransmit))
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalIsAcceptingReadinness);
                }
                else if (dataRode.Take(4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader))
                {
                    packet = PacketConverter.BytesToPacket(dataRode);
                    if (packet.IsErrorWhileReading)
                    {
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.PacketRetransmittionRequired);
                    }
                    else
                    {
                        onReceive(packet, kill);
                        SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.PacketTransmittionSuccess);
                    }
                }
                else if (dataRode.SequenceEqual(CIoCILDefaultPhrases.TransmittionCompleteRequest))
                {
                    SerialPacketizer.TransmitBuffer(CIoCILDefaultPhrases.NonLocalAcceptsPacketSuccessTransmittion);
                    GenericPacketCounter++;
                    isReaderReady = true;
                }
            });
        }

        /*protected byte[] PacketToBytes(Packet packet)
        {
            // Create list for our brand new byte representation of our packet
            List<byte> prepare = new List<byte>();
            // Add header
            prepare.AddRange(CIoCILDefaultPhrases.PacketTransferHeader);
            // Create address variables for transmitter and receiver
            Address receiver = packet.Receiver;
            Address transmitter = packet.Transmitter;
            // Check if they are null or not
            if (receiver.AddressValue == null)
            {
                throw new NullAddressException("packet address receiver null");
            }
            if (transmitter.AddressValue == null)
            {
                throw new NullAddressException("packet address transmitter null");
            }
            // Get string for address value
            string receiverString = receiver.AddressValue;
            string transmitterString = transmitter.AddressValue;
            // Get data for query type and connection ID
            string queryType = packet.QueryType;
            uint connectid = packet.ConnectionID;
            Console.WriteLine("At PhysicalModem.cs at line 632 connectid is: " + packet.ConnectionID.ToString());
            // Add transmitter address, receiver address and also add query type
            prepare.Add((byte)receiverString.Length);
            prepare.AddRange(Encoding.UTF8.GetBytes(receiverString));
            prepare.Add((byte)transmitterString.Length);
            prepare.AddRange(Encoding.UTF8.GetBytes(transmitterString));
            prepare.Add((byte)queryType.Length);
            prepare.AddRange(Encoding.UTF8.GetBytes(queryType));
            // Convert connection ID to byte[]
            Span<byte> connectidBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(connectidBytes, connectid);
            // Add connnection ID
            prepare.AddRange(connectidBytes);
            // Add is packet last in sequence
            prepare.Add(packet.IsLastInSequence ? CIoCILDefaultPhrases.CIoCILTrue[0] : CIoCILDefaultPhrases.CIoCILFalse[0]);
            // Add packet transmission No.
            prepare.Add(packet.PacketNo);
            // Add packet data length
            if (packet.DataBytes.Count > 65536)
            {
                throw new ArgumentOutOfRangeException("packet length must be 0 thru 65535 bytes");
            }
            Span<byte> lengthOfPacketData = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(lengthOfPacketData, (ushort)packet.DataBytes.Count);
            prepare.AddRange(lengthOfPacketData);
            // Add packet data
            prepare.AddRange(packet.DataBytes);
            // Add packet metadata length
            if (packet.Metadata != null)
            {
                if (packet.Metadata.Length > 65536)
                {
                    throw new ArgumentOutOfRangeException("packet length must be 0 thru 65535 bytes");
                }
                Span<byte> lengthOfPacketMetadata = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(lengthOfPacketMetadata, (ushort)packet.DataBytes.Count);
                prepare.AddRange(lengthOfPacketMetadata);
            }
            else
            {
                prepare.AddRange([0, 0]);
            }
            // Add packet metadata
            if (packet.Metadata != null)
            {
                prepare.AddRange(Encoding.UTF8.GetBytes(packet.Metadata));
            }
            // Add packet CRC-32 (checksum)
            prepare.AddRange(Crc32.Hash(prepare.ToArray()));
            // Return builded packet byte[] representation
            return prepare.ToArray();
        }

        protected Packet BytesToPacket(byte[] bytes)
        {
            Packet prepare = new Packet();
            bool success = false;
            byte[] localHash = Crc32.Hash(bytes.SkipLast(4).ToArray());
            byte[] remoteHash = bytes.TakeLast(4).ToArray();
            if (bytes.Take(4).SequenceEqual(CIoCILDefaultPhrases.PacketTransferHeader) && localHash.SequenceEqual(remoteHash))
            {
                // Get receiver address for packet
                bytes = bytes.Skip(4).ToArray();
                byte addressReceiverLength = bytes[0];
                bytes = bytes.Skip(1).ToArray();
                byte[] addressReceiver = bytes.Take(addressReceiverLength).ToArray();
                bytes = bytes.Skip(addressReceiverLength).ToArray();
                // Get transmitter address for packet
                byte addressTransmitterLength = bytes[0];
                bytes = bytes.Skip(1).ToArray();
                byte[] addressTransmitter = bytes.Take(addressTransmitterLength).ToArray();
                bytes = bytes.Skip(addressTransmitterLength).ToArray();
                // Get query type for packet
                byte queryTypeLength = bytes[0];
                bytes = bytes.Skip(1).ToArray();
                byte[] queryType = bytes.Take(queryTypeLength).ToArray();
                bytes = bytes.Skip(queryTypeLength).ToArray();
                // Get connection ID for packet
                byte[] connectid = bytes.Take(4).ToArray();
                bytes = bytes.Skip(4).ToArray();
                // Get is last in packet sequence address for packet
                byte isLastInPacketSequence = bytes[0];
                bytes = bytes.Skip(1).ToArray();
                // Get packet transmission No.
                byte transmissionNo_ = bytes[0];
                bytes = bytes.Skip(1).ToArray();
                // Get data for packet
                ushort packetDataLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Take(2).ToArray());
                bytes = bytes.Skip(2).ToArray();
                byte[] packetData = bytes.Take(packetDataLength).ToArray();
                bytes = bytes.Skip(packetDataLength).ToArray();
                // Get metadata for packet
                ushort packetMetadataLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Take(2).ToArray());
                bytes = bytes.Skip(2).ToArray();
                byte[] packetMetadata = bytes.Take(packetMetadataLength).ToArray();
                bytes = bytes.Skip(packetMetadataLength).ToArray();
                // Preparing data for out brand new packet
                Address addressReceiver_ = new Address();
                addressReceiver_.AddressValue = new Address().AddressValue = Encoding.UTF8.GetString(addressReceiver);
                Address addressTransmitter_ = new Address();
                addressTransmitter_.AddressValue = new Address().AddressValue = Encoding.UTF8.GetString(addressTransmitter);
                string queryType_ = Encoding.UTF8.GetString(queryType);
                uint connectid_ = BinaryPrimitives.ReadUInt32BigEndian(connectid);
                bool isLastInPacketSequence_;
                if (isLastInPacketSequence == CIoCILDefaultPhrases.CIoCILTrue[0])
                {
                    isLastInPacketSequence_ = true;
                }
                else if (isLastInPacketSequence == CIoCILDefaultPhrases.CIoCILFalse[0])
                {
                    isLastInPacketSequence_ = false;
                }
                else
                {
                    goto end;
                }
                string packetDataString_ = Encoding.UTF8.GetString(packetData);
                List<byte> packetDataBytes_ = [.. packetData];
                string packetMetadata_ = Encoding.UTF8.GetString(packetMetadata);
                foreach (char c in packetMetadata_)
                {
                    if (PrintableCharacterList.IsCharacherWrong(c))
                    {
                        throw new WrongCharacherException("metadata must follow valid charlist");
                    }
                }
                // Move all of the readen data to the preparing packet
                prepare.Receiver = addressReceiver_;
                prepare.Transmitter = addressTransmitter_;
                prepare.QueryType = queryType_;
                prepare.ConnectionID = connectid_;
                prepare.IsLastInSequence = isLastInPacketSequence_;
                prepare.DataBytes = packetDataBytes_;
                prepare.DataString = packetDataString_;
                prepare.Metadata = packetMetadata_;
                prepare.PacketNo = transmissionNo_;
                success = true;
            }
        end:
            prepare.IsErrorWhileReading = !success;
            return prepare;
        }

        */
        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            InternalAttachReceiveEvent(delegate (Packet receivedData, Action stopCycle)
            {
                if (LocalModemAddress == null)
                {
                    throw new NullAddressException("cannot read packet local address null");
                }
                ModemAPIDebugger.OutputDebugMessage($"Address local: {LocalModemAddress.AddressValue} // Address receiver: {receivedData.Receiver.AddressValue}");
                if (receivedData.Receiver.AddressValue == LocalModemAddress.AddressValue)
                {
                    PacketBuffer[receivedData.PacketNo] = receivedData;
                    
                    // Обновляем длину буфера только когда получаем последний пакет
                    if (receivedData.IsLastInSequence)
                    {
                        //PacketBufferActualLength = receivedData.PacketNo + 1;
                    }
                    
                    bool isEverythingReceived = true;
                    if (PacketBufferActualLength == 0)
                    {
                        isEverythingReceived = false;
                    }
                    else
                    {
                        foreach (Packet elem in PacketBuffer.Take(PacketBufferActualLength))
                        {
                            if (elem == null)
                            {
                                isEverythingReceived = false;
                                break;
                            }
                        }
                    }
                    if (isEverythingReceived && !IsProcessingMessage)
                    {
                        IsProcessingMessage = true; // Блокируем повторную обработку
                        Packet[] actualPacketBufferValue = PacketBuffer.Take(PacketBufferActualLength).ToArray();
                        
                        // Очищаем буферы перед обработкой нового сообщения
                        MessageBuffer = "";
                        MessageBufferBytesCursor = 0;
                        Array.Clear(MessageBufferBytes);
                        
                        foreach (Packet elem in actualPacketBufferValue)
                        {
                            foreach (byte b in elem.DataBytes)
                            {
                                MessageBufferBytes[MessageBufferBytesCursor] = b;
                                MessageBufferBytesCursor++;
                            }
                            if (elem.IsLastInSequence)
                            {
                                onReceive(
                                    new(
                                        actualPacketBufferValue[actualPacketBufferValue.Length - 1].Transmitter,
                                        actualPacketBufferValue[actualPacketBufferValue.Length - 1].Receiver,
                                        MessageBufferBytes.Take(MessageBufferBytesCursor).ToList(),
                                        MessageBuffer,
                                        elem.Metadata,
                                        elem.QueryType,
                                        elem.ConnectionID
                                    ),
                                    stopCycle
                                );
                                Array.Clear(MessageBufferBytes);
                                MessageBuffer = "";
                                MessageBufferBytesCursor = 0;
                                Array.Clear(PacketBuffer);
                                IsProcessingMessage = false; // Разблокируем обработку
                                break; // Выходим из цикла после обработки последнего пакета
                            }
                        }
                    }
                }
            });
        }

        public void CreateSlave(string serialPortNumber, int smlaLength, int portSpeed, int lineSpeed, BPTx bptx, PhysicalModemCommandList modemCommandList, PhysicalModemEventList modemEventList, bool useATcommands)
        {
            SerialPort preparedPort = new SerialPort(serialPortNumber);
            preparedPort.BaudRate = portSpeed;
            preparedPort.Parity = Parity.None;
            preparedPort.StopBits = StopBits.One;
            preparedPort.DataBits = 8;
            preparedPort.Handshake = Handshake.None;
            preparedPort.DtrEnable = true;
            preparedPort.RtsEnable = true;
            AnsweringPhysicalModem prepare = new(preparedPort);
            if (prepare.CommunicationalSerialPort == null)
            {
                throw new NullReferenceException("port");
            }
            prepare.CommunicationalSerialPort.Open();
            prepare.BitsPerTick = bptx;
            prepare.CommunicationalPort = serialPortNumber;
            prepare.EventList = modemEventList;
            prepare.CommandList = modemCommandList;
            prepare.LineSpeed = lineSpeed;
            prepare.SMLALength = smlaLength;
            prepare.PortSpeed = portSpeed;
            prepare.UseATCommands = useATcommands;
            prepare.AttachConnectEvent(delegate ()
            {
                AddressPortPairs.Add(prepare.GetSubscriberAddress(), serialPortNumber);
            });
            InternalAttachReceiveEvent(delegate (Packet receivedPacket, Action stopLoop)
            {
                if (AddressPortPairs.ContainsKey(receivedPacket.Receiver))
                {
                    SlavePhysicalModems[AddressPortPairs[receivedPacket.Receiver]].TransmitToSubscriber(receivedPacket);
                }
            });
            SlavePhysicalModems.Add(serialPortNumber, prepare);
        }

        public bool IsAddressSMLA()
        {
            return AddressWasSetByUpperNode;
        }

        public Task DeleteLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string metadata, ulong blockId)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            throw new NotImplementedException();
        }

        public void CreateSlave(string serialPortNumber, int portSpeed, L1Types wireType, bool useMoreStableSerialPacket)
        {
            throw new NotImplementedException();
        }

        Dictionary<string, LowLatencyAnsweringPhysicalModem> IAnswerHost.GetAnsweringAllModems()
        {
            throw new NotImplementedException();
        }

        LowLatencyAnsweringPhysicalModem IAnswerHost.GetAnsweringModem(string serialPortNumber)
        {
            throw new NotImplementedException();
        }

        public void AttachPacketRouter(IPacketRouter router)
        {
            throw new NotImplementedException();
        }

        public void LowLevelTransmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null)
        {
            throw new NotImplementedException();
        }
    }
}
