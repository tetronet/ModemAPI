namespace ModemAPI
{
    public class LocalAddressTooLongException : Exception
    {
        public LocalAddressTooLongException()
        {

        }
        public LocalAddressTooLongException(string? message) : base(message)
        {

        }
        public LocalAddressTooLongException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
