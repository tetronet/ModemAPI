namespace ModemAPI
{
    /// <summary>
    /// Represents a Body of the Low Latency Connection Request.
    /// </summary>
    public struct CIoCILLowLatShortConnectionRequestBody
    {
        /// <summary>
        /// true, if request body had one or more errors while reading, otherwise - false.
        /// </summary>
        public bool IsRequestDamaged;
        /// <summary>
        /// Current connecting client's Local Address.
        /// </summary>
        public Address CurrentClientAddress;
        /// <summary>
        /// Is Remote Client is connecting as an Address Machine.
        /// </summary>
        public bool IsAddressMachine;
    }
}
