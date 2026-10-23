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
        private bool IsAttached = false;
        private readonly Lock StateLock = new();
        private IModem UnderlyingModem;
        private string QueryType;
        private uint CIDStartRange;
        private uint CIDEndRange;
        private ConcurrentDictionary<(Address, uint), SRTPClient> Sessions = [];
        // the first message of the stream of every session: its stream ID (newer clients) and its bytes (older ones)
        private readonly ConcurrentDictionary<SRTPClient, (string? StreamId, byte[] FirstMessage)> StreamStarts = new();
        private Timer? IdleCheck;
        private TimeSpan IdleTimeout = Timeout.InfiniteTimeSpan;

        private sealed class IdleCheckState
        {
            // the timer holds the server only weakly, so a server nobody uses anymore can be collected
            public required WeakReference<SRTPServer> Server;
            public Timer? Timer;
        }
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
        /// A session, that gets no packets for this long, is closed (checked once a second while the server is started). SRTP has no
        /// message for closing, so without it a session of a client, that went away, lives until <see cref="RemoveClient"/>.
        /// Infinite (off) by default, because a quiet client is not always gone. Can be changed at any time.
        /// </summary>
        public TimeSpan IdleSessionTimeout
        {
            get { return IdleTimeout; }
            set
            {
                lock (StateLock)
                {
                    IdleTimeout = value;
                    UpdateIdleCheck();
                }
            }
        }
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
            lock (StateLock)
            {
                IsWorking = true;
                if (!IsAttached)
                {
                    UnderlyingModem.AttachReceiveEventNoUnfragment(OnPacket);
                    IsAttached = true;
                }
                UpdateIdleCheck();
            }
        }

        /// <summary>
        /// Runs the idle check only while it can do something: the server is started and the timeout isn't infinite.
        /// Must be called under <see cref="StateLock"/>.
        /// </summary>
        private void UpdateIdleCheck()
        {
            bool needed = IsWorking && IdleTimeout != Timeout.InfiniteTimeSpan;
            if (needed && IdleCheck == null)
            {
                IdleCheckState state = new() { Server = new(this) };
                state.Timer = new Timer(static stateObject =>
                {
                    IdleCheckState state = (IdleCheckState)stateObject!;
                    if (state.Server.TryGetTarget(out SRTPServer? server))
                    {
                        server.CloseIdleSessions();
                    }
                    else
                    {
                        state.Timer?.Dispose();
                    }
                }, state, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                IdleCheck = state.Timer;
            }
            else if (!needed && IdleCheck != null)
            {
                IdleCheck.Dispose();
                IdleCheck = null;
            }
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
            if (Sessions.TryGetValue(key, out SRTPClient? existing))
            {
                // SRTP has no message for closing: a client, that starts over with the same ConnectionID (it restarted or
                // reconnected), just sends the messages of a new stream. Any of them (message 0 may be lost or come later) ends the
                // session of the old stream; the new stream gets a session of its own, when its message 0 comes.
                if (SRTPClient.IsAcknowledgement(p) || !IsAnotherStream(existing, p))
                {
                    return;
                }
                EndSession(key, existing);
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
            // a new stream of the same client is this server's business: it ends this session and opens another one
            prepare.ReportsForeignStreams = false;
            prepare.RetransmissionsBeforeConsideringRemoteSideDead = 10;
            prepare.OnClose += delegate (SRTPClient sender)
            {
                Sessions.TryRemove(new KeyValuePair<(Address, uint), SRTPClient>((sender.RemoteServer, sender.EndPointConnectionID), sender));
                StreamStarts.TryRemove(sender, out _);
            };
            StreamStarts[prepare] = (SRTPClient.StreamIdOf(p), p.DataBytes);
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

        /// <summary>
        /// Tells if a message belongs to another stream than the one of the session: by the stream ID, that newer clients put into
        /// every message. Older clients send no stream ID; then only their message 0 tells, by its contents, and a new stream, that
        /// starts with exactly the same message or whose message 0 doesn't come first, can't be told from the old one.
        /// </summary>
        private bool IsAnotherStream(SRTPClient session, Packet message)
        {
            if (!StreamStarts.TryGetValue(session, out (string? StreamId, byte[] FirstMessage) start))
            {
                return false;
            }
            string? streamId = SRTPClient.StreamIdOf(message);
            if (streamId != start.StreamId)
            {
                return true;
            }
            return streamId == null && IsFirstMessage(message) && !message.DataBytes.AsSpan().SequenceEqual(start.FirstMessage);
        }

        private void EndSession((Address, uint) key, SRTPClient session)
        {
            if (Sessions.TryRemove(new KeyValuePair<(Address, uint), SRTPClient>(key, session)))
            {
                session.CloseWithoutFinalAcknowledgement();
            }
            StreamStarts.TryRemove(session, out _);
        }

        private void CloseIdleSessions()
        {
            TimeSpan timeout = IdleSessionTimeout;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return;
            }
            foreach (KeyValuePair<(Address, uint), SRTPClient> session in Sessions)
            {
                if (session.Value.TimeSinceLastPacket > timeout)
                {
                    EndSession(session.Key, session.Value);
                }
            }
        }

        private static bool IsFirstMessage(Packet p)
        {
            // a message is the sequential number and at least one byte; acknowledgements are 8 bytes or batched ("srtp2a...")
            return !SRTPClient.IsAcknowledgement(p) && p.DataBytes.Length > 8 && BinaryPrimitives.ReadInt64BigEndian(p.DataBytes) == 0;
        }

        /// <summary>
        /// Stops this <see cref="SRTPServer"/> without corrupting any of it's states: it doesn't accept new clients anymore (it
        /// unsubscribes from the modem), the sessions already connected go on, but aren't checked for idleness. <see cref="Start"/>
        /// makes it accept them again.
        /// </summary>
        public void Stop()
        {
            lock (StateLock)
            {
                IsWorking = false;
                if (IsAttached)
                {
                    UnderlyingModem.DetachReceiveEventNoUnfragment(OnPacket);
                    IsAttached = false;
                }
                UpdateIdleCheck();
            }
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
            StreamStarts.TryRemove(client, out _);
            return true;
        }
    }
}
