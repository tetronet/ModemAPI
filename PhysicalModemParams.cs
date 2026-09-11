namespace ModemAPI
{
    public class PhysicalModemParams : IModemParams
    {
        private int PortSpeed = 0;
        private int LineSpeed = 0;
        private BPTx BitsPerTick = BPTx.BPTx1;
        private string CommunicationalPort = "COM1";
        private PhysicalModemCommandList CommandList = new();
        private PhysicalModemEventList EventList = new();
        public LineParams InnerLineParams = new(new Address(), true, null);
        /// <summary>
        /// Sets the sequence of data as an object[] like this:
        ///     1) Port speed - int.
        ///     2) Line speed - int.
        ///     3) Bits per tick (BPTx) - BPTx.
        ///     4) Communicational port - SerialPort.
        ///     5) Command list - PhysicalModemCommandList.
        ///     6) Event list - PhysicalModemEventList.
        ///     7) Settings, that are the same for Physical and Virtual modems - LineParams.
        /// If some params were inserted wrong, this cause the following exceptions:
        /// </summary>
        /// <param name="settings"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="NullReferenceException"></exception>
        public void SetModemParams(params object[] settings)
        {
            try
            {
                if (settings == null)
                {
                    throw new ArgumentNullException(nameof(settings));
                }
                PortSpeed = (int)settings[0];
                LineSpeed = (int)settings[1];
                BitsPerTick = (BPTx)settings[2];
                CommunicationalPort = (string)settings[3];
                CommandList = (PhysicalModemCommandList)settings[4];
                EventList = (PhysicalModemEventList)settings[5];
                InnerLineParams = (LineParams)settings[6];
                if (InnerLineParams == null)
                {
                    throw new NullReferenceException("check InnerLineParams, null is ambigous");
                }
                if (CommandList == null)
                {
                    throw new NullReferenceException("check CommandList, null is ambigous");
                }
                if (EventList == null)
                {
                    throw new NullReferenceException("check EventList, null is ambigous");
                }
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// Fetch all of the data, that was saved in the PhysicalModemParams object.
        /// </summary>
        /// <returns>
        /// Sequence of data as an object[] like this:
        ///     1) Port speed - int.
        ///     2) Line speed - int.
        ///     3) Bits per tick (BPTx) - BPTx.
        ///     4) Communicational port - SerialPort.
        ///     5) Command list - PhysicalModemCommandList.
        ///     6) Event list - PhysicalModemEventList.
        ///     7) Settings, that are the same for Physical and Virtual modems - LineParams.
        /// </returns>
        public object[] GetModemParams()
        {
            return new object[] { PortSpeed, LineSpeed, BitsPerTick, CommunicationalPort, CommandList, EventList, InnerLineParams };
        }
    }
}
