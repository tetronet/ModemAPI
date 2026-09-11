namespace ModemAPI
{
    /// <summary>
    /// Represents a pair of 2 Tetronet Addresses for the Transmitter and the Receiver.
    /// </summary>
    public class TransmitterReceiverPair
    {
        /// <summary>
        /// Transmitter's Tetronet Address.
        /// </summary>
        public Address Transmitter = new();
        /// <summary>
        /// Receiver's Tetronet Address.
        /// </summary>
        public Address Receiver = new();
        /// <summary>
        /// Create a new instance of Transmitter Receiver Pair with default values.
        /// </summary>
        public TransmitterReceiverPair()
        {
            
        }
        /// <summary>
        /// Create a new instance of Transmitter Receiver Pair.
        /// </summary>
        /// <param name="tx">Transmitter's Tetronet Address</param>
        /// <param name="rx">Receiver's Tetronet Address</param>
        public TransmitterReceiverPair(Address tx, Address rx)
        {
            Transmitter = tx;
            Receiver = rx;
        }
    }
}
