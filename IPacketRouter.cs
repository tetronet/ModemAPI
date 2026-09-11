using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public interface IPacketRouter
    {
        Address LocalAddress { get; }
        PacketRouterDecidion Decide(TransmitterReceiverPair txrx, PacketTransmissionDirection dir);
        void WorkOnReceiveByRouterPacket(Packet packetWithReceiveByRoutersFlag);
        Packet? ModifyModifiablePacket(Packet packetWithModifyByRoutersFlag);
    }
}
