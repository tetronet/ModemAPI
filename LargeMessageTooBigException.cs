using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class LargeMessageTooBigException : Exception
    {
        public LargeMessageTooBigException()
        {

        }
        public LargeMessageTooBigException(string? message) : base(message)
        {

        }
        public LargeMessageTooBigException(string? message, Exception? innerException) : base(message, innerException)
        {

        }
    }
}
