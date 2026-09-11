namespace ModemAPI
{
    public interface IAnsweringModem
    {
        void Answer();
        void Drop();
        void Intercept(Action<DataBlock, Action, PacketTransmissionDirection> onPacketIntercepted);
        void TransmitToSubscriber(byte[] data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100);
        void TransmitToSubscriber(string data, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100);
        void LockDataTransfer();
        void UnlockDataTransfer();
        Address GetSubscriberAddress();
        bool SetSubscriberAddress(Address subscriberAddress);
        void AttachIncomingEvent(Action onIncoming);
        void AttachConnectEvent(Action onConnection);
    }
}
