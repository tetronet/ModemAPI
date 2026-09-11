namespace ModemAPI
{
    public class ModemIsNotInitedException : Exception
    {
        public ModemIsNotInitedException()
        {

        }
        public ModemIsNotInitedException(string? message) : base(message)
        {

        }
        public ModemIsNotInitedException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
