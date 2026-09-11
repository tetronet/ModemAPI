namespace ModemAPI
{
    public enum TransmitState
    {
        WaitingForImmediateAnswer,
        WaitingForNonLocalReady,
        WaitingForPacketTransmission,
        WaitingForTransmitComplete,
        Done,
        Error
    }
}
