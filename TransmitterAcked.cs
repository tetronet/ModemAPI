using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;

namespace ModemAPI
{
    public class TransmitterAcked : ITransmitter
    {
        // Используем потокобезопасные коллекции
        private readonly ConcurrentDictionary<string, AcknowledgementState> _receiverStates = new();
        private readonly ConcurrentDictionary<string, AcknowledgementState> _transmitterStates = new();

        // Для управления таймаутами
        private readonly CancellationTokenSource _cts = new();

        public IModem Modem { get; }
        public long TransmitterTimeout { get; set; } = 1000; // Значение по умолчанию

        public TransmitterAcked(IModem modem)
        {
            Modem = modem;
        }

        public void AttachReceiveEvent(Action<DataBlock, Action> onDataReceived)
        {
            Modem.AttachReceiveEvent((r, k) =>
            {
                try
                {
                    ModemAPIDebugger.OutputDebugMessage("Received data is:");
                    ModemAPIDebugger.OutputDebugMessage(r.DataString);
                    ModemAPIDebugger.OutputDebugMessage(r.QueryType);
                    ModemAPIDebugger.OutputDebugMessage(r.ConnectionID.ToString());
                    // Обработка alive-запроса
                    if (r.ConnectionID == ConnectionIDDefaults.TNET_ACK &&
                        r.QueryType == "page" &&
                        r.DataBytes.SequenceEqual("r"u8.ToArray()))
                    {
                        if (_receiverStates.TryGetValue(r.Transmitter.ToString(), out var state) &&
                            state != AcknowledgementState.None)
                            goto end;

                        ModemAPIDebugger.OutputDebugMessage("TransmitterAcked.cs: received alive?");
                        if (!_receiverStates.TryAdd(r.Transmitter.ToString(), AcknowledgementState.WaitingForData))
                        {
                            _receiverStates[r.Transmitter.ToString()] = AcknowledgementState.WaitingForData;
                        }
                        Modem.Transmit("3"u8.ToArray(), r.Transmitter, "page", ConnectionIDDefaults.TNET_ACK);
                        ModemAPIDebugger.OutputDebugMessage("TransmitterAcked.cs: sent answer yes");
                        return;
                    }
                end:
                    // Обработка данных
                    ModemAPIDebugger.OutputDebugMessage($"State for {r.Transmitter} is {_receiverStates[r.Transmitter.ToString()]}");
                    if (_receiverStates.TryGetValue(r.Transmitter.ToString(), out var dataState) &&
                        dataState == AcknowledgementState.WaitingForData)
                    {
                        Modem.Transmit("4"u8.ToArray(), r.Transmitter, "page", ConnectionIDDefaults.TNET_ACK);
                        onDataReceived(r, k);
                        _receiverStates[r.Transmitter.ToString()] = AcknowledgementState.None;
                    }
                }
                catch (Exception ex)
                {
                    ModemAPIDebugger.OutputDebugMessage($"Error in receive event: {ex.Message}");
                }
            });
        }

        public void Transmit(string data, Address receiver, string queryType,
            uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TransmitterTimeout));
            var tcs = new TaskCompletionSource<bool>();

            // Временный обработчик для этой транзакции
            Action<DataBlock, Action>? handler = null;
            handler = (r, k) =>
            {
                try
                {
                    if (r.Transmitter.Equals(receiver) && r.ConnectionID == ConnectionIDDefaults.TNET_ACK)
                    {
                        if (r.DataBytes.SequenceEqual("3"u8.ToArray()))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Stage 1 (receiver IS alive)");
                            Modem.Transmit(data, receiver, queryType, connectid, metadata, 1024, transmissionDelay);
                        }
                        else if (r.DataBytes.SequenceEqual("4"u8.ToArray()))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Stage 2 (Acknowledgement)");
                            tcs.TrySetResult(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            Modem.AttachReceiveEvent(handler);

            try
            {
                // Отправляем alive-запрос
                Modem.Transmit([0x72], receiver, "page", ConnectionIDDefaults.TNET_ACK, null, 1024, transmissionDelay);

                // Ждём подтверждение или таймаут
                tcs.Task.Wait(cts.Token);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"acked transmission exceeded limit of {TransmitterTimeout}ms");
            }
        }

        public void Transmit(byte[] data, Address receiver, string queryType,
            uint connectid, string? metadata = null, int transmissionDelay = 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TransmitterTimeout));
            var tcs = new TaskCompletionSource<bool>();

            // Временный обработчик для этой транзакции
            Action<DataBlock, Action>? handler = null;
            handler = (r, k) =>
            {
                try
                {
                    if (r.Transmitter.Equals(receiver))
                    {
                        if (r.DataBytes.SequenceEqual("3"u8.ToArray()))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Stage 1 (receiver IS alive)");
                            Modem.Transmit(data, receiver, queryType, connectid, metadata, 1024, transmissionDelay);
                        }
                        else if (r.DataBytes.SequenceEqual("4"u8.ToArray()))
                        {
                            ModemAPIDebugger.OutputDebugMessage("Stage 2 (Acknowledgement)");
                            tcs.TrySetResult(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            Modem.AttachReceiveEvent(handler);

            try
            {
                // Отправляем alive-запрос
                ModemAPIDebugger.OutputDebugMessage("Transmitting message: transmitting message");
                ModemAPIDebugger.OutputDebugMessage("destination: " + receiver);
                Modem.Transmit([0x72], receiver, "page", ConnectionIDDefaults.TNET_ACK, null, 1024, transmissionDelay);
                ModemAPIDebugger.OutputDebugMessage("Success");

                // Ждём подтверждение или таймаут
                tcs.Task.Wait(cts.Token);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"acked transmission exceeded limit of {TransmitterTimeout}ms");
            }

        }
    }
}
