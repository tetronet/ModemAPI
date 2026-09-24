using SocketIOClient;
using System.Text;
using System.IO.Ports;
using System.IO.Hashing;
using System.Net.WebSockets;
using System.Buffers;
using System.Text.Json;

namespace ModemAPI
{
    public class VirtualModem : IModem, IAnswerHost
    {
        private string Cias = "";
        private string UploadLargeMessagesTo = "";
        private string DownloadLargeMessagesFrom = "";
        private string DeleteLargeMessages = "";
        private SocketIO? TransmitterDevice = null;
        private ClientWebSocket? RawTransmitterDevice = null;
        private object PacketBufferLock = new object();
        private SortedDictionary<ulong, SortedDictionary<ulong, Packet>> PacketBuffer = new SortedDictionary<ulong, SortedDictionary<ulong, Packet>>();
        private byte[]? MessageBufferBytes = null;
        private int MessageBufferBytesCursor = 0;
        private event Action<Packet, Action> InternalPacketEvents = delegate { };
        private Dictionary<string, LowLatencyAnsweringPhysicalModem> SlavePhysicalModems = []; // Slave physical modem list
        private Dictionary<string, string> AddressPortPairs = []; // For slave physical modems
        private IPacketRouter? InnerRouter = null;
        private bool AddressWasSetByUpperNode = false; // SMLA or not
        private bool IsProcessingMessage = false; // Flag to prevent working on the same packet more than 1 time.
        private bool InternalModemConnected = false;
        private bool WebSocketOrSocketIO = false; // false if socket.io, true if websocket
        internal List<string> TakenNumbers = []; // Address Values, that were already taken by the Slave Modems
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        public bool IsModemConnected { get { return InternalModemConnected && LocalModemAddress != null && !LocalModemAddress.IsEmpty; } }
        /// <summary>
        /// How many times SMLA address will be regenerated if it's already taken by a slave.
        /// </summary>
        public int SMLAMaxRetries = 1000000;
        public string SlaveLargeMessageExchangeDirectory { get; set; }
        /// <summary>
        /// Connect to the tetronet as an address-machine. If true, modem will be able to have slaves, otherwise, no.
        /// </summary>
        public bool ConnectAsAnAddressMachine { get; set; }
        /// <summary>
        /// Length of address postfix for the slave modems.
        /// </summary>
        public int SMLALength { get; set; }
        /// <summary>
        /// How many times this modem will try to transmit connection phrase.
        /// </summary>
        public int MaxReinitializeAttempts { get; set; }
        /// <summary>
        /// How many packets was received or transmitted by this modem.
        /// </summary>
        public long GenericPacketCounter { get; private set; }
        /// <summary>
        /// Local tetronet address of this modem.
        /// </summary>
        public Address? LocalModemAddress { get; set; }
        /// <summary>
        /// Gets called once every reconnection retry for the RawWS transport.
        /// </summary>
        public Action OnReconnectWebsocket = delegate { };
        /// <summary>
        /// Gets called on a successful reconnect to the RawWS transport.
        /// </summary>
        public Action OnSuccessReconnectWebsocket = delegate { };
        /// <summary>
        /// Creates a new instance of this Virtual Modem.
        /// </summary>
        /// <param name="domain">CIAS (Copybook Internet Access Server) domain name or IP Address</param>
        /// <param name="innerLineParams">DTO carrying address (use new LineParams() to get an address from tetronet)</param>
        /// <param name="uploadLmsTo">Used to upload Large Messages to the tetronet</param>
        /// <param name="downloadLmsFrom">Used to get Large Messages from the tetronet</param>
        /// <param name="deleteLmsApi">Used to delete Large Messages from the tetronet</param>
        public VirtualModem(string domain, LineParams innerLineParams, string uploadLmsTo = "", string downloadLmsFrom = "", string deleteLmsApi = "", bool rawWs = false)
        {
            LocalModemAddress = innerLineParams.ModemLocalAddress;
            if (!rawWs)
            {
                TransmitterDevice = new SocketIO(new(domain), new SocketIOOptions());
            }
            else
            {
                RawTransmitterDevice = new ClientWebSocket();
            }
            WebSocketOrSocketIO = rawWs;
            UploadLargeMessagesTo = uploadLmsTo;
            DownloadLargeMessagesFrom = downloadLmsFrom;
            DeleteLargeMessages = deleteLmsApi;
            SlaveLargeMessageExchangeDirectory = "";
            Cias = domain;
        }
        /// <summary>
        /// Attach an event when packet queue is received from the tetronet.
        /// Not recommened for high-performance systems, as it uses not the most optimal
        /// network defragmentation options. For high perfoming systems it is better to
        /// use the AttachReceiveEventNoUnfragment to get raw network packets.
        /// </summary>
        /// <param name="onReceive">Delegate, that will be triggered after all packets from the transmitted queue are received from the tetronet</param>
        /// <exception cref="NullAddressException">If address of that modem is null</exception>
        /// <exception cref="NotImplementedException">I'm too lazy to implement this =]</exception>
        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            InternalAttachReceiveEvent(delegate (Packet receivedData, Action stopLoop)
            {
                if (LocalModemAddress == null)
                {
                    throw new NullAddressException("cannot read packet local address null");
                }
                if (receivedData.Receiver.AddressValue == LocalModemAddress.AddressValue)
                {
                    lock (PacketBufferLock)
                    {
                        if (!PacketBuffer.ContainsKey(receivedData.MessageId))
                        {
                            PacketBuffer.Add(receivedData.MessageId, new SortedDictionary<ulong, Packet>());
                        }
                        PacketBuffer[receivedData.MessageId].Add(receivedData.PacketNo, receivedData);
                    }
                    bool isEverythingReceived = false;
                    if (receivedData.IsLastInSequence)
                    {
                        //PacketBufferActualLength = receivedData.PacketNo + 1;
                        int totalLength = 0;
                        lock (PacketBufferLock)
                        {
                            foreach (Packet p in PacketBuffer[receivedData.MessageId].Values.ToArray())
                            {
                                totalLength += p.DataBytes.Length;
                            }
                        }
                        MessageBufferBytes = new byte[totalLength];
                        isEverythingReceived = true;
                    }
                    
                    
                    /*if (PacketBufferActualLength == 0)
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
                    }*/
                    if (isEverythingReceived && !IsProcessingMessage)
                    {
                        lock (PacketBufferLock)
                        {
                            KeyValuePair<ulong, SortedDictionary<ulong, Packet>>[] packBufferDump = PacketBuffer.ToArray();
                            foreach (KeyValuePair<ulong, SortedDictionary<ulong, Packet>> packQueue in packBufferDump)
                            {
                                IsProcessingMessage = true;
                                Packet[] actualPacketBufferValue = [.. PacketBuffer[packQueue.Key].Values];
                                //Console.WriteLine($"Processing {actualPacketBufferValue.Length} packets, MessageBuffer length: {MessageBuffer.Length}");
                                MessageBufferBytesCursor = 0;
                                if (MessageBufferBytes != null)
                                {
                                    Array.Clear(MessageBufferBytes);
                                }
                        
                        
                                foreach (Packet elem in actualPacketBufferValue)
                                {
                                    //Console.WriteLine($"Processing packet {elem.PacketNo}");
                                    //Console.WriteLine($"MessageBuffer after: '{MessageBuffer}'");
                                    if (MessageBufferBytes == null)
                                    {
                                        int totalLength = 0;
                                        lock (PacketBufferLock)
                                        {
                                            if (PacketBuffer.TryGetValue(receivedData.MessageId, out SortedDictionary<ulong, Packet>? packbuf))
                                            {
                                                foreach (Packet p in packbuf.Values.ToArray())
                                                {
                                                    totalLength += p.DataBytes.Length;
                                                }
                                            }
                                            
                                        }
                                        MessageBufferBytes = new byte[totalLength];
                                        //Console.WriteLine("msg buf bytes null, create new buffer with length");
                                        //continue;
                                    }
                                    foreach (byte b in elem.DataBytes)
                                    {
                                        if (MessageBufferBytesCursor > MessageBufferBytes.Length)
                                        {
                                            break;
                                        }
                                        try
                                        {
                                            MessageBufferBytes[MessageBufferBytesCursor] = b;
                                            MessageBufferBytesCursor++;
                                        }
                                        catch
                                        {
                                            IsProcessingMessage = false;
                                            Array.Clear(MessageBufferBytes);
                                            MessageBufferBytesCursor = 0;
                                            return;
                                        }
                                    }
                            
                                    if (elem.IsLastInSequence)
                                    {
                                        //Console.WriteLine($"Received all bytes for this message");
                                        onReceive(
                                            new(
                                                actualPacketBufferValue[^1].Transmitter,
                                                actualPacketBufferValue[^1].Receiver,
                                                [.. MessageBufferBytes.Take(MessageBufferBytesCursor)],
                                                Encoding.UTF8.GetString([.. MessageBufferBytes.Take(MessageBufferBytesCursor)]),
                                                elem.Metadata,
                                                elem.QueryType,
                                                elem.ConnectionID
                                            ),
                                            delegate () { throw new NotImplementedException("closing event in this version of modem api is not implemented"); }
                                        );
                                        MessageBufferBytes = null;
                                        MessageBufferBytesCursor = 0;
                                        PacketBuffer.Remove(packQueue.Key);
                                        IsProcessingMessage = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            });
        }
        /// <summary>
        /// Creates a new slave modem for that Virtual Modem. Works only with L1_SERIAL wireType.
        /// </summary>
        /// <param name="serialPortNumber">Serial port of that slave</param>
        /// <param name="portSpeed">Bitrate</param>
        /// <param name="wireType">L1 type</param>
        /// <exception cref="InvalidOperationException">Happens if slave being created without connecting as an Address Machine</exception>
        /// <exception cref="NullReferenceException">Happens if serial port of that modem is NULL</exception>
        /// <exception cref="NullAddressException">Happens if slave is being created while the tetronet address of that modem is NULL</exception>
        public void CreateSlave(string serialPortNumber, int portSpeed, L1Types wireType, bool useMoreStableSerialPacket = false)
        {
            if (!ConnectAsAnAddressMachine)
            {
                throw new InvalidOperationException("to create slaves this modem must be connected as an address machine");
            }
            LowLatencyAnsweringPhysicalModem prepare;
            if (wireType == L1Types.L1_SERIAL)
            {
                prepare = new(serialPortNumber, portSpeed, wireType, SlaveLargeMessageExchangeDirectory, useMoreStableSerialPacket);
            }
            else
            {
                throw new InvalidOperationException("use CreateSlave(Stream, String) method for TCP");
            }
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("modem is not allowed to have slaves if modem does not own an address");
            }
            prepare.AttachConnectEvent(delegate ()
            {
                if (!AddressPortPairs.ContainsKey(prepare.GetSubscriberAddress().AddressValue ?? ""))
                {
                    // Add out subscriber to the data base (needed for routing)
                    AddressPortPairs.Add(prepare.GetSubscriberAddress().AddressValue ?? "", serialPortNumber);
                }
            });
            prepare.AddSMLARequest(SMLARequest);
            prepare.ModemName = serialPortNumber;
            prepare.AttachIncomingEvent(prepare.Answer);
            prepare.InternalAttachReceiveEvent(delegate (Packet data, Action k)
            {
                //Console.WriteLine("==== [at VirtualModem.cs at line 169] ====");
                //Console.WriteLine(Environment.StackTrace);
                //ModemAPIDebugger.PrintOutPacket(data);
                if (InnerRouter != null && InnerRouter.Decide(new(data.Transmitter, data.Receiver), PacketTransmissionDirection.FromUpperToLower) == PacketRouterDecidion.ForwardDown)
                {
                    Transmit(data);
                    return;
                }
                if (InnerRouter != null && InnerRouter.Decide(new(data.Transmitter, data.Receiver), PacketTransmissionDirection.FromUpperToLower) == PacketRouterDecidion.ForwardInInnerNetwork)
                {
                    if (AddressPortPairs.TryGetValue(data.Receiver.AddressValue?[..(LocalModemAddress.Length + 1 + SMLALength)] ?? "", out string? value))
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(data);
                        ModemAPIDebugger.OutputDebugMessage("decidion: forward to the inner network");
                        return;
                    }
                }
            });
            InternalAttachReceiveEvent(delegate (Packet receivedPacket, Action stopLoop)
            {
                ModemAPIDebugger.OutputDebugMessage($"VirtualModem.cs: forwarding data to slave, dest addr: {receivedPacket.Receiver}");
                ModemAPIDebugger.OutputDebugMessage("Forwarding to port: " + AddressPortPairs.GetValueOrDefault(receivedPacket.Receiver.AddressValue ?? ""));
                if (AddressPortPairs.TryGetValue(receivedPacket.Receiver.AddressValue ?? "", out string? value))
                {
                    ModemAPIDebugger.OutputDebugMessage("forward ok");
                    receivedPacket.Metadata ??= "";
                    if (InnerRouter != null && InnerRouter.Decide(new(receivedPacket.Transmitter, receivedPacket.Receiver), PacketTransmissionDirection.FromLowerToUpper) == PacketRouterDecidion.ForwardInInnerNetwork)
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(receivedPacket);
                        ModemAPIDebugger.OutputDebugMessage("decidion: forward to the inner network");
                        return;
                    }
                    // TODO implement this
                    /*if (InnerRouter != null && receivedPacket.Receiver.ToString().StartsWith('*') && !receivedPacket.Receiver.ToString().StartsWith("**"))
                    {
                        InnerRouter.WorkOnReceiveByRouterPacket(receivedPacket);
                        SlavePhysicalModems[value].TransmitToSubscriber(receivedPacket);
                        return;
                    }
                    if (InnerRouter != null && receivedPacket.Receiver.ToString().StartsWith("**"))
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(InnerRouter.ModifyModifiablePacket(receivedPacket) ?? receivedPacket);
                        return;
                    }*/
                }
                else
                {
                    ModemAPIDebugger.OutputDebugMessage("forward fail");
                }
            });
            SlavePhysicalModems.Add(serialPortNumber, prepare);
        }
        /// <summary>
        /// Creates a new slave modem for that Virtual Modem.
        /// </summary>
        /// <param name="baseStream">Stream, which will be used to communicate between modems</param>
        /// <param name="modemName">Modem name, which will be used to get the modem</param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NullAddressException"></exception>
        public LowLatencyAnsweringPhysicalModem CreateSlave(Stream baseStream, string modemName)
        {
            if (!ConnectAsAnAddressMachine)
            {
                throw new InvalidOperationException("to create slaves this modem must be connected as an address machine");
            }
            LowLatencyAnsweringPhysicalModem prepare = new(baseStream);
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("modem is not allowed to have slaves if modem does not own an address");
            }
            prepare.AttachConnectEvent(delegate ()
            {
                if (!AddressPortPairs.ContainsKey(prepare.GetSubscriberAddress().AddressValue ?? ""))
                {
                    // Add out subscriber to the data base (needed for routing)
                    AddressPortPairs.Add(prepare.GetSubscriberAddress().AddressValue ?? "", modemName);
                }
            });
            prepare.AddSMLARequest(SMLARequest);
            prepare.ModemName = modemName;
            prepare.AttachIncomingEvent(prepare.Answer);
            prepare.InternalAttachReceiveEvent(delegate (Packet data, Action k)
            {
                //Console.WriteLine("==== [at VirtualModem.cs at line 169] ====");
                //Console.WriteLine(Environment.StackTrace);
                //ModemAPIDebugger.PrintOutPacket(data);
                if (InnerRouter != null && InnerRouter.Decide(new(data.Transmitter, data.Receiver), PacketTransmissionDirection.FromUpperToLower) == PacketRouterDecidion.ForwardDown)
                {
                    Transmit(data);
                    return;
                }
                if (InnerRouter != null && InnerRouter.Decide(new(data.Transmitter, data.Receiver), PacketTransmissionDirection.FromUpperToLower) == PacketRouterDecidion.ForwardInInnerNetwork)
                {
                    if (AddressPortPairs.TryGetValue(data.Receiver.AddressValue ?? "", out string? value))
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(data);
                        ModemAPIDebugger.OutputDebugMessage("decidion: forward to the inner network");
                        return;
                    }
                }
            });
            InternalAttachReceiveEvent(delegate (Packet receivedPacket, Action stopLoop)
            {
                ModemAPIDebugger.OutputDebugMessage($"VirtualModem.cs: forwarding data to slave, dest addr: {receivedPacket.Receiver}");
                ModemAPIDebugger.OutputDebugMessage("Forwarding to port: " + AddressPortPairs.GetValueOrDefault(receivedPacket.Receiver.AddressValue ?? ""));
                if (AddressPortPairs.TryGetValue(receivedPacket.Receiver.AddressValue ?? "", out string? value))
                {
                    ModemAPIDebugger.OutputDebugMessage("forward ok");
                    receivedPacket.Metadata ??= "";
                    if (InnerRouter != null && InnerRouter.Decide(new(receivedPacket.Transmitter, receivedPacket.Receiver), PacketTransmissionDirection.FromLowerToUpper) == PacketRouterDecidion.ForwardInInnerNetwork)
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(receivedPacket);
                        ModemAPIDebugger.OutputDebugMessage("decidion: forward to the inner network");
                        return;
                    }
                    // TODO implement this
                    /*if (InnerRouter != null && receivedPacket.Receiver.ToString().StartsWith('*') && !receivedPacket.Receiver.ToString().StartsWith("**"))
                    {
                        InnerRouter.WorkOnReceiveByRouterPacket(receivedPacket);
                        SlavePhysicalModems[value].TransmitToSubscriber(receivedPacket);
                        return;
                    }
                    if (InnerRouter != null && receivedPacket.Receiver.ToString().StartsWith("**"))
                    {
                        SlavePhysicalModems[value].TransmitToSubscriber(InnerRouter.ModifyModifiablePacket(receivedPacket) ?? receivedPacket);
                        return;
                    }*/
                }
                else
                {
                    ModemAPIDebugger.OutputDebugMessage("forward fail");
                }
            });
            SlavePhysicalModems.Add(modemName, prepare);
            return prepare;
        }
        /// <summary>
        /// Connects this modem to the tetronet.
        /// </summary>
        /// <exception cref="NullReferenceException">Happens when something goes extremely wrong, and SocketIO client is NULL</exception>
        public void Dial()
        {
            if (!WebSocketOrSocketIO)
            {
                if (TransmitterDevice == null)
                {
                    throw new NullReferenceException("there are no transmitter device in this modem");
                }
                string localModemAddressString = "";
                if (LocalModemAddress == null || LocalModemAddress.AddressValue == null)
                {
                    localModemAddressString = "0";
                }
                else
                {
                    localModemAddressString = LocalModemAddress.AddressValue;
                }
                TransmitterDevice.On("get-addr", async delegate (IEventContext context)
                {
                    await TransmitterDevice.EmitAsync("self-addr", [localModemAddressString]);
                    return;
                });
                TransmitterDevice.On("set-modem-local-address", delegate (IEventContext dto)
                {
                    string[]? data = dto.GetValue<string[]>(0);
                    if (data == null)
                    {
                        throw new ConnectionFailedException();
                    }
                    localModemAddressString = string.Join("-", data);
                    LocalModemAddress = new Address(localModemAddressString);
                    AddressWasSetByUpperNode = true;
                    return Task.CompletedTask;
                });
                TransmitterDevice.On("addr-ok", delegate (IEventContext dto)
                {
                    AddressWasSetByUpperNode = false;
                    return Task.CompletedTask;
                });
                TransmitterDevice.On("get-device-type", async delegate (IEventContext dto)
                {
                    await TransmitterDevice.EmitAsync("device-type", [ConnectAsAnAddressMachine ? "address-machine" : "modem"]);
                    InternalModemConnected = true;
                    return;
                });
                TransmitterDevice.On("data-transmission", async delegate (IEventContext dto)
                {
                    ModemAPIDebugger.OutputDebugMessage("virtmodem received packet");
                    if (LocalModemAddress == null || LocalModemAddress.AddressValue == null)
                    {
                        throw new NullAddressException("address not exist");
                    }
                    WebSocketMessage? receivedMessage = dto.GetValue<WebSocketMessage>(0);
                    if (receivedMessage == null)
                    {
                        return;
                    }
                    Packet receivedPacket = PacketConverter.WebSocketMessageToPacket(receivedMessage);
                    ModemAPIDebugger.OutputDebugMessage(receivedPacket.Receiver.AddressValue ?? "virtmodem no receiver");
                    ModemAPIDebugger.OutputDebugMessage(receivedPacket.Transmitter.AddressValue ?? "virtmodem no transmitter");
                    if (receivedPacket.IsErrorWhileReading == true)
                    {
                        ModemAPIDebugger.OutputDebugMessage("error while receiving packet");
                        return;
                    }
                    string? receiverAddress = receivedPacket.Receiver.AddressValue;
                    string? transmitterAddress = receivedPacket.Transmitter.AddressValue;
                    if (receiverAddress == null || transmitterAddress == null)
                    {
                        //Console.WriteLine("tx or rx null");
                        return;
                    }
                    if (receiverAddress.StartsWith(LocalModemAddress.AddressValue) && !transmitterAddress.StartsWith(LocalModemAddress.AddressValue))
                    {
                        //Console.WriteLine("actually working");
                        InternalPacketEvents(receivedPacket, delegate () { throw new NotImplementedException("was unable to remove the event"); });
                        GenericPacketCounter++;
                    }
                });
                TransmitterDevice.ConnectAsync();
            }
            else
            {
                if (RawTransmitterDevice == null)
                {
                    throw new NullReferenceException("there are no transmitter device in this modem");
                }
                string localModemAddressString = "";
                if (LocalModemAddress == null || LocalModemAddress.AddressValue == null)
                {
                    localModemAddressString = "0";
                }
                else
                {
                    localModemAddressString = LocalModemAddress.AddressValue;
                }
                Task.Run(async delegate ()
                {
                    try
                    {
                        await RawTransmitterDevice.ConnectAsync(new Uri(Cias), default);
                    }
                    catch
                    {
                        await ReconnectAsync();
                    }
                    byte[] webSocketReceiveBuffer = new byte[500000];
                    while (true)
                    {
                        try
                        {
                            WebSocketReceiveResult rslt;
                            try
                            {
                                rslt = await RawTransmitterDevice.ReceiveAsync(webSocketReceiveBuffer, default);
                            }
                            catch
                            {
                                await ReconnectAsync();
                                continue;
                            }
                            if (rslt.MessageType == WebSocketMessageType.Close) // connection closed
                            {
                                await ReconnectAsync();
                                continue;
                            }
                            Span<byte> message = webSocketReceiveBuffer.AsSpan(0, rslt.Count);
                            string textMessage = Encoding.ASCII.GetString(message);
                            int eventNameStart = textMessage.IndexOf('"');
                            int eventNameEnd = textMessage.IndexOf('"', eventNameStart + 1);
                            string eventName = textMessage[(eventNameStart + 1)..eventNameEnd].Trim();
                            ModemAPIDebugger.OutputDebugMessage("RECEIVED EVENT!!! " + eventName);
                            switch (eventName)
                            {
                                case "get-addr":
                                    await RawTransmitterDevice.SendAsync(Encoding.ASCII.GetBytes($"[\"self-addr\",\"{localModemAddressString}\"]"), WebSocketMessageType.Text, true, default);
                                    break;
                                case "set-modem-local-address":
                                    List<JsonElement>? stringsSmla = JsonSerializer.Deserialize<List<JsonElement>>(textMessage) ?? throw new ConnectionFailedException("during SMLA there was no JSON data");
                                    string[]? data = stringsSmla[1].Deserialize<string[]>() ?? throw new ConnectionFailedException();
                                    localModemAddressString = string.Join("-", data);
                                    LocalModemAddress = new Address(localModemAddressString);
                                    AddressWasSetByUpperNode = true;
                                    break;
                                case "addr-ok":
                                    AddressWasSetByUpperNode = false;
                                    break;
                                case "get-device-type":
                                    await RawTransmitterDevice.SendAsync(Encoding.ASCII.GetBytes($"[\"device-type\",\"{(ConnectAsAnAddressMachine ? "address-machine" : "modem")}\"]"), WebSocketMessageType.Text, true, default);
                                    InternalModemConnected = true;
                                    break;
                                case "data-transmission":
                                    ModemAPIDebugger.OutputDebugMessage("virtmodem received packet");
                                    if (LocalModemAddress == null || LocalModemAddress.AddressValue == null)
                                    {
                                        break;
                                    }
                                    List<JsonElement>? temp = JsonSerializer.Deserialize<List<JsonElement>>(textMessage);
                                    if (temp == null) break;

                                    WebSocketMessage? receivedMessage = temp[1].Deserialize<WebSocketMessage>();
                                    if (receivedMessage == null)
                                    {
                                        break;
                                    }
                                    Packet receivedPacket = PacketConverter.WebSocketMessageToPacket(receivedMessage);
                                    ModemAPIDebugger.OutputDebugMessage(receivedPacket.Receiver.AddressValue ?? "virtmodem no receiver");
                                    ModemAPIDebugger.OutputDebugMessage(receivedPacket.Transmitter.AddressValue ?? "virtmodem no transmitter");
                                    if (receivedPacket.IsErrorWhileReading == true)
                                    {
                                        ModemAPIDebugger.OutputDebugMessage("error while receiving packet");
                                        break;
                                    }
                                    string? receiverAddress = receivedPacket.Receiver.AddressValue;
                                    string? transmitterAddress = receivedPacket.Transmitter.AddressValue;
                                    if (receiverAddress == null || transmitterAddress == null)
                                    {
                                        //Console.WriteLine("tx or rx null");
                                        break;
                                    }
                                    if (receiverAddress.StartsWith(LocalModemAddress.AddressValue) && !transmitterAddress.StartsWith(LocalModemAddress.AddressValue))
                                    {
                                        //Console.WriteLine("actually working");
                                        InternalPacketEvents(receivedPacket, delegate () { throw new NotImplementedException("was unable to remove the event"); });
                                        GenericPacketCounter++;
                                    }
                                    break;
                            }
                        }
                        catch
                        {
                            await Task.Delay(100);
                        }
                    }
                });
            }
        }
        /// <summary>
        /// Disconnects this modem from the tetronet.
        /// </summary>
        /// <param name="carefulMode">Not used for Virtual Modems</param>
        /// <exception cref="VirtualModemsCannotDisconnectCarefullyException">Tetronet over Socket.IO does not support disconnection phrases</exception>
        /// <exception cref="NullReferenceException">Happens when something goes extremely wrong, and SocketIO client is NULL</exception>
        public void Drop(bool carefulMode)
        {
            if (carefulMode)
            {
                throw new VirtualModemsCannotDisconnectCarefullyException("this action is not supported by virtual modem communication protocols");
            }
            if (TransmitterDevice == null)
            {
                throw new NullReferenceException("transmitter device was null");
            }
            TransmitterDevice.DisconnectAsync();
        }
        /// <summary>
        /// Returns all slaves for this modem.
        /// </summary>
        /// <returns>Dictionary with "Serial Port Name: Slave Modem" pairs</returns>
        public Dictionary<string, LowLatencyAnsweringPhysicalModem> GetAnsweringAllModems()
        {
            return SlavePhysicalModems;
        }
        /// <summary>
        /// Returns a slave modem by it's serial port number.
        /// </summary>
        /// <param name="serialPortNumber">Serial Port Name, e.g. /dev/tty or COM</param>
        /// <returns>The slave modem</returns>
        public LowLatencyAnsweringPhysicalModem GetAnsweringModem(string serialPortNumber)
        {
            return SlavePhysicalModems[serialPortNumber];
        }
        /// <summary>
        /// Transmits a packet or a packet queue depending on Packet Size and length of data.
        /// </summary>
        /// <param name="data">Data to transmit</param>
        /// <param name="address">Destination address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectionId">Tetronet port</param>
        /// <param name="metadata">Metadata (doesn't work for some reason)</param>
        /// <param name="packetSize">Max amount of bytes in a single packet in a packet queue</param>
        /// <param name="delay">Delay between transmitting each packet in the queue, useful for overloaded/slow network</param>
        /// <exception cref="NullAddressException">Happens when transmitting modem does not have any tetronet address</exception>
        public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            ulong mid = (ulong)Random.Shared.NextInt64();
            for (int i = 0; i < data.Length; i += packetSize)
            {
                //Console.WriteLine("at line 223 iterator is : " + i);
                Packet packet = new()
                {
                    DataBytes = [.. data.Skip(i).Take(packetSize)]
                };
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = address;
                packet.Transmitter = LocalModemAddress;
                packet.PacketNo = (byte)(i / packetSize);
                packet.MessageId = mid;
                Transmit(packet);
                Thread.Sleep(delay);
            }
        }
        /// <summary>
        /// Transmits a packet or a packet queue depending on Packet Size and length of data.
        /// </summary>
        /// <param name="data">Data to transmit</param>
        /// <param name="address">Destination address</param>
        /// <param name="queryType">Query type</param>
        /// <param name="connectionId">Tetronet port</param>
        /// <param name="metadata">Metadata (doesn't work for some reason)</param>
        /// <param name="packetSize">Max amount of bytes in a single packet in a packet queue</param>
        /// <param name="delay">Delay between transmitting each packet in the queue, useful for overloaded/slow network</param>
        /// <exception cref="NullAddressException">Happens when transmitting modem does not have any tetronet address</exception>
        public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to transmit, receive or exchange data, modem must have a local address");
            }
            ulong mid = (ulong)Random.Shared.NextInt64();
            for (int i = 0; i < data.Length; i += packetSize)
            {
                //Console.WriteLine("at line 247 iterator is : " + i);
                Packet? packet = new();
                packet.DataBytes = [.. Encoding.UTF8.GetBytes(new string([.. data.ToCharArray().Skip(i).Take(packetSize)]))];
                packet.QueryType = queryType;
                packet.ConnectionID = connectionId;
                packet.Metadata = metadata;
                packet.IsLastInSequence = (i / packetSize) == (Math.Ceiling((double)data.Length / packetSize) - 1);
                packet.Receiver = address;
                packet.Transmitter = LocalModemAddress;
                packet.PacketNo = (byte)(i / packetSize);
                packet.MessageId = mid;
                //Console.WriteLine("the value at line 258 is : " + (i + packetSize) + " and packet estimated packsize will be : " + packetSize);
                //Console.WriteLine("the value at line 259 is : " + new string(data.ToCharArray().Skip(i).Take(packetSize).ToArray()));
                Transmit(packet);
                Thread.Sleep(delay);
            }
        }
        /// <summary>
        /// Returns true if Local Address for this modem was set by the Remote Address Machine.
        /// </summary>
        /// <returns>true if Loca Address was set by remote; otherwise - false</returns>
        public bool IsAddressSMLA()
        {
            return AddressWasSetByUpperNode;
        }
        /// <summary>
        /// Inner-method for transmitting a single packet to the tetronet.
        /// </summary>
        /// <param name="packet">Packet for transmitting</param>
        /// <exception cref="InvalidOperationException">When something goes wrong</exception>
        internal void Transmit(Packet packet)
        {
            if (!WebSocketOrSocketIO)
            {
                if (TransmitterDevice == null)
                {
                    throw new InvalidOperationException("transmitter device null");
                }
                TransmitterDevice.EmitAsync("data-transmission", [PacketConverter.PacketToWebSocketMessage(packet)]).Wait();
            }
            else
            {
                if (RawTransmitterDevice == null)
                {
                    throw new InvalidOperationException("transmitter device null");
                }
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new object[] { "data-transmission", PacketConverter.PacketToWebSocketMessage(packet) });
                try
                {
                    RawTransmitterDevice.SendAsync(payload, WebSocketMessageType.Text, true, default).Wait();
                }
                catch (OperationCanceledException)
                {
                    ReconnectAsync().Wait();
                }
                catch (InvalidOperationException)
                {
                    ReconnectAsync().Wait();
                }
            }
            GenericPacketCounter++;
        }
        public void LowLevelTransmit(byte[] data, Address address, string qt, uint cid, string? metadata)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException();
            }
            Packet packet = new();
            packet.PacketNo = 0;
            packet.MessageId = (ulong)Random.Shared.NextInt64();
            packet.Transmitter = LocalModemAddress;
            packet.QueryType = qt;
            packet.Receiver = address;
            packet.ConnectionID = cid;
            packet.Metadata = metadata;
            packet.DataBytes = data;
            packet.IsLastInSequence = true;
            Transmit(packet);
        }
        /// <summary>
        /// Inner-method for attaching generic packet events.
        /// </summary>
        /// <param name="onReceive">Delegate to be added</param>
        /// <exception cref="NullReferenceException">No socket io client</exception>
        /// <exception cref="ModemNotConnectedException">If modem is not connected to the tetronet</exception>
        /// <exception cref="NullAddressException">If there's no address in this modem</exception>
        /// <exception cref="NotImplementedException">idk how to implement that</exception>
        internal void InternalAttachReceiveEvent(Action<Packet, Action> onReceive)
        {
            InternalPacketEvents += onReceive;
        }
        /// <summary>
        /// Forces a Large Message to be uploaded to the tetronet.
        /// </summary>
        /// <param name="data">Large Message Data</param>
        /// <param name="receiver">Destination address (string)</param>
        /// <param name="connectid">Tetronet port</param>
        /// <param name="queryType">Query type</param>
        /// <param name="metadata">User Metadata for that Large Message</param>
        /// <returns>Nothing</returns>
        /// <exception cref="NullAddressException">Modem does not have an tetronet address</exception>
        /// <exception cref="HttpRequestException">Failed to upload the Large Message</exception>
        public async Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata = "")
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to push large messages, modem must have a local address");
            } 

            // ─────────────────────────────────────────────
            // 1. Prepare additional data
            // ─────────────────────────────────────────────
            string tail =
                "\n" + queryType +
                "\n" + LocalModemAddress.AddressValue +
                "\n" + metadata +
                "\n" + connectid;

            // ─────────────────────────────────────────────
            // 2. Convert data to base64
            // ─────────────────────────────────────────────
            
            string base64Data = Convert.ToBase64String(data);
            base64Data += tail;
            int transmittedBytesCount = base64Data.Length;

            // ─────────────────────────────────────────────
            // 3. Split into blocks 1048576 bytes each for uploading to the server
            // ─────────────────────────────────────────────
            List<string> blocks = SplitIntoChunks(base64Data, 1024 * 1024);

            using HttpClient client = new();
            client.Timeout = Timeout.InfiniteTimeSpan;

            // ─────────────────────────────────────────────
            // 4. Serial transmission only
            // ─────────────────────────────────────────────
            for (int i = 0; i < blocks.Count; i++)
            {
                var payload = new
                {
                    copybook_internet_modem_receiver = LocalModemAddress.AddressValue + "_" + receiver,
                    transferred_information = blocks[i]
                };

                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                HttpResponseMessage response = await client.PutAsync(UploadLargeMessagesTo, content);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Block {i + 1}/{blocks.Count} failed: {response.StatusCode}"
                    );
                }
            }

            return transmittedBytesCount;
        }
        /// <summary>
        /// Gets a Large Message from the tetronet, result will be in LastDownloadedLargeMessage
        /// </summary>
        /// <param name="transmitter">Who transmitted this Large Message to you?</param>
        /// <returns>Nothing</returns>
        /// <exception cref="NullAddressException"></exception>
        public async Task GetLargeMessage(Address transmitter)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("do obtain this large message, the modem must have a local tetronet address");
            }
            await GetLargeMessage(transmitter, LocalModemAddress);
        }
        /// <summary>
        /// Makes a Large Message Delete request to the tetronet.
        /// </summary>
        /// <param name="receiver">Who received this Large Message</param>
        /// <returns>Nothing</returns>
        /// <exception cref="NullAddressException">When there's no address for this modem</exception>
        public async Task DeleteLargeMessage(Address receiver)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("must have a local address to destroy large messages");
            }
            using HttpClient httpClient = new();
            await httpClient.GetAsync(DeleteLargeMessages + "?address_transmitter=" + LocalModemAddress.AddressValue + "_" + receiver.AddressValue);
        }
        /// <summary>
        /// Makes a Large Message Delete request to the tetronet.
        /// </summary>
        /// <param name="txrxpair">The transmitter and the receiver of this Large Message</param>
        /// <returns></returns>
        /// <exception cref="NullAddressException"></exception>
        public async Task DeleteLargeMessage(TransmitterReceiverPair txrxpair)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("must have a local address to destroy large messages");
            }
            using HttpClient httpClient = new();
            await httpClient.GetAsync(DeleteLargeMessages + "?address_transmitter=" + txrxpair.Transmitter.AddressValue + "_" + txrxpair.Receiver.AddressValue);
        }
        /// <summary>
        /// Makes a Large Message Delete request to the tetronet.
        /// </summary>
        /// <param name="tx">Transmitter</param>
        /// <param name="rx">Receiver</param>
        /// <returns></returns>
        public async Task DeleteLargeMessage(Address tx, Address rx)
        {
            await DeleteLargeMessage(new TransmitterReceiverPair(tx, rx));
        }
        private static List<string> SplitIntoChunks(string str, int chunkSize)
        {
            var chunks = new List<string>();
            int offset = 0;
            while (offset < str.Length)
            {
                int size = Math.Min(chunkSize, str.Length - offset);
                chunks.Add(str.Substring(offset, size));
                offset += size;
            }
            return chunks;
        }

        public async Task GetLargeMessage(Address tx, Address rx)
        {
            if (LocalModemAddress == null)
            {
                throw new NullAddressException("to download large message, modem must have a local address");
            }
            using HttpClient client = new();
            client.Timeout = Timeout.InfiniteTimeSpan;
            HttpResponseMessage response = await client.GetAsync(DownloadLargeMessagesFrom + tx.AddressValue + "_" + rx.AddressValue + ".largemessage");
            response.EnsureSuccessStatusCode();
            LargeMessage receivedData = new();
            string content = await response.Content.ReadAsStringAsync();
            string[] contentSplitted = content.Split('\n');
            receivedData.Data = [.. Convert.FromBase64String(contentSplitted[0])];
            /*try
            {
                receivedData.DataString = Encoding.UTF8.GetString([.. receivedData.DataBytes]);
            }
            catch
            {
                ModemAPIDebugger.OutputDebugMessage("Decode to UTF8 error");
                receivedData.DataString = "";
            }*/
            receivedData.QueryType = contentSplitted[1];
            receivedData.Transmitter = new(contentSplitted[2]);
            receivedData.Metadata = contentSplitted[3];
            receivedData.ConnectionID = uint.Parse(contentSplitted[4]);
            LastDownloadedLargeMessage = receivedData;
        }

        public async Task GetLargeMessage(TransmitterReceiverPair txrxpair)
        {
            await GetLargeMessage(txrxpair.Transmitter, txrxpair.Receiver);
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            InternalAttachReceiveEvent(delegate (Packet p, Action k)
            {
                if (LocalModemAddress == null)
                {
                    throw new NullAddressException("local modem address was null");
                }
                if (p.Receiver.AddressValue != LocalModemAddress.AddressValue)
                {
                    return;
                }
                onReceive(p, k);
            });
        }

        private Address SMLARequest(string modemName)
        {
            if (SMLALength < 1)
            {
                throw new InvalidDataException("smla length less than 1");
            }
            string number = "";
            int retries = 0;
            while (true)
            {
                if (retries > SMLAMaxRetries)
                {
                    throw new TimeoutException("smla process timed out");
                }
                for (int i = 0; i < SMLALength; i++)
                {
                    number += Random.Shared.Next(10).ToString();
                }
                if (!TakenNumbers.Contains(number))
                {
                    break;
                }
                retries++;
            }
            Address result = new($"{LocalModemAddress}-{number}");
            TakenNumbers.Add(number);
            AddressPortPairs.TryAdd(result.ToString(), modemName);
            return result;
        }
        private async Task ReconnectAsync()
        {
            InternalModemConnected = false;
            while (true)
            {
                try
                {
                    OnReconnectWebsocket();
                    RawTransmitterDevice = new ClientWebSocket();
                    await RawTransmitterDevice.ConnectAsync(new Uri(Cias), default);
                    InternalModemConnected = true;
                    OnSuccessReconnectWebsocket();
                    return;
                }
                catch
                {
                    await Task.Delay(2000);
                }
            }
        }

        public void AttachPacketRouter(IPacketRouter router)
        {
            InnerRouter = router;
        }
    }
}
