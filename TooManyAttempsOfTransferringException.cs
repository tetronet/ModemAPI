namespace ModemAPI
{
    internal class TooManyAttempsOfTransferringException : Exception
    {
        public TooManyAttempsOfTransferringException()
        {

        }
        public TooManyAttempsOfTransferringException(string? message) : base(message)
        {

        }
        public TooManyAttempsOfTransferringException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
