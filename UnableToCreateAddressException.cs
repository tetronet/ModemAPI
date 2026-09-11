namespace ModemAPI
{
    public class UnableToCreateAddressException : Exception
    {
        public UnableToCreateAddressException()
        {

        }
        public UnableToCreateAddressException(string? message) : base(message)
        {

        }
        public UnableToCreateAddressException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
