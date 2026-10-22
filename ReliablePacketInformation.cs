using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class ReliablePacketInformation
    {
        public long Id;
        /// <summary>
        /// <see cref="Stopwatch.GetTimestamp"/> of the last time the packet was sent. A monotonic clock: changes of the system
        /// time (daylight saving, clock corrections) don't make packets time out early or never.
        /// </summary>
        public long TimeStamp;
        public byte[]? Data;
        public int RetransmissionCounter = 0;
        /// <summary>
        /// Sequential number of the next packet at the moment of the last fast retransmission of this one, or -1 if there was none.
        /// </summary>
        public long FastRetransmittedBefore = -1;

        public ReliablePacketInformation()
        {

        }
        /// <param name="id">Sequential number</param>
        /// <param name="ts"><see cref="Stopwatch.GetTimestamp"/> of the moment the packet was sent</param>
        /// <param name="data">The packet</param>
        public ReliablePacketInformation(long id, long ts, byte[] data)
        {
            Id = id;
            TimeStamp = ts;
            Data = data;
        }
        /// <summary>
        /// Checks if the packet was sent more than timeout ago.
        /// </summary>
        /// <param name="timeout">Timeout in ticks (100 ns)</param>
        public bool CheckTimeout(int timeout)
        {
            return Stopwatch.GetElapsedTime(Volatile.Read(ref TimeStamp)).Ticks > timeout;
        }
        public void UpdateTimeStamp()
        {
            Volatile.Write(ref TimeStamp, Stopwatch.GetTimestamp());
        }
    }
}
