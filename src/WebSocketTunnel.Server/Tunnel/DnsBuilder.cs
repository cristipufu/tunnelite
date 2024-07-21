namespace WebSocketTunnel.Server.Tunnel
{
    public static class DnsBuilder
    {
        public static string RandomSubdomain(int length = 8)
        {
            Random random = new();
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            return new(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}
