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
            string local = LocalAddress.ToString();
            if (!IsWithin(txrx.Transmitter.ToString(), local) && dir == PacketTransmissionDirection.FromUpperToLower)
            {
                ModemAPIDebugger.OutputDebugMessage("address spoofing detected");
                return PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward;
            }
            if (IsWithin(txrx.Receiver.ToString(), local))
            {
                ModemAPIDebugger.OutputDebugMessage("forwarding in the inner network");
                return PacketRouterDecidion.ForwardInInnerNetwork;
            }
            else
            {
                return PacketRouterDecidion.ForwardDown;
            }
        }

        /// <summary>
        /// Whether the address is the local one or one under it: 7-1 and 7-1-5 are within 7-1, but 7-10-5 isn't. Compared
        /// ordinally: addresses are digits and dashes, and the rules of the current culture are ~150 times slower for nothing.
        /// An empty local address has everything within it.
        /// </summary>
        internal static bool IsWithin(string address, string local)
        {
            return local.Length == 0 ||
                (address.StartsWith(local, StringComparison.Ordinal) && (address.Length == local.Length || address[local.Length] == '-'));
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
