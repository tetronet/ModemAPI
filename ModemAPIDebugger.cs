namespace ModemAPI
{
    public class ModemAPIDebugger
    {
        public const bool ALLOW_DEBUG_OUTPUTS = false;
        public static void PrintOutPacket(Packet packet)
        {
            if (ALLOW_DEBUG_OUTPUTS)
            {
                Console.WriteLine("*** Debugging information about packet ***");
                Console.Write("packet.IsLastInSequence: "); Console.WriteLine(packet.IsLastInSequence);
                Console.Write("packet.ConnectionID: "); Console.WriteLine(packet.ConnectionID);
                Console.Write("packet.DataBytes: "); Console.WriteLine(packet.DataBytes);
                Console.Write("packet.IsErrorWhileReading: "); Console.WriteLine(packet.IsErrorWhileReading);
                Console.Write("packet.QueryType: "); Console.WriteLine(packet.QueryType);
                Console.Write("packet.PacketNo: "); Console.WriteLine(packet.PacketNo);
                Console.Write("packet.Transmitter: "); Console.WriteLine(packet.Transmitter);
                Console.Write("packet.Receiver: "); Console.WriteLine(packet.Receiver);
                Console.Write("packet.Transmitter.AddressValue: "); Console.WriteLine(packet.Transmitter.AddressValue);
                Console.Write("packet.Receiver.AddressValue: "); Console.WriteLine(packet.Receiver.AddressValue);
                Console.WriteLine("*** Packet was successfully printed out! Now you can use it to see what's going in wrong way! ***");
            }
            
        }

        public static void PrintByteArray(byte[] array)
        {
            if (ALLOW_DEBUG_OUTPUTS)
            {
                Console.WriteLine("Array sequence value is: " + string.Join(", ", array));
            }
        }

        public static void PrintAddressPortPairDict(Dictionary<Address, string> keyValuePairs)
        {
            if (ALLOW_DEBUG_OUTPUTS)
            {
                for (int i = 0; i > keyValuePairs.Count; i++)
                {
                    Console.Write("Address \\ Port: ");
                    Console.Write(keyValuePairs.Keys.ToArray()[i].AddressValue);
                    Console.Write(" \\ ");
                    Console.WriteLine(keyValuePairs.GetValueOrDefault(keyValuePairs.Keys.ToArray()[i]));
                }
            }
            
        }

        public static void OutputDebugMessage(string message)
        {
            if (ALLOW_DEBUG_OUTPUTS)
            {
                Console.WriteLine(message);
            }
        }
    }
}
