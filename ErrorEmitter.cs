using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public enum ErrorEmitter
    {
        LowLatencyPhysicalModem = 0,
        MoreStableSerialPacket = 1,
        HardwareSerialPort = 2,
        Base64Converter = 3,
    }
}
