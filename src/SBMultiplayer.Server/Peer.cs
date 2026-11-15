using LiteNetLib;
using LiteNetLib.Utils;

namespace SBMultiplayer.Server;

/// <summary>
/// One connected player, whatever carried them here: a UDP connection (the game's own hosting) or a
/// WebSocket (a room made on the website). The game logic only talks to players through this.
/// </summary>
public interface IPeer
{
    int Id { get; }

    /// <summary>Round-trip time in milliseconds.</summary>
    int Ping { get; }

    /// <summary>Queues one message. Ordered and reliable methods are honoured by UDP; WebSocket is always ordered.</summary>
    void Send(NetDataWriter writer, DeliveryMethod method);

    /// <summary>Closes the connection and tells the player why.</summary>
    void Disconnect(string reason);
}

/// <summary>A player connected through the LiteNetLib UDP listener.</summary>
public sealed class UdpPeer : IPeer
{
    private readonly NetPeer peer;

    public UdpPeer(NetPeer peer)
    {
        this.peer = peer;
    }

    public int Id => peer.Id;

    public int Ping => peer.Ping;

    public void Send(NetDataWriter writer, DeliveryMethod method) => peer.Send(writer, method);

    public void Disconnect(string reason) => peer.Disconnect(NetDataWriter.FromString(reason));

    public override string ToString() => peer.ToString();
}
