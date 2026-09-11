using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public enum PacketRouterDecidion
    {
        None = 0,
        AddressSpoofingDetectedRefusedToForward = 1,
        ForwardInInnerNetwork = 2,
        ForwardDown = 3,
    }
}
