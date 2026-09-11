namespace ModemAPI
{
    public class PhysicalModemCommandList
    {
        public string Dial = "ATD";
        public string ChangeBitrate = "ATB %{arg1}";
        public string SetBPTx1 = "ATBPT0";
        public string SetBPTx2 = "ATBPT1";
        public string SetBPTx4 = "ATBPT2";
        public string SetBPTx8 = "ATBPT3";
        public string ExitDataMode = "+++";
        public string EnterDataMode = "ATT";
        public string AboutModemInfo = "ATI";
        public string MaxBPT = "ATI0";
        public string MaxBitrate = "ATI1";
        public string ModemTest = "ATI2";
        public string GetModemVendor = "ATI3";
        public string CheckIfConnected = "ATI4";
        public string MaxLargeMessageBitrate = "ATI5";
        public string BitrateCurrent = "ATI6";
        public string BitrateCurrentLargeMessage = "ATI7";
        public string AnswerToIncoming = "ATA";
        public string SmallMessages = "ATSM";
        public string LargeMessages = "ATLM";
        public string ConnectionAbort = "ATHU";
        public string ChangeLargeMessageBitrate = "ATBLM %{arg1}";
    }
}
