namespace ModemAPI
{
    public enum LMDTPResponseFlags : ushort
    {
        Success = 0x0000,
        LargeMessageNotFound = 0x0001,
        LargeMessageExceededMTU = 0x0002,
        ServerSideError = 0x0004,
        NotYourLargeMessage = 0x0008,
        ServerHardDriveFailed = 0x0010,
    }
}
