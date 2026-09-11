namespace ModemAPI
{
    public class NetworkInterfaceLibrary
    {
        /// <summary>
        /// Stores every created network interface as a pair of Network interface name: Interface interaction object
        /// </summary>
        public static Dictionary<string, SerialNetworkInterface> NetworkInterfaces = [];
    }
}
