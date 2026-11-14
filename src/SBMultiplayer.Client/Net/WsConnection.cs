using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// The WebSocket to a room, through the website. Cloudflare carries WebSockets (HTTP over TCP), not the game's
/// former UDP, so every player connects to the same address and no port is opened anywhere.
/// The payloads are the same binary messages as before. Receives on a background thread and sends in order
/// from a queue, so the game never waits on the network.
/// Events: <c>onOpen</c> once connected; <c>onMessage</c> per message; <c>onClosed</c> once, with the reason:
/// the close text sent by the server, or <see cref="ServerReasons"/> codes for connection failures.
/// </summary>
internal sealed class WsConnection
{
    private static readonly Regex StatusCodeInError = new(@"status code '(\d+)'", RegexOptions.Compiled);

    private readonly Uri uri;
    private readonly IReadOnlyDictionary<string, string> headers;
    private readonly Action<WsConnection> onOpen;
    private readonly Action<WsConnection, byte[]> onMessage;
    private readonly Action<WsConnection, string> onClosed;
    private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource cancel = new();
    private ClientWebSocket? socket;
    private int finished;

    public WsConnection(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        Action<WsConnection> onOpen,
        Action<WsConnection, byte[]> onMessage,
        Action<WsConnection, string> onClosed)
    {
        this.uri = uri;
        this.headers = headers;
        this.onOpen = onOpen;
        this.onMessage = onMessage;
        this.onClosed = onClosed;
    }

    /// <summary>
    /// Round-trip time in milliseconds. WebSocket doesn't expose one, so this stays 0 (as on the server):
    /// the round clock is not corrected for latency.
    /// </summary>
    public int Ping => 0;

    /// <summary>Starts connecting in the background; the events follow on the network thread.</summary>
    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>
    /// Queues a message. The delivery method only exists to match the old UDP call sites;
    /// WebSocket is always reliable and ordered.
    /// </summary>
    public void Send(NetDataWriter writer, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
    {
        if (Volatile.Read(ref finished) == 0)
        {
            outgoing.Writer.TryWrite(writer.CopyData());
        }
    }

    /// <summary>Closes the connection from our side. <paramref name="onClosed"/> still runs once.</summary>
    public void Disconnect(string reason = "") => Finish(reason, sendClose: true);

    private async Task RunAsync()
    {
        var ws = new ClientWebSocket();
        socket = ws;
        foreach (KeyValuePair<string, string> header in headers)
        {
            ws.Options.SetRequestHeader(header.Key, header.Value);
        }
        try
        {
            await ws.ConnectAsync(uri, cancel.Token);
        }
        catch (WebSocketException e)
        {
            Finish(FailureReason(e.Message), sendClose: false);
            return;
        }
        catch (Exception)
        {
            Finish(ServerReasons.Unreachable, sendClose: false);
            return;
        }
        if (Volatile.Read(ref finished) != 0)
        {
            return; // disconnected while connecting
        }

        onOpen(this);
        _ = Task.Run(SendLoopAsync);

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, cancel.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Finish(result.CloseStatusDescription ?? "", sendClose: true); // the server closed: answer the handshake
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    byte[] data = message.ToArray();
                    message.SetLength(0);
                    if (Volatile.Read(ref finished) == 0)
                    {
                        onMessage(this, data);
                    }
                }
            }
        }
        catch (Exception)
        {
            Finish("", sendClose: false); // dropped: the caller reports it as a lost connection
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (byte[] data in outgoing.Reader.ReadAllAsync(cancel.Token))
            {
                await socket!.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancel.Token);
            }
        }
        catch (Exception)
        {
            Finish("", sendClose: false);
        }
    }

    private void Finish(string reason, bool sendClose)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0)
        {
            return;
        }
        outgoing.Writer.TryComplete();
        ClientWebSocket? ws = socket;
        if (ws != null)
        {
            if (sendClose && ws.State == WebSocketState.Open)
            {
                _ = CloseGracefullyAsync(ws, reason);
            }
            else
            {
                ws.Abort();
            }
        }
        cancel.Cancel();
        onClosed(this, reason);
    }

    /// <summary>Sends the close frame, waits briefly for the answer, then drops the socket.</summary>
    private static async Task CloseGracefullyAsync(ClientWebSocket ws, string reason)
    {
        try
        {
            string text = reason.Length > 100 ? reason[..100] : reason;
            Task close = ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, text, CancellationToken.None);
            await Task.WhenAny(close, Task.Delay(1000));
        }
        catch (Exception)
        {
            // already closing: nothing more to do
        }
        finally
        {
            ws.Abort();
            ws.Dispose();
        }
    }

    /// <summary>A refused handshake names its HTTP status ("...status code '401'..."); anything else is unreachable.</summary>
    private static string FailureReason(string message)
    {
        Match match = StatusCodeInError.Match(message);
        return match.Success ? "HTTP_STATUS:" + match.Groups[1].Value : ServerReasons.Unreachable;
    }
}
