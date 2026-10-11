using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Represents types of the requests while using LMDTP Protocol.
    /// </summary>
    public enum LMDTPRequestType : byte
    {
        /// <summary>
        /// Represents a value for the OBTAIN Request Type of the LMDTP.
        /// </summary>
        Obtain = 0x15,
        /// <summary>
        /// Represents a value for the HASH Request Type of the LMDTP.
        /// </summary>
        Hash = 0x2F,
    }
}
