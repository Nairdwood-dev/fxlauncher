using System.Net.Sockets;
using System.Text;

namespace Nairdwood.Launcher.Services;

public sealed class FxServerRconClient
{
    public async Task<string> SendCommandAsync(
        string host,
        int port,
        string password,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("RCON host is required.");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "RCON port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Set an RCON password in both Nairdwood Launcher and server.cfg before sending commands.");
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.");

        var safeCommand = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
        using var udp = new UdpClient();
        udp.Connect(host, port);

        var body = Encoding.UTF8.GetBytes($"rcon {password} {safeCommand}");
        var packet = new byte[body.Length + 4];
        packet[0] = packet[1] = packet[2] = packet[3] = 0xFF;
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);

        await udp.SendAsync(packet, packet.Length);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        UdpReceiveResult result;
        try
        {
            result = await udp.ReceiveAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "FXServer did not answer RCON. Check that it is running, the UDP port matches, and rcon_password is set in server.cfg.");
        }

        var offset = result.Buffer.Length >= 4
                     && result.Buffer[0] == 0xFF && result.Buffer[1] == 0xFF
                     && result.Buffer[2] == 0xFF && result.Buffer[3] == 0xFF
            ? 4
            : 0;
        var response = Encoding.UTF8.GetString(result.Buffer, offset, result.Buffer.Length - offset).TrimEnd('\0', '\r', '\n');
        return response.StartsWith("print ", StringComparison.OrdinalIgnoreCase) ? response[6..] : response;
    }
}
