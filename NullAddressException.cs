namespace ModemAPI
{
    public class NullAddressException : Exception
    {
        public NullAddressException()
        {

        }
        public NullAddressException(string? message) : base(message)
        {

        }
        public NullAddressException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
