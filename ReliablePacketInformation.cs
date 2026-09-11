using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class ReliablePacketInformation
    {
        public long Id;
        public long TimeStamp;
        public byte[]? Data;

        public ReliablePacketInformation()
        {

        }
        public ReliablePacketInformation(long id, long ts, byte[] data)
        {
            Id = id;
            TimeStamp = ts;
            Data = data;
        }
        public bool CheckTimeout(int timeout)
        {
            return DateTime.Now.Ticks - timeout > TimeStamp;
        }
        public void UpdateTimeStamp()
        {
            TimeStamp = DateTime.Now.Ticks;
        }
    }
}
