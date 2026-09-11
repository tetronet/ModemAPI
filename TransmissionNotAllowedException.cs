namespace ModemAPI
{
    public class TransmissionNotAllowedException : Exception
    {
        public TransmissionNotAllowedException()
        {

        }
        public TransmissionNotAllowedException(string? message) : base(message)
        {

        }
        public TransmissionNotAllowedException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
