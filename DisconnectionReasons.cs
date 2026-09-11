namespace ModemAPI
{
    public enum DisconnectionReasons
    {
        ReasonUnspecified = 0,
        ReasonGracefulDisconnect = 1,
        ReasonReconnectAsAnAddressMachine = 2,
        ReasonLineOverload = 3,
        ReasonUnexpectedDisconnect = 4,
        ReasonBPTxOrBitrateUnacceptable = 5,
    }
}
