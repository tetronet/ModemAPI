namespace ModemAPI
{
    /// <summary>
    /// A record of a UNNRP (User Name Network Resolve Protocol) server, see <see cref="UNNRPServer.AddRecord"/>.
    /// Only the record classes of this library can be added to a server: <see cref="RecordUnameToAfTnet"/>, <see cref="RecordAfTnetToUname"/>,
    /// <see cref="RecordUnameToArrayTnet"/>, <see cref="RecordRegister"/>, <see cref="RecordVideoNetwork"/> and <see cref="RecordNameSpaceServer"/>.
    /// </summary>
    public interface IResolveRecord
    {
        /// <summary>
        /// The type of the record: the <c>RecordType</c> constant of its class, the server answers it to the requests of the record type.
        /// </summary>
        byte Type { get; }
        /// <summary>
        /// What the record is looked up by: a UNAME, a wildcard (<c>*.retalog.com</c>), a tetronet address or a videonet address (<c>vnet8596226</c>).
        /// Two records with the same key can't be on one server.
        /// </summary>
        string Key { get; }
    }
}
