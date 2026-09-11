namespace ModemAPI
{
    public class IncorrectModemException : Exception
    {
        public IncorrectModemException()
        {

        }
        public IncorrectModemException(string? message) : base(message)
        {

        }
        public IncorrectModemException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
