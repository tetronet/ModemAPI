namespace ModemAPI
{
    /// <summary>
    /// Messages for CIoCIL (soon becoming deprecated).
    /// </summary>
    public class CIoCILDefaultPhrases
    {
        // Connection phrases
        public static readonly byte[] RequestConnection = [0x20, 0x00, 0x00, 0x00, 0xff];
        public static readonly byte[] LocalAddressRequest = [0xff, 0x22, 0x96, 0x00, 0x00];
        public static readonly byte[] LocalAddressTransferToNonLocal = [0x55, 0x22,];
        public static readonly byte[] LocalDeviceTypeRequest = [0xff, 0x96, 0x96, 0x00, 0x20];
        public static readonly byte[] SetModemLocalAddress = [0x73, 0x6d, 0x6c, 0x61];
        public static readonly byte[] DeviceTypeAddressMachine = [0x00];
        public static readonly byte[] DeviceTypeModem = [0xff];
        public static readonly byte[] NonLocalFinishInitSuccess = [0x00, 0x00, 0xff, 0xff];
        public static readonly byte[] LocalFinishInitFailure = [0x00, 0x00, 0xff, 0xff];
        public static readonly byte[] LocalFinishInitSuccess = [0xff, 0xff, 0x00, 0x00];
        public static readonly byte[] NonLocalFinishInitFailure = [0xff, 0xff, 0x00, 0x00];
        public static readonly byte[] DeviceTypeTransferToNonLocal = [0x97];
        // Disconnection phrases
        public static readonly byte[] RequestDisconnection = [0xff, 0xff, 0x20, 0xff, 0xff];
        public static readonly byte[] NonLocalModemRequestsDisconnectionReason = [0x26, 0x32, 0xfd, 0xff, 0xff, 0xff];
        public static readonly byte[] ReasonUnspecified = [0x00];
        public static readonly byte[] ReasonGracefulDisconnect = [0x14];
        public static readonly byte[] ReasonReconnectAsAnAddressMachine = [0x2d];
        public static readonly byte[] ReasonLineOverload = [0x6c];
        public static readonly byte[] ReasonUnexpectedDisconnect = [0x99];
        public static readonly byte[] ReasonBPTxOrBitrateUnacceptable = [0xff];
        public static readonly byte[] NonLocalSideReasonAccept = [0x54, 0x54, 0x54, 0x23];
        public static readonly byte[] LocalSideAcceptDisconnection = [0x22, 0x54, 0x43, 0xff];
        // Packet transfer phrases
        public static readonly byte[] RequestForTransmittingPacket = [0xf3, 0xf2, 0xff, 0x00];
        public static readonly byte[] ImmediateAnswerFromNonLocal = [0x20, 0x00, 0x35, 0x66, 0x00, 0x00, 0x25, 0x07];
        public static readonly byte[] LocalIsReadyForTransmit = [0xff, 0xff, 0x00, 0x92];
        public static readonly byte[] NonLocalIsAcceptingReadinness = [0x29, 0x00, 0xff, 0xff];
        public static readonly byte[] PacketTransferHeader = [0x56, 0x92, 0x3f, 0xfd];
        public static readonly byte[] PacketTransmittionSuccess = [0x42, 0x15, 0xc2];
        public static readonly byte[] TransmittionCompleteRequest = [0x81, 0xcc, 0x4c];
        public static readonly byte[] NonLocalAcceptsPacketSuccessTransmittion = [0x22, 0xc4, 0x40, 0x04];
        public static readonly byte[] PacketRetransmittionRequired = [0x89, 0x92, 0x43];
        // Common phrases
        public static readonly byte[] CIoCILTrue = [0xfa];
        public static readonly byte[] CIoCILFalse = [0x27];
    }
}
