namespace ModemAPI
{
    /// <summary>
    /// Represents a data block (a packet sequence), that is transferred by the TetroNet.
    /// </summary>
    public class DataBlock
    {
        /// <summary>
        /// TetroNet subscriber address, that transmitted this data block.
        /// </summary>
        public Address Transmitter = new Address();
        /// <summary>
        /// TetroNet subscriber address, that is going to receive this data block.
        /// </summary>
        public Address Receiver = new Address();
        /// <summary>
        /// Data of the block, that was transferred in it.
        /// </summary>
        public List<byte> DataBytes = new List<byte>();
        /// <summary>
        /// String representation of the data of the block, that was transferred in it.
        /// </summary>
        public string DataString = "";
        /// <summary>
        /// Metadata string that was received, metadata may not exist, then value will be set to null.
        /// </summary>
        public string? Metadata = null;
        /// <summary>
        /// Query type of that data block.
        /// </summary>
        public string QueryType = "";
        /// <summary>
        /// ConnectionID for that data block.
        /// </summary>
        public uint ConnectionID = 0;
        /// <summary>
        /// Initializes a new instance of DataBlock with default values
        /// </summary>
        public DataBlock()
        {
        
        }
        /// <summary>
        /// Initializes a new instance of DataBlock with values, that will be taken from arguments
        /// </summary>
        /// <param name="transmitter">Address for transmitter</param>
        /// <param name="receiver">Address for receiver</param>
        /// <param name="data">Data for this data block</param>
        /// <param name="data_">String representation of data for this data block</param>
        /// <param name="metadata">String, that contains metadata for this data block</param>
        /// <param name="qtype">Query type for this data block</param>
        /// <param name="connectid">Connection ID for this data block</param>
        public DataBlock(Address transmitter, Address receiver, List<byte> data, string data_, string? metadata, string qtype, uint connectid)
        {
            Transmitter = transmitter;
            Receiver = receiver;
            DataBytes = data;
            DataString = data_;
            Metadata = metadata;
            QueryType = qtype;
            ConnectionID = connectid;
        }
    }
}
