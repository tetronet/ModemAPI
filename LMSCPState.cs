namespace ModemAPI
{
    public enum LMSCPState
    {
        None = 0,
        UploadingLM = 1,
        DownloadingLM = 2,
        Initializing = 3,
        LMSCPTerminalReady = 4,
        LMSCPAccepted = 5,
        LMSCPTerminate = 6,
        LMSCPConnectOk = 7,
        LMSCPLargeMessagesTransmitted = 8,
        LMSCPTransmissionCompleted = 9,
    }
}
