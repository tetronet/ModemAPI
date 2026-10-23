# Tetronet Protocols (Carrier-level)
### CIoCIL_LowLatShort, aka LL-CIoCIL-mini
Low Latency - Copybook Internet over Copybook Internet Lines - mini. How it works:

* Everything is BIG Endian

##### Connection establishment:
```
+----------------------------------------------------------------------+
| 0x4c4c2d43496f43494c2d6d696e                                         |
| (1 byte length) (0-255 current CI address of the connecting system)  |
| (device type 0x00 = address machine, 0xff = endpoint)                |
| (4 bytes CRC-32)                                                     |
+----------------------------------------------------------------------+
                               |
                               V
+------------------------------------------------------------------------------------------+
| 0x4c4c2d43496f43494c2d6d696e                                                             |
| (1 byte - response flags*)                                                               |
| (data, defined by the flags)                                                             |
| (4 bytes CRC-32)                                                                         |
+------------------------------------------------------------------------------------------+
| depending on the flags these can be in this order between the flags and the CRC-32:      |
| SMLA for the endpoint                                                                    |
| (1 byte length) (0-255 bytes address)                                                    |
| maximum MTU (packet)                                                                     |
| (2 bytes - max MTU)                                                                      |
| maximum MTU (large messages)                                                             |
| (8 bytes - max MTU)                                                                      |
| timeout for the connection without keep-alive                                            |
| (8 bytes - timeout in nanoseconds)                                                       |
+------------------------------------------------------------------------------------------+
```
##### Disconnecting:
```
+--------------------+
| 0x4c4c636e7462726b |
+--------------------+
           |
           V
+----------------------+
| 0x4c4c537465726d6f6b |
+----------------------+
```
##### Exchanging packets (fire-and-forget)
```
+----------------------------------------------------------------------------------------------+
| 0x56923ffd                                                                                   |
| (length byte) (0-255 bytes receiver address)                                                 |
| (length byte) (0-255 bytes transmitter address)                                              |
| (length byte) (0-255 bytes query type)                                                       |
| (4 bytes ConnectionID)                                                                       |
| (1 byte is the packet last in queue false - 0x27, true - 0xfa)                               |
| (8 bytes sequential number of the network level fragmentation, package_no)                   |
| (8 bytes id of the message that this packet participates in)                                 |
| (2 bytes TTL - how many routers the packet can still pass)                                   |
| (2 byte length) (0-65536 bytes packet data)                                                  |
| (2 byte length) (0-65536 bytes packet metadata, can contain only symbols from a list)        |
| (4 bytes CRC-32)                                                                             |
+----------------------------------------------------------------------------------------------+
                                          |
                                          V
                                 +----------------+
                                 | No response :) |
                                 +----------------+
* Metadata can contain only the symbols from PrintableCharacters string in the PrintableCharacterList.cs file
* The CRC-32 of the packet is the only little endian field: it's stored the way System.IO.Hashing.Crc32.Hash() writes it
* Over a TCP line (without More Stable Serial Packet) every packet is prefixed with 3 bytes of its length; over UDP every datagram is exactly one packet, without a length (up to 65507 bytes, a datagram may be fragmented by IP)
```
##### Large Messages
```
Large Messages in the tetronet are considered deprecated, consider using LMDTPServer and LMDTPClient, they're like a billion times faster and they don't have limitations for the size that you want to download because of that one address machine on the ceiling from 2000 years ago. LMDTP doesn't need so all of the address machines on the route will have enough space to store the large message, LMDTP is Large Message Direct Tunnel Protocol.
```
##### Keep-alive
```
Modem-KeepAlive tells both sides, that the other one is still there, so a connection lost without a disconnection request doesn't stay "connected" forever.
* The address machine (the answering modem) sends 0x77997799 every third of the connection timeout, that it gave in its connection response; the subscriber (the calling modem) answers 0x22FF22FF. The subscriber never sends 0x77997799 itself
* A Modem-KeepAlive counts as answered, if anything came from the subscriber before the next one is sent: its answer or any other data
* After 3 (configurable) Modem-KeepAlives in a row without an answer, the answering modem considers the connection lost, stops sending them and is ready for a new connection; the subscriber isn't told. Unanswered ones in between answered ones don't count: ok, lost, ok, ok, lost, ok is alive; ok, lost, lost, lost is lost
* The subscriber considers the connection lost, when nothing (Modem-KeepAlive or any other data) came from the answering modem for the whole timeout, and can connect again
* A timeout of 0 means no Modem-KeepAlive at all
* A connection response without the timeout (flag 0b00100000 not set) means the same as a timeout of 0
* Over UDP a connection request from another address or port (the same IP address too) is accepted only while there is no active connection and no connection request waiting to be answered (an unanswered one keeps the line for a while, 10 seconds by default), for example after Modem-KeepAlive found the connection lost; from the subscriber's own address and port it is accepted right away
+------------+
| 0x77997799 |
+------------+
       |
       V
+------------+
| 0x22FF22FF |
+------------+
```
##### Terms
```
LowLatency Connection Respone Flags:
0b00000001 - large message exchange supported (obsolete)
0b00000010 - SMLA is contained in the response (Set Modem Local Address)
0b00000100 - RESERVED
0b00001000 - connection response provides maximum packet size
0b00010000 - connection response provides maximum large message size (obsolete)
0b00100000 - connection response provides timeout without keep-alives
0b01000000 - RESERVED
0b10000000 - RESERVED

Empty address:
The address contains a "0", or it's empty (no address characters in it).

Invalid address:
The address contains characters outside of list (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, -), or it's an address, which can't be used to connect to the tetronet through a point, handling the current connection process.
```

# Tetronet Protocols (Transport-level)
Both protocols run inside the payload (packet data) of ordinary tetronet packets, so they work over any carrier: LL-CIoCIL-mini, RAWWS, Socket.IO and so on.

* Everything is BIG Endian
* v2 of both protocols is backward compatible: every v2 node talks to v1 nodes using v1

### SRTP, aka Simple Reliable Transmission Protocol
A reliable, ordered stream of messages between two tetronet addresses. There's no handshake: a session is identified by the remote address, the query type and the ConnectionID of the tetronet packets carrying it. Each direction of a session is a separate stream with its own sequential numbers.

##### Exchanging messages (v1)
```
+------------------------------------------------------------------+
| (8 bytes signed sequential number of the message, starting at 0) |
| (1-59992 bytes message data)                                     |
+------------------------------------------------------------------+
                                 |
                                 V
+------------------------------------------------------------------+
| (8 bytes sequential number of the received message)              |
+------------------------------------------------------------------+
* The length of the tetronet packet data tells what it is: exactly 8 bytes - an acknowledgement, more than 8 - a message, less than 8 - invalid
* The receiver acknowledges every message, the repeated ones too (the previous acknowledgement could have been lost)
* The receiver delivers messages to the application in the order of their sequential numbers
* Messages more than 1024 (MaxPacketNoFromFuture) numbers ahead of the last delivered one are dropped without an acknowledgement
* The sender retransmits a message, that wasn't acknowledged in time (the timeout is set by the application), and considers the remote side dead after 100 (RetransmissionsBeforeConsideringRemoteSideDead) retransmissions of one message
```
##### v2: batched and selective acknowledgements
```
A v2 sender puts "srtp2:" and the ID of its stream (8 random hex digits, picked once per stream) into the metadata of the
tetronet packets with its messages, for example "srtp2:1f0c93a7"; the format of the messages doesn't change. (Senders of an earlier
v2 put just "srtp2", without an ID.)
A receiver, that got a message with "srtp2" or "srtp2:<ID>", answers with v2 acknowledgements, with "srtp2a:<ID>" in the metadata
("srtp2a" to a sender without an ID):
+--------------------------------------------------------------------------------------+
| (8 bytes signed: every message up to this number is delivered, -1 - nothing yet)     |
| (0-32 bytes bitmap*: bit i is set - message (number + 1 + i) is received)            |
+--------------------------------------------------------------------------------------+
* bit i is in byte (i / 8) of the bitmap, it's bit (i % 8) counting from the least significant one; trailing zero bytes are left out

The receiver sends a v2 acknowledgement:
* after every 16 (AckEveryPackets) delivered messages
* not later than 2 ms (AckDelayMilliseconds, plus the timer resolution) after the first message it hasn't acknowledged yet was delivered
* right away, when a repeated message arrives
* after a gap in the sequential numbers: when the 3rd message after the gap arrives, and then after every 4 more, until the gap is filled (counted since the last acknowledgement)
* when the session is closed, if something isn't acknowledged yet

The receiver takes the stream of the first message it got: messages with another stream ID are neither acknowledged nor delivered.
A sender ignores acknowledgements with another stream ID, so an acknowledgement of one stream never acknowledges messages of another.

The sender:
* sends messages only while (next sequential number - first unacknowledged number) < 1024 (MaxUnackedPackets, less with the packet loss based congestion control); messages, that are only marked in the bitmap, don't move this window, they are received, but not delivered yet
* doesn't retransmit messages marked in the bitmap
* retransmits a message right away, without waiting for the timeout, when it's not marked in the bitmap, but at least 3 (FastRetransmitAfterPackets) messages after it are; it retransmits it again only when messages, sent after that retransmission, are marked and it still isn't
```
##### Compatibility
```
+-----------+-------------+------------------------------------------------------------------+
| messages  | receiver    | acknowledgements                                                 |
+-----------+-------------+------------------------------------------------------------------+
| v1        | v1          | v1                                                               |
| v2        | v1          | v1: the receiver ignores the metadata, the v2 sender accepts v1  |
| v1        | v2          | v1: there's no "srtp2" in the messages                           |
| v2        | v2          | v2                                                               |
+-----------+-------------+------------------------------------------------------------------+
* v1 and v2 acknowledgements are told apart by the metadata, not by the length: a v2 acknowledgement without the bitmap is 8 bytes long too
* If something on the route drops the metadata of every packet, both sides stay on v1
* There is no message for closing a stream. A client, that starts over with the same address and ConnectionID (it restarted or
  reconnected), just sends the messages of a new stream: a server ends the session of the old stream on any message with another
  stream ID (message 0 may be lost or come later), and opens a session for the new stream, when its message 0 comes. From senders
  without a stream ID, only a message 0 with other contents tells a new stream
* Receivers of a v1 or an earlier v2 don't know "srtp2:<ID>": they take such messages for v1 ones and acknowledge every message,
  which a v2 sender accepts
```
### LMDTP, aka Large Message Direct Tunnel Protocol
Downloads a resource (a file or any other data) straight from the server, which has it, through an SRTP session. The query type of every packet is "lm_tunnel".

##### Request (one tetronet packet, not SRTP)
```
+---------------------------------------------------------------------+
| 0xff40                                                              |
| (2 bytes length) (0-65535 bytes UTF-8 resource name)                |
| (8 bytes signed - the most bytes the client agrees to download)     |
| (1 byte request type: 0x15 - OBTAIN, 0x2f - HASH)                   |
| (1 byte supported hash algorithms*, v2 only)                        |
| (4 bytes request ID, v2 only)                                       |
+---------------------------------------------------------------------+
                                   |
                                   V
SRTP session from the server to the client with the same ConnectionID
+---------------------------------------------------------------------+
| message 1, the response header (76 bytes):                          |
| 0x72d3                                                              |
| (2 bytes response flags*)                                           |
| (64 bytes hash)                                                     |
| (8 bytes signed - resource length)                                  |
+---------------------------------------------------------------------+
| OBTAIN: next messages contain the resource data, until the length   |
| HASH: one message with the 64 bytes SHA-512 of the resource         |
+---------------------------------------------------------------------+
* The ConnectionID of the request must be in the range of the server. A v2 server has one session per client address and ConnectionID: a new request from them ends the session in progress (the client gave it up or is already done with it) and gets a new one
* A v2 client picks a new random request ID (not 0, which means "no ID") for every request. A v2 server remembers the last request IDs of every client address and ConnectionID, and ignores a request with an ID it has seen: that's a duplicate or a late copy, and answering it again would restart or replace the answer in progress. Requests without an ID (v1 clients) are always taken
* The request is sent once, without retransmission: if it gets lost, the client times out (5 seconds by default)
* OBTAIN: the hash is the hash of the resource data, the client checks it after receiving the last byte
* HASH: the hash in the header is SHA-512 of the 64 bytes in the next message, and the length is 64
* If the resource is longer than the client agrees to download, the header has 0x0002 in the flags, zero hash and zero length, and nothing follows
* v1 servers send the data in messages of 8000 bytes, v2 servers in messages of 59992 bytes; clients take any size
* A v2 server closes the session only after the client acknowledged all of the data
```
##### v2: choosing the hash
```
Supported hash algorithms:
0x01 - SHA-512 (64 bytes)
0x02 - XxHash3, 64-bit, seed 0 (8 bytes, not cryptographic, about 25 times faster than SHA-512)
* A v2 client always sets 0x01, because a v1 server answers with SHA-512 anyway
* If the client set 0x02 and the server can provide XxHash3 of the resource, the server sets 0x0100 in the response flags and the hash field contains the 8 bytes of XxHash3 followed by 56 zero bytes; otherwise it's SHA-512, as in v1
* Only OBTAIN responses can use XxHash3, HASH responses are always SHA-512
```
##### Compatibility
```
* A v1 server reads the request at fixed offsets and doesn't look at the bytes after the request type, so a v2 request works with it (the hash algorithms and the request ID are ignored), and it answers with SHA-512
* A v1 client doesn't send the supported hash algorithms, a v2 server treats it as 0x01 (SHA-512 only)
* 0x0100 is only set when the client asked for XxHash3, so a v1 client never gets it
```
##### Limits and guarantees
```
* Resource name: 0-65535 bytes of UTF-8; a v2 server ignores requests, that are shorter than their fields or whose name isn't valid UTF-8
* Resource length: 0 to the maximum the client agreed to download; an empty resource is the header alone
* Data messages: 1-59992 bytes, the most one SRTP message can carry
* The client writes the data in order into any writable stream, it doesn't need seeking
* The hash detects data damaged or lost on the way. It is not authentication: it comes in the same unauthenticated SRTP session
  as the data, so whoever can change the data on the way can change the hash too, and that is true for SHA-512 as well
* A v2 client ends the request with an error right away on an error flag, a header breaking the format, a negative length,
  a length over its maximum, more data than announced or a hash mismatch; it only times out, when nothing arrives
* A v2 server ends the session whatever happens to the request, so the same client address and ConnectionID can make a new request
```
##### Terms
```
LMDTP Response Flags:
0x0000 - success
0x0001 - the resource was not found
0x0002 - the resource is longer, than the client agrees to download
0x0004 - server-side error
0x0008 - the resource is not for this client
0x0010 - the hard drive of the server failed
0x0100 - the hash field contains XxHash3 instead of SHA-512 (v2)
```
