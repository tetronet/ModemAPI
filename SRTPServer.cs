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
        private bool IsWorking = false;
        private bool IsFirstStart = true;
        private IModem UnderlyingModem;
        private string QueryType;
        private uint CIDStartRange;
        private uint CIDEndRange;
        private ConcurrentDictionary<(Address, uint), SRTPClient> Sessions = [];
        public event Action<SRTPServer, SRTPClient> OnClientConnected = delegate { };
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
                UnderlyingModem.AttachReceiveEventNoUnfragment(delegate (Packet p, Action k)
                {
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
                    if (Sessions.ContainsKey((p.Transmitter, p.ConnectionID)))
                    {
                        return;
                    }
                    Task.Run(delegate ()
                    {
                        SRTPClient prepare = new(UnderlyingModem, p.Transmitter, QueryType, p.ConnectionID, 20000000);
                        prepare.RetransmissionsBeforeConsideringRemoteSideDead = 10;
                        prepare.OnClose += delegate (SRTPClient sender)
                        {
                            Sessions.TryRemove((sender.RemoteServer, sender.EndPointConnectionID), out _);
                        };
                        Sessions.TryAdd((p.Transmitter, p.ConnectionID), prepare);
                        OnClientConnected(this, prepare);
                    });
                });
            }
            IsFirstStart = false;
        }

        /// <summary>
        /// Stops this <see cref="SRTPServer"/> without corrupting any of it's states.
        /// </summary>
        public void Stop()
        {
            IsWorking = false;
        }
        /// <summary>
        /// Removes a <see cref="SRTPClient"/> from the Sessions.
        /// </summary>
        /// <param name="sid">Session ID of the client</param>
        /// <returns>Was the remove successful</returns>
        public bool RemoveClient(Address source, uint connectid)
        {
            return Sessions.TryRemove((source, connectid), out _);
        }
    }
}
