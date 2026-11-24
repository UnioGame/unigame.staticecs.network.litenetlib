using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using UniGame.StaticEcs.Network;

namespace UniGame.StaticEcs.Network.LiteNetLib.Tests
{
    /// <summary>A real LiteNetLib connection that runs through an impaired <see cref="NetworkLinkRelay"/>.</summary>
    [TestFixture]
    public sealed class LiteNetLibLinkRelayTests
    {
        private const int DeadlineSeconds = 30;
        private const int PacketCount = 200;

        [Test]
        public void ReliablePacketsCrossALossyDelayedLinkCompleteAndInOrder()
        {
            var serverSettings = LiteNetLibSettings.Default;
            serverSettings.Address = NetworkLinkRelay.LoopbackAddress;
            serverSettings.Port = FindFreePort();
            using var relay = new NetworkLinkRelay();
            using var server = new LiteNetLibServerHost(serverSettings);
            var link = relay.Open(serverSettings.Address, serverSettings.Port,
                new NetworkLinkImpairment { LatencyMs = 15f, JitterMs = 10f, LossPercent = 20f });
            var clientSettings = serverSettings;
            clientSettings.Port = checked((ushort)link.LocalPort);
            using var client = new LiteNetLibClientHost(clientSettings);
            using var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes);
            var deadline = Stopwatch.GetTimestamp() + DeadlineSeconds * Stopwatch.Frequency;

            INetworkTransport accepted = null;
            while (accepted == null || !client.Connected)
            {
                Assert.That(Stopwatch.GetTimestamp(), Is.LessThan(deadline),
                    "The connection through the relay was not accepted.");
                Pump(server, client);
                if (accepted == null)
                    server.TryAccept(out accepted);
                Thread.Sleep(1);
            }

            // One packet per pump keeps each in a datagram of its own: LiteNetLib merges packets flushed
            // together, and a handful of datagrams could all slip past a 20 % loss.
            // The payload length is the packet's place in the sending order.
            var sent = 0;
            var received = 0;
            while (received < PacketCount)
            {
                Assert.That(Stopwatch.GetTimestamp(), Is.LessThan(deadline),
                    $"Only {received} of {PacketCount} reliable packets arrived.");
                if (sent < PacketCount && client.Endpoint.TrySend(CreatePacket(pool, 16 + sent)))
                    sent++;
                Pump(server, client);
                while (accepted.TryReceive(out var packet))
                {
                    using (packet)
                    {
                        Assert.That(NetworkPacket.TryDecode(packet, out var header, out var payload),
                            Is.True);
                        Assert.That(header.Flags, Is.EqualTo(PacketFlags.ReliableOrdered));
                        Assert.That(payload.Length, Is.EqualTo(16 + received));
                    }
                    received++;
                }
                Thread.Sleep(1);
            }

            var diagnostics = relay.CaptureDiagnostics();
            Assert.That(diagnostics.ClientToServer.LossDrops + diagnostics.ServerToClient.LossDrops,
                Is.GreaterThan(0), "The link dropped nothing, so the transport was not tested.");
            Assert.That(diagnostics.ClientToServer.DelayedPackets, Is.GreaterThan(0));
            Assert.That(diagnostics.ServerToClient.DelayedPackets, Is.GreaterThan(0));
            Assert.That(relay.LastFailure, Is.Null);
        }

        private static NetworkBufferLease CreatePacket(NetworkBufferPool pool, int payloadBytes)
        {
            var payload = new byte[payloadBytes];
            var header = new PacketHeader
            {
                Kind = PacketKind.Ping,
                Flags = PacketFlags.ReliableOrdered,
                Compression = NetworkCompression.None,
                ServerTick = PacketHeader.NoneTick,
            };
            Assert.That(NetworkPacket.TryEncode(pool, header, payload, out var packet), Is.True);
            return packet;
        }

        private static void Pump(LiteNetLibServerHost server, LiteNetLibClientHost client)
        {
            client.Update();
            client.Flush();
            server.Update();
            server.Flush();
        }

        private static ushort FindFreePort()
        {
            using (var socket = new UdpClient(0))
                return checked((ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port);
        }
    }
}
