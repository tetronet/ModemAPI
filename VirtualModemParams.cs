namespace ModemAPI
{
    public class VirtualModemParams : IModemParams
    {
        private string CIASInternetAddress = "";
        private ushort CIASInternetPort = 0;
        private bool CIASUsesEncryptedSocketIO = false;
        private string LargeMessageGetURL = "";
        private string LargeMessageAddURL = "";
        private LineParams InnerLineParams = new();

        /// <summary>
        /// Changes the saved settings.
        /// </summary>
        /// <param name="settings">Multi-param, that must have this sequence: CIAS URL/IP - string, Large message download dir URL - string, Large message upload URL - string</param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="NullReferenceException"></exception>
        public void SetModemParams(params object[] settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            CIASInternetAddress = (string)settings[0];
            CIASInternetPort = (ushort)settings[1];
            CIASUsesEncryptedSocketIO = (bool)settings[2];
            if (InnerLineParams == null)
            {
                throw new NullReferenceException("check InnerLineParams, null is ambigous");
            }
        }
        /// <summary>
        /// Return sequence: (CIAS Name), (CIAS Port), (Encrypted Channel), (Params common for physical and virtual lines)
        /// </summary>
        public object[] GetModemParams()
        {
            return new object[] { CIASInternetAddress, CIASInternetPort, CIASUsesEncryptedSocketIO, InnerLineParams };
        }
    }
}
