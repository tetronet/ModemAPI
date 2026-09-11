namespace ModemAPI
{
    public class LineParams
    {
        public Address ModemLocalAddress { get; set; }
        public string? ModemLocalUsername { get; set; }
        public bool UseLargeMessages { get; set; }

        public LineParams(Address address, bool useLargeMessages, string? username)
        {
            ModemLocalAddress = address;
            UseLargeMessages = useLargeMessages;
            ModemLocalUsername = username;
        }

        public LineParams()
        {
            ModemLocalAddress = new();
            UseLargeMessages = true;
            ModemLocalUsername = null;
        }
    }
}
