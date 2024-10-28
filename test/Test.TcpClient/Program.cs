using System.Net.Sockets;
using System.Text;

class TcpClientApp
{
    const int BUFFER_SIZE = 8192; // 8KB buffer size

    static async Task Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.WriteLine("Usage: TcpClientApp <server_ip> <port> <save_directory>");
            return;
        }

        string serverIp = args[0];
        int port = int.Parse(args[1]);
        string saveDirectory = args[2];

        if (!Directory.Exists(saveDirectory))
        {
            Directory.CreateDirectory(saveDirectory);
        }

        try
        {
            using var client = new TcpClient();
            Console.WriteLine($"Connecting to {serverIp}:{port}...");
            await client.ConnectAsync(serverIp, port);
            Console.WriteLine("Connected to server!");

            using NetworkStream stream = client.GetStream();
            await ReceiveFileAsync(stream, saveDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static async Task ReceiveFileAsync(NetworkStream stream, string saveDirectory)
    {
        // Receive file name length
        byte[] fileNameLengthBytes = new byte[sizeof(int)];
        await stream.ReadAsync(fileNameLengthBytes);
        int fileNameLength = BitConverter.ToInt32(fileNameLengthBytes);

        // Receive file name
        byte[] fileNameBytes = new byte[fileNameLength];
        await stream.ReadAsync(fileNameBytes);
        string fileName = Encoding.UTF8.GetString(fileNameBytes);

        // Receive file size
        byte[] fileSizeBytes = new byte[sizeof(long)];
        await stream.ReadAsync(fileSizeBytes);
        long fileSize = BitConverter.ToInt64(fileSizeBytes);

        string filePath = Path.Combine(saveDirectory, fileName);
        Console.WriteLine($"Receiving file: {fileName} ({fileSize} bytes)");

        // Receive file contents
        using var fileStream = File.Create(filePath);
        byte[] buffer = new byte[BUFFER_SIZE];
        long totalBytesReceived = 0;

        while (totalBytesReceived < fileSize)
        {
            int bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytesReceived += bytesRead;

            // Report progress
            double progress = (double)totalBytesReceived / fileSize * 100;
            Console.Write($"\rReceiving: {progress:F2}% ({totalBytesReceived}/{fileSize} bytes)");
        }

        Console.WriteLine("\nFile received successfully!");
        Console.WriteLine($"Saved as: {filePath}");
    }
}