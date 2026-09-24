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
| depending on the flags these can be in this odrer between the flags and the CRC-32:      |
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
##### Exchanging packets
```
+----------------------------------------------------------------------------------------------+
| 0x56923ffd                                                                                   |
| (length byte) (0-255 bytes receiver address)                                                 |
| (length byte) (0-255 bytes transmitter address)                                              |
| (length byte) (0-255 bytes query type)                                                       |
| (4 bytes ConnectionID)                                                                       |
| (1 byte is the packet last in queue false - 0x27, true - 0xfa)                               |
| (8 bytes sequentional number of the network level fragmentation, package_no)                 |
| (8 bytes id of the message that this packet participates in)                                 |
| (2 byte length) (0-65536 bytes packet data)                                                  |
| (2 byte length) (0-65536 bytes packet metadata, can contain only symbols from a list)        |
| (4 bytes CRC-32)                                                                             |
+----------------------------------------------------------------------------------------------+
                                          |
                                          V
                                 +----------------+
                                 | No response :) |
                                 +----------------+
```
##### Large Messages
```
Large Messages in the tetronet are considered deprecated, consider using LMDTPServer and LMDTPClient, they're like a billion times faster and they don't have limitations for the size that you want to download because of that one address machine on the ceiling from 2000 years ago. LMDTP doesn't need so all of the address machines on the route will have enough space to store the large message, LMDTP is Large Message Direct Tunnel Protocol.
```
##### Keep-alive
```
Sometimes is used, to know if one of the sides died to prevent desyncing states. For example if 3 keep-alives in a row receive a timeout, that can mean that the address machine is dead, and if the address machine doesn't receive the keep-alives for example for 120 seconds, it means the endpoint is dead and the states could be cleaned up.
+------------+
| 0x77997799 |
+------------+
       |
       V
+------------+
| 0x22FF22FF |
+------------+
```
