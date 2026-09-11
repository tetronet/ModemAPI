using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public enum L1DropBytesReasons
    {
        None = 0,
        DataTimedOut = 1,
        FindingHeaders = 2,
        NoHeader = 3,
    }
}
