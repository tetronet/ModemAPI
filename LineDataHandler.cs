namespace ModemAPI
{
    /// <summary>
    /// Gets the data of one received buffer (a packet or a line message). The data is valid only during the call: the buffer
    /// behind it is reused, so a handler, that needs the data later, copies it.
    /// </summary>
    internal delegate void LineDataHandler(ReadOnlySpan<byte> data);
}
