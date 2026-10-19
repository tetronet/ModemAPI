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
        /// <summary>
        /// The hash field contains a 64-bit XxHash3 instead of SHA-512. Only set when the client listed <see cref="LMDTPHashAlgorithms.XxHash3"/> in its request.
        /// </summary>
        HashIsXxHash3 = 0x0100,
    }
}
