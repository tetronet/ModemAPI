namespace ModemAPI
{
    public class WebSocketMessage
    {
        public string? package_data { get; set; }
        public string? metadata { get; set; }
        public WebSocketMessageInfo? package_info { get; set; }

        public class WebSocketMessageInfo
        {
            public uint? connectionid { get; set; }
            public List<object>? from { get; set; }
            public List<object>? to { get; set; }
            public string? package_type { get; set; }
            public bool? is_last_in_package_queue { get; set; }
            public ulong? package_no { get; set; }
            public ulong? message_id { get; set; }
        }
    }
}
