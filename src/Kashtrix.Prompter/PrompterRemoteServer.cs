using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kashtrix.Prompter;

public sealed class PrompterRemoteServer : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public bool IsRunning => _udp is not null;
    public int Port { get; private set; }
    public event Action<string, IPEndPoint>? CommandReceived;

    public void Start(int port)
    {
        Stop();
        Port = Math.Clamp(port, 1024, 65535);
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                var command = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (command.Length > 0) CommandReceived?.Invoke(command, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(150, ct).ConfigureAwait(false); }
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();
}
