namespace ModemAPI
{
    public class NotACopybookModemException : Exception
    {
        public NotACopybookModemException()
        {

        }
        public NotACopybookModemException(string? message) : base(message)
        {

        }
        public NotACopybookModemException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
