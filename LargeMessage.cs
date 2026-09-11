using System.Text;

namespace ModemAPI
{
    /// <summary>
    /// Represents a Large Message in the Tetronet.
    /// </summary>
    public class LargeMessage
    {
        /// <summary>
        /// Data (payload) of this Large Message.
        /// </summary>
        public byte[] Data = [];
        /// <summary>
        /// Transmitter's Tetronet Address of this Large Message.
        /// </summary>
        public Address Transmitter = new();
        /// <summary>
        /// Receiver's Tetronet Address of this Large Message.
        /// </summary>
        public Address Receiver = new();
        /// <summary>
        /// Query Type (Package Type) of this Large Message.
        /// </summary>
        public string QueryType = "";
        /// <summary>
        /// Connection ID (Port) of this Large Message.
        /// </summary>
        public uint ConnectionID = 0;
        /// <summary>
        /// Metadata of this Large Message.
        /// </summary>
        public string? Metadata = null;
        /// <summary>
        /// Create new Large Message with default values.
        /// </summary>
        public LargeMessage()
        {
            
        }
        /// <summary>
        /// Create new Large Message.
        /// </summary>
        /// <param name="data">Data (payload) of this Large Message</param>
        /// <param name="tx">Transmitter's Tetronet Address of this Large Message</param>
        /// <param name="rx">Receiver's Tetronet Address of this Large Message</param>
        /// <param name="qt">Query Type (Package Type) of this Large Message</param>
        /// <param name="connectid">Connection ID (Port) of this Large Message</param>
        /// <param name="metadata">Metadata of this Large Message</param>
        public LargeMessage(byte[] data, Address tx, Address rx, string qt, uint connectid, string? metadata = "")
        {
            Data = data;
            Transmitter = tx;
            Receiver = rx;
            QueryType = qt;
            ConnectionID = connectid;
            Metadata = metadata;
        }
        /// <summary>
        /// Get a Data Block with the same values as presented in this instance of Large Message.
        /// </summary>
        /// <returns>Created Data Block instance</returns>
        public DataBlock ToDataBlock()
        {
            return new DataBlock(Transmitter, Receiver, Data.ToList(), Encoding.UTF8.GetString(Data), Metadata, QueryType, ConnectionID);
        }
    }
}
