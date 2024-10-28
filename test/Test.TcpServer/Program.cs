using System.Net;
using System.Net.Sockets;
using System.Text;

class TcpServerApp
{
    const int BUFFER_SIZE = 8192; // 8KB buffer size

    static async Task Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: TcpServerApp <port> <filepath>");
            return;
        }

        int port = int.Parse(args[0]);
        string filePath = args[1];

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            Console.WriteLine($"Server listening on port {port}");
            Console.WriteLine($"Ready to send file: {filePath}");

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client, filePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task HandleClientAsync(TcpClient client, string filePath)
    {
        Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");

        try
        {
            using NetworkStream stream = client.GetStream();
            var sendFileTask = SendFileAsync(stream, filePath);
            var receiveDataTask = ReceiveDataAsync(stream);

            await Task.WhenAll(sendFileTask, receiveDataTask);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client: {ex.Message}");
        }
        finally
        {
            client.Close();
            Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint}");
        }
    }

    static async Task SendFileAsync(NetworkStream stream, string filePath)
    {
        // First send the file name
        string fileName = Path.GetFileName(filePath);
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(fileName);
        byte[] fileNameLengthBytes = BitConverter.GetBytes(fileNameBytes.Length);
        await stream.WriteAsync(fileNameLengthBytes);
        await stream.WriteAsync(fileNameBytes);

        // Then send the file size
        long fileSize = new FileInfo(filePath).Length;
        byte[] fileSizeBytes = BitConverter.GetBytes(fileSize);
        await stream.WriteAsync(fileSizeBytes);

        // Send the file contents
        using var fileStream = File.OpenRead(filePath);
        byte[] buffer = new byte[BUFFER_SIZE];
        long totalBytesSent = 0;
        int bytesRead;

        while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytesSent += bytesRead;

            // Report progress
            double progress = (double)totalBytesSent / fileSize * 100;
            Console.Write($"\rSending file: {progress:F2}% ({totalBytesSent}/{fileSize} bytes)");
        }

        Console.WriteLine("\nFile sent successfully!");
    }

    static async Task ReceiveDataAsync(NetworkStream stream)
    {
        var buffer = new byte[BUFFER_SIZE];
        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer);
                if (bytesRead == 0) break;

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Received from client: {message}");
            }
        }
        catch (IOException)
        {
            // Client disconnected
        }
    }
}