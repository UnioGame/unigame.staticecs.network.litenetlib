using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using UniGame.StaticEcs.Network;

namespace UniGame.StaticEcs.Network.LiteNetLib.Tests
{
    [TestFixture]
    public sealed class AdapterTests
    {
        [Test]
        public void LimitsMatchNativeHeaders()
        {
            Assert.That(LiteNetLibLimits.FragmentationHeaderSize, Is.EqualTo(10));
            Assert.That(LiteNetLibLimits.MaximumFragmentsCount,
                Is.EqualTo((LiteNetLibLimits.MaximumReliableBytes +
                    LiteNetLibLimits.ReliableFragmentPayloadBytes - 1) /
                    LiteNetLibLimits.ReliableFragmentPayloadBytes));
            Assert.That(LiteNetLibSettings.Default.MaximumUnreliableBytes,
                Is.EqualTo(LiteNetLibLimits.MaximumSequencedBytes));
            Assert.That(LiteNetLibSettings.Default.Normalize(false).MaximumUnreliableBytes,
                Is.EqualTo(LiteNetLibLimits.MaximumSequencedBytes));
        }

        [Test]
        public void NativeLoopbackDeliversSequencedAndReliablePackets()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 8;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                Assert.That(client.Connected, Is.True);
                Assert.That(serverEndpoint.Connection.Value, Is.GreaterThan(0));
                Assert.That(serverEndpoint.MaxReliablePayloadBytes,
                    Is.EqualTo(LiteNetLibLimits.MaximumReliableBytes));
                Assert.That(client.Endpoint.MaxUnreliablePayloadBytes,
                    Is.LessThanOrEqualTo(LiteNetLibLimits.MaximumSequencedBytes));

                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var sequenced = CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.UnreliableSequenced, 32);
                    Assert.That(client.Endpoint.TrySend(sequenced), Is.True);
                    var receivedSequenced = WaitForReceive(server, client, serverEndpoint);
                    AssertPacket(receivedSequenced, PacketKind.Ping,
                        PacketFlags.UnreliableSequenced, 32);
                    receivedSequenced.Dispose();

                    var reliable = CreatePacket(pool, PacketKind.TransactionCommand,
                        PacketFlags.ReliableOrdered, 15000);
                    Assert.That(client.Endpoint.TrySend(reliable), Is.True);
                    var receivedReliable = WaitForReceive(server, client, serverEndpoint);
                    AssertPacket(receivedReliable, PacketKind.TransactionCommand,
                        PacketFlags.ReliableOrdered, 15000);
                    receivedReliable.Dispose();

                    var snapshot = CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 24, 20);
                    Assert.That(client.Endpoint.TrySend(snapshot), Is.True);
                    var oldSnapshot = CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 24, 19);
                    Assert.That(client.Endpoint.TrySend(oldSnapshot), Is.False);

                    WaitForDelivery(server, client);
                    var receivedSnapshot = WaitForReceive(server, client, serverEndpoint);
                    AssertPacket(receivedSnapshot, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 24);
                    receivedSnapshot.Dispose();
                    var diagnostics = client.CaptureDiagnostics();
                    Assert.That(diagnostics.ReliableSentPackets, Is.GreaterThanOrEqualTo(2));
                    Assert.That(diagnostics.UnreliableSentPackets, Is.GreaterThanOrEqualTo(1));
                    Assert.That(diagnostics.NativeReliableBytes, Is.EqualTo(0));
                    Assert.That(diagnostics.DeliveryCallbacks, Is.GreaterThanOrEqualTo(2));
                    Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                    Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));

                    var oversizedReliable = CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered,
                        LiteNetLibLimits.MaximumReliableBytes - PacketHeader.Size + 1);
                    Assert.That(client.Endpoint.TrySend(oversizedReliable), Is.False);
                    Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                }

                serverEndpoint.Dispose();
                serverEndpoint.Dispose();
                WaitForDisconnect(server, client);
            }

            using (var server = new LiteNetLibServerHost(settings))
                server.Dispose();
        }

        [Test]
        public void ReliableFifoRetainsLeaseUntilNativeCapacityIsAvailable()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);

                    var first = WaitForReceive(server, client, serverEndpoint);
                    first.Dispose();
                    var second = WaitForReceive(server, client, serverEndpoint);
                    second.Dispose();

                    WaitForDelivery(server, client);
                    Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                    Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                }
            }
        }

        [Test]
        public void ReliableReceiveOverflowDisconnectsPeer()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 1;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);

                    for (var i = 0; i < 400; i++)
                    {
                        Pump(server, client);
                        if (server.CaptureDiagnostics().ReliableReceiveOverflowDisconnects > 0)
                            break;
                        Thread.Sleep(1);
                    }

                    Assert.That(server.CaptureDiagnostics().ReliableReceiveOverflowDisconnects,
                        Is.GreaterThanOrEqualTo(1));
                    if (serverEndpoint.TryReceive(out var queued))
                        queued.Dispose();
                    WaitForDisconnect(server, client);
                }
            }
        }

        private static NetworkBufferLease CreatePacket(NetworkBufferPool pool,
            PacketKind kind, PacketFlags flags, int payloadBytes, uint tick = PacketHeader.NoneTick)
        {
            var payload = new byte[payloadBytes];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 31 + 7);
            var header = new PacketHeader
            {
                Kind = kind,
                Flags = flags,
                Compression = NetworkCompression.None,
                ServerTick = tick
            };
            Assert.That(NetworkPacket.TryEncode(pool, header, payload, out var packet), Is.True);
            return packet;
        }

        private static void AssertPacket(NetworkBufferLease packet, PacketKind kind,
            PacketFlags flags, int payloadBytes)
        {
            Assert.That(NetworkPacket.TryDecode(packet, out var header, out var payload), Is.True);
            Assert.That(header.Kind, Is.EqualTo(kind));
            Assert.That(header.Flags, Is.EqualTo(flags));
            Assert.That(payload.Length, Is.EqualTo(payloadBytes));
        }

        private static INetworkTransport WaitForAccept(LiteNetLibServerHost server,
            LiteNetLibClientHost client)
        {
            INetworkTransport accepted = null;
            for (var i = 0; i < 400; i++)
            {
                Pump(server, client);
                if (accepted == null)
                    server.TryAccept(out accepted);
                if (accepted != null && client.Connected)
                    return accepted;
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib loopback connection was not accepted.");
            return null;
        }

        private static NetworkBufferLease WaitForReceive(LiteNetLibServerHost server,
            LiteNetLibClientHost client, INetworkTransport endpoint)
        {
            for (var i = 0; i < 400; i++)
            {
                if (endpoint.TryReceive(out var packet))
                    return packet;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib loopback packet was not received.");
            return null;
        }

        private static void WaitForDelivery(LiteNetLibServerHost server,
            LiteNetLibClientHost client)
        {
            for (var i = 0; i < 400; i++)
            {
                var diagnostics = client.CaptureDiagnostics();
                if (diagnostics.NativeReliableBytes == 0 &&
                    diagnostics.DeliveryCallbacks >= 2)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib delivery callbacks did not drain reliable ownership.");
        }

        private static void WaitForDisconnect(LiteNetLibServerHost server,
            LiteNetLibClientHost client)
        {
            for (var i = 0; i < 400; i++)
            {
                if (server.TryDequeueDisconnected(out var connection))
                {
                    Assert.That(connection.Value, Is.GreaterThan(0));
                    return;
                }
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib disconnect was not observed.");
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
