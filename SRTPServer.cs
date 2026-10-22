using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public class SRTPServer
    {
        private volatile bool IsWorking = false;
        private bool IsFirstStart = true;
        private IModem UnderlyingModem;
        private string QueryType;
        private uint CIDStartRange;
        private uint CIDEndRange;
        private ConcurrentDictionary<(Address, uint), SRTPClient> Sessions = [];
        /// <summary>
        /// Gets called on a thread pool thread for every new client. Messages of the client are delivered from the moment its
        /// <see cref="SRTPClient.OnMessageReceived"/> is set (or this handler returns), so none of them is missed; the handler may
        /// subscribe and then wait for messages.
        /// </summary>
        public event Action<SRTPServer, SRTPClient> OnClientConnected = delegate { };
        /// <summary>
        /// The most sessions this server keeps at the same time; first messages of new clients over the limit are ignored
        /// (the client retransmits them). Default 256.
        /// </summary>
        public int MaxSessions = 256;
        /// <summary>
        /// Retransmission timeout of the sessions this server creates, in ticks (100 ns). Default is 2 seconds.
        /// </summary>
        public int SessionTimeoutTicks = 20000000;
        public SRTPServer(IModem underlyingModem, string queryType, uint connectionIdRangeStart, uint connectionIdRangeEnd)
        {
            UnderlyingModem = underlyingModem;
            QueryType = queryType;
            CIDStartRange = connectionIdRangeStart;
            CIDEndRange = connectionIdRangeEnd;
        }
        /// <summary>
        /// Starts this <see cref="SRTPServer"/> for listening to incoming Connects from the remote <see cref="SRTPClient"/>.
        /// </summary>
        public void Start()
        {
            IsWorking = true;
            if (IsFirstStart)
            {
                UnderlyingModem.AttachReceiveEventNoUnfragment(OnPacket);
            }
            IsFirstStart = false;
        }

        private void OnPacket(Packet p, Action k)
        {
            if (!IsWorking)
            {
                return;
            }
            // check Connection ID range
            if (CIDStartRange > p.ConnectionID || p.ConnectionID > CIDEndRange)
            {
                return;
            }
            // check the query type
            if (QueryType != p.QueryType)
            {
                return;
            }
            // other processing
            (Address, uint) key = (p.Transmitter, p.ConnectionID);
            if (Sessions.ContainsKey(key))
            {
                return;
            }
            // only the first message of a stream opens a session: an acknowledgement, or a late message of a session that is gone,
            // would open a session waiting for message 0 forever. If message 0 got lost, its retransmission opens the session.
            if (!IsFirstMessage(p) || Sessions.Count >= MaxSessions)
            {
                return;
            }
            // the session is created right here on the modem's thread: if it were created later, every packet arriving
            // in the meantime would find no session yet and create one more
            SRTPClient prepare = new(UnderlyingModem, p.Transmitter, QueryType, p.ConnectionID, SessionTimeoutTicks);
            // nothing is delivered before OnClientConnected, where the application subscribes to the messages; packets of
            // this session are handled on this same thread, so none of them can be delivered before this line
            prepare.HoldDelivery();
            prepare.RetransmissionsBeforeConsideringRemoteSideDead = 10;
            prepare.OnClose += delegate (SRTPClient sender)
            {
                Sessions.TryRemove(new KeyValuePair<(Address, uint), SRTPClient>((sender.RemoteServer, sender.EndPointConnectionID), sender));
            };
            if (!Sessions.TryAdd(key, prepare))
            {
                prepare.Close();
                return;
            }
            // the packet, that opened the session, came before the session subscribed to the modem; it is handed over right away
            // (still on the modem's thread, so before the next packets), and waits with them until the application subscribes
            prepare.Receive(p);
            Task.Run(delegate ()
            {
                try
                {
                    OnClientConnected(this, prepare);
                }
                finally
                {
                    prepare.ReleaseDelivery();
                }
            });
        }

        private static bool IsFirstMessage(Packet p)
        {
            // a message is the sequential number and at least one byte; acknowledgements are 8 bytes or batched ("srtp2a")
            return p.Metadata != "srtp2a" && p.DataBytes.Length > 8 && BinaryPrimitives.ReadInt64BigEndian(p.DataBytes) == 0;
        }

        /// <summary>
        /// Stops this <see cref="SRTPServer"/> without corrupting any of it's states: it doesn't accept new clients anymore,
        /// the sessions already connected go on. <see cref="Start"/> makes it accept them again.
        /// </summary>
        public void Stop()
        {
            IsWorking = false;
        }
        /// <summary>
        /// Removes a <see cref="SRTPClient"/> from the Sessions and closes it.
        /// </summary>
        /// <param name="source">Remote address of the client</param>
        /// <param name="connectid">Connection ID of the client</param>
        /// <returns>Was the remove successful</returns>
        public bool RemoveClient(Address source, uint connectid)
        {
            if (!Sessions.TryRemove((source, connectid), out SRTPClient? client))
            {
                return false;
            }
            client.Close();
            return true;
        }
    }
}
