namespace ModemAPI
{
    public class LineNotPresentedException : Exception
    {
        public LineNotPresentedException()
        {

        }
        public LineNotPresentedException(string? message) : base(message)
        {
            
        }
        public LineNotPresentedException(string? message, Exception? innerException) : base(message, innerException)
        {
            
        }
    }
}
