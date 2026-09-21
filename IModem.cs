namespace ModemAPI
{
    /// <summary>
    /// Base interface for implementing to make class a modem.
    /// </summary>
    public interface IModem
    {
        int MaxReinitializeAttempts { get; set; }
        Address? LocalModemAddress { get; set; }
        bool IsModemConnected { get; }
        LargeMessage? LastDownloadedLargeMessage { get; set; }
        void Dial();
        void Drop(bool carefulMode);
        void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0);
        void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 0);
        void LowLevelTransmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null);
        void AttachReceiveEvent(Action<DataBlock, Action> onReceive);
        void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive);
        bool IsAddressSMLA();
        Task GetLargeMessage(Address transmitter);
        Task GetLargeMessage(Address tx, Address rx);
        Task GetLargeMessage(TransmitterReceiverPair txrxpair);
        Task DeleteLargeMessage(Address transmitter);
        Task<int> PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata = null);
        public Task DeleteLargeMessage(Address tx, Address rx);
        public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair);
    }
}
