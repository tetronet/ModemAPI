using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class SRTPPacketData
    {
        internal long id;
        internal byte[] data;
        internal SRTPPacketData(long a, byte[] b)
        {
            id = a;
            data = b;
        }
    }
}
