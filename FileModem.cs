using System.Text;
using System.Security.Cryptography;
using System.Linq;

namespace ModemAPI {
    public class FileModem : IModem
    {
        public string CommunicationDirectoryPath { get; private set; }
        public string BufferDirectoryPath { get; private set; }
        private Dictionary<string, Dictionary<string, string>> IniResolved;
        private readonly bool LogsAreEnabled;
        private LoggingIntensity EventLoggingIntensity = LoggingIntensity.None;
        private long CarryByteLimit = long.MaxValue;

        public int MaxReinitializeAttempts { get; set; }
        public Address? LocalModemAddress { get; set; }
        public LargeMessage? LastDownloadedLargeMessage { get; set; }
        public bool IsModemConnected { get; set; }

        public FileModem(string communicationPath, string bufferPath)
        {
            CommunicationDirectoryPath = communicationPath;
            BufferDirectoryPath = bufferPath;
            IniResolved = IniFileResolver.ParseFile(communicationPath + "carry/config.ini");
            if (IniResolved.GetValueOrDefault("COMMUNICATION_PROTOCOL") != null ||
                IniResolved.GetValueOrDefault("COMMUNICATION_PROTOCOL")!.GetValueOrDefault("protocol") != "ci_over_tape" ||
                IniResolved.GetValueOrDefault("COMMUNICATION_PROTOCOL")!.GetValueOrDefault("allow_transmission") != "true")
            {
                throw new InvalidDataException();
            }
            LogsAreEnabled = IniResolved.GetValueOrDefault("LOGGING_EVENTS") != null && IniResolved.GetValueOrDefault("LOGGING_EVENTS")!.GetValueOrDefault("logs_are_enabled") == "true";
            string? levelStr = IniResolved.GetValueOrDefault("LOGGING_EVENTS")?.GetValueOrDefault("logging_level");
            if (!string.IsNullOrWhiteSpace(levelStr) && Enum.TryParse<LoggingIntensity>(levelStr, true, out var parsed))
            {
                EventLoggingIntensity = parsed;
            }
            // read carry limit if provided
            string? limitStr = IniResolved.GetValueOrDefault("CARRY_LIMITS")?.GetValueOrDefault("byte_limit");
            if (!string.IsNullOrWhiteSpace(limitStr) && long.TryParse(limitStr, out var limit))
            {
                CarryByteLimit = Math.Max(0, limit);
            }
            // ensure directories exist
            Directory.CreateDirectory(CommunicationDirectoryPath + "carry/");
            Directory.CreateDirectory(CommunicationDirectoryPath + "hashes/");
            Directory.CreateDirectory(BufferDirectoryPath);
            LogMessage("FileModem initialized");
        }

        public void Dial() {
            File.WriteAllText(CommunicationDirectoryPath + $"carry/connection_request_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.hash", "[CONNECTION_REQUEST]");
            string hashFile = CommunicationDirectoryPath + $"hashes/hash_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.hash";
            byte[] dirHash = ComputeSha512ForDirectory(CommunicationDirectoryPath + "hashes/");
            File.WriteAllText(hashFile, ToHex(dirHash), Encoding.UTF8);
            TryFlushBufferToCarry();
        }

        public void Drop(bool carefulMode)
        {
            LogMessage("FileModem started to disconnect...");
            LogMessage("Writing disconnection sequence...");
            File.WriteAllText(CommunicationDirectoryPath + $"carry/disconnection_sequence_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.dat", "[DISCONNECTION_SEQUENCE]");
            LogMessage("Writing hash...");
            string hashFile = CommunicationDirectoryPath + $"hashes/hash_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.hash";
            byte[] dirHash = ComputeSha512ForDirectory(CommunicationDirectoryPath + "hashes/");
            File.WriteAllText(hashFile, ToHex(dirHash), Encoding.UTF8);
        }

        public void Transmit(byte[] data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            string header = $"[TRANSMISSION_HEADER]{address.AddressValue}{queryType}{connectionId}{metadata}";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            long payloadSize = headerBytes.LongLength + data.LongLength;

            bool wroteToCarry = TryWriteToCarry(ts, headerBytes, data, payloadSize);
            if (!wroteToCarry)
            {
                WriteToBuffer(ts, headerBytes, data);
            }
            TryFlushBufferToCarry();
        }
        public void LowLevelTransmit(byte[] data, Address address, string qt, uint cid, string? metadata)
        {
            throw new NotImplementedException();
        }
        public void Transmit(string data, Address address, string queryType, uint connectionId, string? metadata = null, ushort packetSize = 1024, int delay = 100)
        {
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            string header = $"[TRANSMISSION_HEADER]{address.AddressValue}{queryType}{connectionId}{metadata}";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(data);
            long payloadSize = headerBytes.LongLength + payloadBytes.LongLength;

            bool wroteToCarry = TryWriteToCarry(ts, headerBytes, payloadBytes, payloadSize);
            if (!wroteToCarry)
            {
                WriteToBuffer(ts, headerBytes, payloadBytes);
            }
            TryFlushBufferToCarry();
        }

        public void AttachReceiveEvent(Action<DataBlock, Action> onReceive)
        {
            throw new NotImplementedException();
        }

        public bool IsAddressSMLA()
        {
            throw new NotImplementedException();
        }

        private string GetLogsDirectory()
        {
            return CommunicationDirectoryPath + "carry/logs/";
        }

        private string GetTodayLogFile()
        {
            string dir = GetLogsDirectory();
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir + $"{DateTime.UtcNow:yyyyMMdd}.log";
        }

        public void LogError(string message)
        {
            if (!LogsAreEnabled) { return; }
            if (EventLoggingIntensity >= LoggingIntensity.Small)
            {
                AppendLogLine("ERROR", message);
            }
        }

        public void LogWarning(string message)
        {
            if (!LogsAreEnabled) { return; }
            if (EventLoggingIntensity >= LoggingIntensity.Intermediate)
            {
                AppendLogLine("WARN", message);
            }
        }

        public void LogMessage(string message)
        {
            if (!LogsAreEnabled) { return; }
            if (EventLoggingIntensity >= LoggingIntensity.High)
            {
                AppendLogLine("INFO", message);
            }
        }

        private void AppendLogLine(string level, string message)
        {
            string line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(GetTodayLogFile(), line, Encoding.UTF8);
        }

        private bool FlashAvailableAndWritable()
        {
            try
            {
                string testDir = CommunicationDirectoryPath + "carry/";
                Directory.CreateDirectory(testDir);
                string probe = Path.Combine(testDir, $".probe_{Guid.NewGuid():N}");
                File.WriteAllText(probe, "probe", Encoding.UTF8);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0L;
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        }

        private bool TryWriteToCarry(string ts, byte[] headerBytes, byte[] dataBytes, long payloadSize)
        {
            if (!FlashAvailableAndWritable())
            {
                LogWarning("Carry not available; will buffer.");
                return false;
            }
            string carryDir = CommunicationDirectoryPath + "carry/";
            long currentSize = GetDirectorySize(carryDir);
            if (currentSize + payloadSize > CarryByteLimit)
            {
                LogWarning($"Carry limit exceeded ({currentSize + payloadSize} > {CarryByteLimit}); will buffer.");
                return false;
            }
            string headerPath = Path.Combine(carryDir, $"transmission_header_{ts}.hash");
            string payloadPath = Path.Combine(carryDir, $"transmission_sequence_{ts}.dat");
            File.WriteAllBytes(headerPath, headerBytes);
            File.WriteAllBytes(payloadPath, dataBytes);
            LogMessage($"Wrote transmission to carry ({payloadSize} bytes).");
            return true;
        }

        private void WriteToBuffer(string ts, byte[] headerBytes, byte[] dataBytes)
        {
            Directory.CreateDirectory(BufferDirectoryPath);
            string headerPath = Path.Combine(BufferDirectoryPath, $"transmission_header_{ts}.hash");
            string payloadPath = Path.Combine(BufferDirectoryPath, $"transmission_sequence_{ts}.dat");
            File.WriteAllBytes(headerPath, headerBytes);
            File.WriteAllBytes(payloadPath, dataBytes);
            LogMessage($"Buffered transmission ({headerBytes.LongLength + dataBytes.LongLength} bytes).");
        }

        private void TryFlushBufferToCarry()
        {
            try
            {
                if (!Directory.Exists(BufferDirectoryPath)) return;
                if (!FlashAvailableAndWritable()) return;
                string carryDir = CommunicationDirectoryPath + "carry/";
                Directory.CreateDirectory(carryDir);

                // group header/payload by timestamp prefix to move atomically where possible
                var files = Directory.EnumerateFiles(BufferDirectoryPath, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    // estimate size impact
                    long fileLen = new FileInfo(file).Length;
                    long current = GetDirectorySize(carryDir);
                    if (current + fileLen > CarryByteLimit)
                    {
                        // cannot move further now
                        break;
                    }
                    string dest = Path.Combine(carryDir, name);
                    File.Move(file, dest, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Buffer flush failed: {ex.Message}");
            }
        }

        private static byte[] ComputeSha512ForDirectory(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentNullException(nameof(rootPath));
            }
            if (!Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException(rootPath);
            }

            using SHA512 sha = SHA512.Create();

            // Stable ordering guarantees deterministic hash
            IEnumerable<string> files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

            bool anyBlock = false;
            foreach (string file in files)
            {
                // Stream file content
                using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
                byte[] buffer = new byte[1024 * 128];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    anyBlock = true;
                }
            }

            if (anyBlock)
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return sha.Hash ?? Array.Empty<byte>();
            }
            else
            {
                // Hash of empty input
                return sha.ComputeHash(Array.Empty<byte>());
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        public Task GetLargeMessage()
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address transmitter)
        {
            throw new NotImplementedException();
        }

        public Task PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string metadata, ulong blockId)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task GetLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        Task<int> IModem.PushLargeMessage(byte[] data, string receiver, uint connectid, string queryType, string? metadata)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(Address tx, Address rx)
        {
            throw new NotImplementedException();
        }

        public Task DeleteLargeMessage(TransmitterReceiverPair txrxpair)
        {
            throw new NotImplementedException();
        }

        public void AttachReceiveEventNoUnfragment(Action<Packet, Action> onReceive)
        {
            throw new NotImplementedException();
        }
    }
}