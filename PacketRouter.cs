using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class PacketRouter(Address localAddress) : IPacketRouter
    {
        public Address LocalAddress { get; } = localAddress;

        public PacketRouterDecidion Decide(TransmitterReceiverPair txrx, PacketTransmissionDirection dir)
        {
            if (!txrx.Transmitter.ToString().StartsWith(LocalAddress.ToString()) && dir == PacketTransmissionDirection.FromUpperToLower)
            {
                ModemAPIDebugger.OutputDebugMessage("address spoofing detected");
                return PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward;
            }
            if (txrx.Receiver.ToString().StartsWith(LocalAddress.ToString()))
            {
                ModemAPIDebugger.OutputDebugMessage("forwarding in the inner network");
                return PacketRouterDecidion.ForwardInInnerNetwork;
            }
            else
            {
                return PacketRouterDecidion.ForwardDown;
            }
        }

        public Packet? ModifyModifiablePacket(Packet packetWithModifyByRoutersFlag)
        {
            return null;
        }

        public void WorkOnReceiveByRouterPacket(Packet packetWithReceiveByRoutersFlag)
        {
            // nothing there
        }
    }
}
