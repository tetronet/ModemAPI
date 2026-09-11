namespace ModemAPI
{
    public class UnableToConnectToTheTetronetException : Exception
    {
        public UnableToConnectToTheTetronetException()
        {

        }
        public UnableToConnectToTheTetronetException(string? message) : base(message)
        {

        }
        public UnableToConnectToTheTetronetException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
