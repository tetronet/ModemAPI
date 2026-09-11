namespace ModemAPI
{
    public class NullTransmitterException : Exception
    {
        public NullTransmitterException()
        {

        }
        public NullTransmitterException(string? message) : base(message)
        {

        }
        public NullTransmitterException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
