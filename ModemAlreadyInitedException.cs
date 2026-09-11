namespace ModemAPI
{
    public class ModemAlreadyInitedException : Exception
    {
        public ModemAlreadyInitedException()
        {

        }
        public ModemAlreadyInitedException(string? message) : base(message)
        {

        }
        public ModemAlreadyInitedException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
