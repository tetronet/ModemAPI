namespace ModemAPI
{
    public class DataTransmissionLockedException : Exception
    {
        public DataTransmissionLockedException()
        {

        }
        public DataTransmissionLockedException(string? message) : base(message)
        {

        }
        public DataTransmissionLockedException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
