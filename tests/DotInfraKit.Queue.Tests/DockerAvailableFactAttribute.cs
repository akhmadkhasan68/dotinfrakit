using System.Net.Sockets;

namespace DotInfraKit.Queue.Tests;

public sealed class DockerAvailableFactAttribute : FactAttribute
{
    public DockerAvailableFactAttribute()
    {
        if (!IsDockerAvailable())
            Skip = "Docker is not running. Start Docker/OrbStack to run Redis integration tests.";
    }

    internal static bool IsDockerAvailable()
    {
        var candidates = new[]
        {
            "/var/run/docker.sock",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orbstack/run/docker.sock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock")
        };
        return candidates.Any(CanPingDockerApi);
    }

    private static bool CanPingDockerApi(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 2000;
            socket.SendTimeout = 2000;
            socket.Connect(new UnixDomainSocketEndPoint(path));

            var request = "GET /_ping HTTP/1.0\r\nHost: localhost\r\n\r\n"u8.ToArray();
            socket.Send(request);

            var buffer = new byte[128];
            var received = socket.Receive(buffer);
            var response = System.Text.Encoding.ASCII.GetString(buffer, 0, received);
            return response.StartsWith("HTTP/1.", StringComparison.Ordinal) && response.Contains(" 200 ");
        }
        catch
        {
            return false;
        }
    }
}
