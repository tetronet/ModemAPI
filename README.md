### ModemAPI
This is an open-source project for connecting to the Tetronet.

Test code (Tx and Rx):

```
// Transmission side
using ModemAPI;

VirtualModem modem = new VirtualModem("wss://data-set.su:3000", new LineParams()); // create the modem
modem.Dial();
Console.Write("Connecting");
while (!modem.IsModemConnected)
{
  Console.Write('.');
  Thread.Sleep(50);
}
Console.WriteLine("Connected to tetronet with local address " + modem.LocalModemAddress);
Console.Write("Enter destination address (shown by the Rx program): ");
Address dest = new Address(Console.ReadLine() ?? "");
while (true)
{
  modem.Transmit("Hello, world!", dest, "message", 22384112); // data (byte[] or string), receiver, query type, connection id
  // for messages it will be qt="message" and cid=22384112
  // for files - qt="ftci" and cid=99451
  // this is peer-to-peer over tetronet for messages and files, but packet loss is a thing, explained in the readme
  Thread.Sleep(1000); // don't DDoS my server guys, i have iptables
}
```

```
// Receiver code
using ModemAPI;

VirtualModem modem = new VirtualModem("wss://data-set.su:3000", new LineParams()); // create the modem
modem.Dial();
Console.Write("Connecting");
while (!modem.IsModemConnected)
{
  Console.Write('.');
  Thread.Sleep(50);
}
Console.WriteLine("Connected to tetronet with local address " + modem.LocalModemAddress); // use that address in the Tx program

modem.AttachReceiveEvent(delegate (DataBlock remoteMessage, Action obsolete)
{
  Console.WriteLine($"Network message received from {remoteMessage.Transmitter}: " + remoteMessage.DataString);
});
```

### Important
Tetronet doesn't deliver packets 100% of the time, use the `SRTPClient` to prevent packet loss and reordering.

### Things you'll need to do
1) Install doghappy's SocketIOClient library
2) Install System.IO.Ports library
3) Install System.IO.Hashing library
4) Copy the code from above
5) Have fun! Tetronet is made for people, not for asking for your wallet!

Cmds:
`dotnet add package SocketIOClient`
`dotnet add package System.IO.Ports`
`dotnet add package System.IO.Hashing`

### Supporting the project
I don't know for to use donation services, just have fun and this will support me. Build you tetronet-compatible infrastructure, develop protocols, servers, clients, web-browsers, terminals, remote desktops and other stuff, I'm gonna appreciate that.
Basically just have fun. I'm not gonna ask you for anything. I'm new on github, so don't judge me for my codestyle, or README, I made it in like a few minutes xd
You could use my tetronet server, wss://data-set.su:3000/, but soon I'm gonna open-source it, so all of us will be able to make tetronet a real network with thousands or even a hundred thousand computers.

### P.s.
Guys, look though other of my projects, they're free and open source cause I don't need money. I need a program used by a lot of people. I don't want to earn billion dollars, I want so you will appreciate my work. I'm spending weeks or sometimes even months on the development and don't ask you any subscription or "premium" features. It's all free.
I'm gonna improve this README later, because ModemAPI has much more than just VirtualModem. It's supporting a lot of protocols, like LL-CIoCIL-mini (Low Latency Copybook Internet over Copybook Internet Lines mini). Yup, tetronet was originally called Copybook Internet, because I wanted to inject flexible computers into the blank/root of the copybook and connect those copybooks together. Why? Don't ask me, I was a kid back then.
Also, don't be lazy. Look though the code. At least the user-method names. I tried making it readable.
