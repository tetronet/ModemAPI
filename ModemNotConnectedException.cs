namespace ModemAPI
{
    public class ModemNotConnectedException : Exception
    {
        public ModemNotConnectedException()
        {

        }
        public ModemNotConnectedException(string? message) : base(message)
        {

        }
        public ModemNotConnectedException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
