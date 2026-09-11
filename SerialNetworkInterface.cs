using System.IO.Ports;

namespace ModemAPI
{
    public class SerialNetworkInterface
    {
        private bool IsSubscribedForIncomingData = false;
        private List<Action<byte[]>> Handlers = [];
        public SerialPort Port;
        public SerialNetworkInterface(SerialPort port)
        {
            Port = port;
            if (NetworkInterfaceLibrary.NetworkInterfaces.ContainsKey(port.PortName))
            {
                throw new InvalidOperationException("cannot add a new network interface because it is conflicting");
            }
            NetworkInterfaceLibrary.NetworkInterfaces.Add(port.PortName, this);
            Port.DiscardInBuffer();
            Port.DiscardOutBuffer();
        }

        public void OnDataReceived(Action<byte[]> eventBody)
        {
            Handlers.Add(eventBody);
            if (!IsSubscribedForIncomingData)
            {
                Port.DataReceived += delegate (object handler, SerialDataReceivedEventArgs e)
                {
                    byte[] buffer = new byte[Port.BytesToRead];
                    Port.Read(buffer, 0, buffer.Length);
                    List<Action<byte[]>> snapshot = [.. Handlers];
                    foreach (Action<byte[]> eventHandler in snapshot)
                    {
                        eventHandler(buffer);
                    }
                    
                };
                IsSubscribedForIncomingData = true;
            }
        }
        public void Write(byte[] data)
        {
            Port.Write(data, 0, data.Length);
        }
    }
}
