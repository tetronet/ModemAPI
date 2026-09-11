namespace ModemAPI
{
    public interface IAnswerHost
    {
        void CreateSlave(string serialPortNumber, int portSpeed, L1Types wireType, bool useMoreStableSerialPacket);
        Dictionary<string, LowLatencyAnsweringPhysicalModem> GetAnsweringAllModems();
        LowLatencyAnsweringPhysicalModem GetAnsweringModem(string serialPortNumber);
        void AttachPacketRouter(IPacketRouter router);
    }
}
