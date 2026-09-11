namespace ModemAPI
{
    /// <summary>
    /// Represents Acked Transmitter states
    /// </summary>
    public enum AcknowledgementState
    {
        /// <summary>
        /// Default state
        /// </summary>
        None = 0,
        /// <summary>
        /// Transmitter is waiting for other side to answer for the "alive?" packet
        /// </summary>
        WaitingForAlivement = 1,
        /// <summary>
        /// Transmitter is waiting for other side to accept data
        /// </summary>
        WaitingForAcknowledgement = 2,
        /// <summary>
        /// Receiver answered to the "alive?" packet and is waiting for the transmitter to transmit the data
        /// </summary>
        WaitingForData = 3
    }
}
