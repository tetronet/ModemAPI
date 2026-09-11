namespace ModemAPI
{
    public class WrongCharacherException : Exception
    {
        public WrongCharacherException()
        {

        }
        public WrongCharacherException(string? message) : base(message)
        {

        }
        public WrongCharacherException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
