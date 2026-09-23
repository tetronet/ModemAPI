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
##### Sending packets and everything else will be diagramed later on, so be patient ;)
