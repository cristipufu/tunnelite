namespace WebSocketTunnel.Server.Dns
{
    public static class DnsBuilder
    {
        public static string RandomSubdomain(int length = 6)
        {
            Random random = new();
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            return new(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}
