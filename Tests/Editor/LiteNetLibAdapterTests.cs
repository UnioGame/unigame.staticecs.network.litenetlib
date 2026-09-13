using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Native = global::LiteNetLib;
using NUnit.Framework;
using UniGame.StaticEcs.Network;

namespace UniGame.StaticEcs.Network.LiteNetLib.Tests
{
    [TestFixture]
    public sealed class LiteNetLibAdapterTests
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
        public void NativePacketPoolSizeNormalizationAppliesRoleDefaultsAndBounds()
        {
            var client = LiteNetLibSettings.Default.Normalize(false);
            Assert.That(client.NativePacketPoolSize, Is.EqualTo(1000));

            var listener = LiteNetLibSettings.Default.Normalize(true);
            Assert.That(listener.NativePacketPoolSize, Is.EqualTo(8192));

            var belowMinimum = LiteNetLibSettings.Default;
            belowMinimum.NativePacketPoolSize =
                LiteNetLibSettings.MinimumNativePacketPoolSize - 1;
            Assert.That(belowMinimum.Normalize(false).NativePacketPoolSize,
                Is.EqualTo(LiteNetLibSettings.MinimumNativePacketPoolSize));

            var aboveMaximum = LiteNetLibSettings.Default;
            aboveMaximum.NativePacketPoolSize =
                LiteNetLibSettings.MaximumNativePacketPoolSize + 1;
            Assert.That(aboveMaximum.Normalize(false).NativePacketPoolSize,
                Is.EqualTo(LiteNetLibSettings.MaximumNativePacketPoolSize));

            var hugeListener = LiteNetLibSettings.Default;
            hugeListener.MaximumConnections = int.MaxValue;
            Assert.That(hugeListener.Normalize(true).NativePacketPoolSize,
                Is.EqualTo(LiteNetLibSettings.MaximumNativePacketPoolSize));
        }

        [Test]
        public void SimultaneousHostsReportIndependentNativePacketPoolCapacities()
        {
            var firstSettings = LiteNetLibSettings.Default;
            firstSettings.Address = "127.0.0.1";
            firstSettings.Port = FindFreePort();
            firstSettings.NativePacketPoolSize = 1000;

            var secondSettings = LiteNetLibSettings.Default;
            secondSettings.Address = "127.0.0.1";
            secondSettings.Port = FindFreePort();
            secondSettings.NativePacketPoolSize = 2048;

            using (var first = new LiteNetLibServerHost(firstSettings))
            using (var second = new LiteNetLibServerHost(secondSettings))
            {
                Assert.That(first.CaptureDiagnostics().NativePacketPoolCapacity,
                    Is.EqualTo(1000));
                Assert.That(second.CaptureDiagnostics().NativePacketPoolCapacity,
                    Is.EqualTo(2048));
            }
        }

        [Test]
        public void NativePacketPoolDiagnosticsObserveLowWaterAfterTraffic()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 8;
            settings.NativePacketPoolSize = 1000;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var initial = client.CaptureDiagnostics();
                Assert.That(initial.NativePacketPoolLowWater, Is.EqualTo(-1));
                Assert.That(initial.NativePacketPoolCapacity, Is.EqualTo(1000));

                var serverEndpoint = WaitForAccept(server, client);
                var callbacksBefore = client.CaptureDiagnostics().DeliveryCallbacks;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    var received = WaitForReceive(server, client, serverEndpoint);
                    received.Dispose();
                    WaitForDeliveryAtLeast(server, client, callbacksBefore + 1);

                    var observed = -1;
                    var capacity = initial.NativePacketPoolCapacity;
                    for (var i = 0; i < 400; i++)
                    {
                        client.Update();
                        client.Flush();
                        var diagnostics = client.CaptureDiagnostics();
                        if (diagnostics.NativePacketPoolLowWater >= 0)
                        {
                            observed = diagnostics.NativePacketPoolLowWater;
                            capacity = diagnostics.NativePacketPoolCapacity;
                            break;
                        }
                        Thread.Sleep(1);
                    }

                    Assert.That(observed, Is.GreaterThanOrEqualTo(0));
                    Assert.That(observed, Is.LessThanOrEqualTo(capacity));
                    var final = client.CaptureDiagnostics();
                    Assert.That(final.NativePacketPoolCount, Is.InRange(0, capacity));
                    Assert.That(final.NativePacketPoolLowWater, Is.InRange(0, capacity));
                }
            }
        }

        [Test]
        public void ScaledNativePacketPoolReusesWarmedPacketsWithoutRefillAllocations()
        {
            var smallPoolAllocations =
                MeasureWarmedNativePacketPoolAllocations(1000, out var smallLowWater);
            var scaledPoolAllocations =
                MeasureWarmedNativePacketPoolAllocations(2048, out var scaledLowWater);

            TestContext.Progress.WriteLine($"Warmed native pool allocation bytes: small={smallPoolAllocations}, "+
                $"scaled={scaledPoolAllocations}.");

            Assert.That(smallLowWater, Is.Zero);
            Assert.That(scaledLowWater, Is.GreaterThan(0));
#if !UNITY_5_3_OR_NEWER
            Assert.That(scaledPoolAllocations + 4096, Is.LessThan(smallPoolAllocations),
                $"Warmed native pool allocations were small={smallPoolAllocations}, " +
                $"scaled={scaledPoolAllocations} bytes.");
#endif
        }

        [Test]
        public void NonlocalClientDestinationUsesEphemeralLocalSocket()
        {
            var settings = LiteNetLibSettings.Default;
            settings.Address = "192.0.2.1";
            settings.Port = 7777;

            using (var client = new LiteNetLibClientHost(settings))
            {
                Assert.That(client.Endpoint, Is.Not.Null);
                Assert.That(client.Connected, Is.False);
            }
        }

        [Test]
        public void ListenerStillRejectsUnavailableNonlocalBind()
        {
            var settings = LiteNetLibSettings.Default;
            settings.Address = "192.0.2.1";
            settings.Port = FindFreePort();
            LiteNetLibServerHost server = null;

            try
            {
                Assert.That(() => server = new LiteNetLibServerHost(settings), Throws.Exception);
            }
            finally
            {
                server?.Dispose();
            }
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
                var nativeBeforeProtocol = client.CaptureDiagnostics();
                Assert.That(nativeBeforeProtocol.NativeSentPackets, Is.GreaterThan(0));
                Assert.That(nativeBeforeProtocol.NativeReceivedPackets, Is.GreaterThan(0));
                Assert.That(nativeBeforeProtocol.NativeSentBytes, Is.GreaterThan(0));
                Assert.That(nativeBeforeProtocol.NativeReceivedBytes, Is.GreaterThan(0));
                Assert.That(nativeBeforeProtocol.NativePacketLoss, Is.GreaterThanOrEqualTo(0));

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
                    Assert.That(diagnostics.NativeSentPackets,
                        Is.GreaterThanOrEqualTo(nativeBeforeProtocol.NativeSentPackets));
                    Assert.That(diagnostics.NativeReceivedPackets,
                        Is.GreaterThanOrEqualTo(nativeBeforeProtocol.NativeReceivedPackets));
                    Assert.That(diagnostics.NativeSentBytes,
                        Is.GreaterThanOrEqualTo(nativeBeforeProtocol.NativeSentBytes));
                    Assert.That(diagnostics.NativeReceivedBytes,
                        Is.GreaterThanOrEqualTo(nativeBeforeProtocol.NativeReceivedBytes));
                    Assert.That(diagnostics.NativePacketLoss,
                        Is.GreaterThanOrEqualTo(nativeBeforeProtocol.NativePacketLoss));
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

                    var staged = client.CaptureDiagnostics();
                    Assert.That(staged.PendingReliablePackets, Is.EqualTo(1));
                    Assert.That(staged.PendingReliableBytes,
                        Is.EqualTo(PacketHeader.Size + 32));

                    var first = WaitForReceive(server, client, serverEndpoint);
                    AssertPacket(first, PacketKind.Ping, PacketFlags.ReliableOrdered, 32);
                    first.Dispose();
                    var second = WaitForReceive(server, client, serverEndpoint);
                    AssertPacket(second, PacketKind.Pong, PacketFlags.ReliableOrdered, 32);
                    second.Dispose();

                    WaitForDelivery(server, client);
                    var complete = client.CaptureDiagnostics();
                    Assert.That(complete.PendingReliablePackets, Is.Zero);
                    Assert.That(complete.PendingReliableBytes, Is.Zero);
                    Assert.That(complete.NativeReliableFragments, Is.Zero);
                    Assert.That(complete.NativeReliableBytes, Is.Zero);
                    Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                    Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(0));
                }
            }
        }

        [Test]
        public void ReliableFifoEnqueueDoesNotAllocatePerPacket()
        {
            const int WarmCount = 1100;
            const int MeasuredCount = 512;
            const int PayloadBytes = 32;
            var packetBytes = PacketHeader.Size + PayloadBytes;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;
            settings.ReliableSendQueueCapacity = WarmCount + MeasuredCount + 64;
            settings.ReliableSendBytesCapacity =
                (WarmCount + MeasuredCount + 64L) * packetBytes;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Hello,
                        PacketFlags.ReliableOrdered, PayloadBytes)), Is.True);
                    Assert.That(client.CaptureDiagnostics().NativeReliableFragments,
                        Is.EqualTo(1));

                    for (var i = 0; i < WarmCount; i++)
                    {
                        Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                            PacketFlags.ReliableOrdered, PayloadBytes)), Is.True);
                    }

                    var measured = new NetworkBufferLease[MeasuredCount];
                    for (var i = 0; i < MeasuredCount; i++)
                    {
                        measured[i] = CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, PayloadBytes);
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    var accepted = 0;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < MeasuredCount; i++)
                    {
                        if (client.Endpoint.TrySend(measured[i]))
                            accepted++;
                    }
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                    Assert.That(accepted, Is.EqualTo(MeasuredCount));
                    Assert.That(allocated, Is.LessThanOrEqualTo(4096),
                        $"FIFO enqueue allocated {allocated} bytes for {MeasuredCount} packets.");

                    var diagnostics = client.CaptureDiagnostics();
                    Assert.That(diagnostics.PendingReliablePackets,
                        Is.EqualTo(WarmCount + MeasuredCount));
                    Assert.That(diagnostics.PendingReliableBytes,
                        Is.EqualTo((WarmCount + MeasuredCount) * (long)packetBytes));

                    client.Endpoint.Dispose();
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);
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

        [Test]
        public void FragmentedReliableDeliveryRetainsLeaseUntilOneMessageCallback()
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
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacksBefore = client.CaptureDiagnostics().DeliveryCallbacks;
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 60000, 20)), Is.True);

                    client.Update();
                    client.Flush();
                    var stalled = client.CaptureDiagnostics();
                    Assert.That(stalled.NativeReliableFragments, Is.GreaterThan(1));
                    Assert.That(stalled.NativeReliableBytes, Is.GreaterThan(60000));
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);

                    var received = WaitForReceive(server, client, serverEndpoint);
                    received.Dispose();
                    WaitForDeliveryAtLeast(server, client, callbacksBefore + 1);

                    var complete = client.CaptureDiagnostics();
                    Assert.That(complete.NativeReliableFragments, Is.Zero);
                    Assert.That(complete.NativeReliableBytes, Is.Zero);
                    Assert.That(complete.OutstandingLeases, Is.Zero);
                    Assert.That(complete.DeliveryCallbacks - callbacksBefore, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void NativeFragmentCountLimitRejectsPermissivePeer()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            using (var native = new NativePeerHarness(port,
                       LiteNetLibLimits.MaximumFragmentsCount + 1))
            {
                var endpoint = WaitForNativeAccept(server, native);
                var before = server.CaptureDiagnostics();
                var tooManyFragments = new byte[
                    checked((LiteNetLibLimits.MaximumFragmentsCount + 1) *
                        LiteNetLibLimits.ReliableFragmentPayloadBytes)];
                native.Send(tooManyFragments, Native.DeliveryMethod.ReliableOrdered);

                for (var i = 0; i < 500; i++)
                {
                    native.Pump();
                    server.Update();
                    server.Flush();
                    Thread.Sleep(1);
                }

                var after = server.CaptureDiagnostics();
                Assert.That(after.ReceivedPackets, Is.EqualTo(before.ReceivedPackets));
                Assert.That(after.MalformedPackets, Is.EqualTo(before.MalformedPackets));
                Assert.That(endpoint.TryReceive(out var packet), Is.False);
                Assert.That(native.IsConnected, Is.True);
                Assert.That(after.Connections, Is.EqualTo(1));
            }
        }

        [Test]
        public void PostReassemblyReliableOversizeDisconnectsSender()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            using (var native = new NativePeerHarness(port,
                       LiteNetLibLimits.MaximumFragmentsCount))
            {
                WaitForNativeAccept(server, native);
                var oversized = new byte[LiteNetLibLimits.MaximumReliableBytes + 1];
                native.Send(oversized, Native.DeliveryMethod.ReliableOrdered);

                var disconnected = false;
                for (var i = 0; i < 1200; i++)
                {
                    native.Pump();
                    server.Update();
                    server.Flush();
                    if (server.TryDequeueDisconnected(out var connection))
                    {
                        Assert.That(connection.Value, Is.GreaterThan(0));
                        disconnected = true;
                        break;
                    }
                    Thread.Sleep(1);
                }

                var diagnostics = server.CaptureDiagnostics();
                Assert.That(disconnected, Is.True);
                Assert.That(diagnostics.MalformedPackets, Is.GreaterThanOrEqualTo(1));
                Assert.That(diagnostics.Connections, Is.Zero);
                Assert.That(diagnostics.OutstandingLeases, Is.Zero);
            }
        }

        [Test]
        public void EveryRejectedSendConsumesItsLease()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;
            settings.ReliableSendQueueCapacity = 1;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(pool.Rent(PacketHeader.Size - 1)), Is.False);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered,
                        LiteNetLibLimits.MaximumReliableBytes - PacketHeader.Size + 1)), Is.False);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.UnreliableSequenced,
                        client.Endpoint.MaxUnreliablePayloadBytes - PacketHeader.Size + 1)), Is.False);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.False);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(1));

                    var first = WaitForReceive(server, client, serverEndpoint);
                    first.Dispose();
                    var second = WaitForReceive(server, client, serverEndpoint);
                    second.Dispose();
                    WaitForDeliveryAtLeast(server, client, 2);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);

                    client.Endpoint.Dispose();
                    Assert.That(client.Endpoint.TrySend(pool.Rent(PacketHeader.Size)), Is.False);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                }
            }
        }

        [Test]
        public void UnreliableReceiveOverflowDropsPacketWithoutDisconnectingPeer()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 1;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var endpoint = WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    for (var i = 0; i < 4; i++)
                    {
                        Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.UnreliableSequenced, 32)), Is.True);
                        client.Flush();
                    }

                    for (var i = 0; i < 500; i++)
                    {
                        client.Update();
                        client.Flush();
                        server.Update();
                        server.Flush();
                        if (server.CaptureDiagnostics().UnreliableReceiveDrops > 0)
                            break;
                        Thread.Sleep(1);
                    }

                    var diagnostics = server.CaptureDiagnostics();
                    Assert.That(diagnostics.UnreliableReceiveDrops, Is.GreaterThanOrEqualTo(1));
                    Assert.That(diagnostics.ReliableReceiveOverflowDisconnects, Is.Zero);
                    Assert.That(diagnostics.Connections, Is.EqualTo(1));
                    if (endpoint.TryReceive(out var queued))
                        queued.Dispose();

                }
            }
        }

        [Test]
        public void ReliableOverflowIsIsolatedToOnePeer()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 1;
            settings.MaximumConnections = 2;

            using (var server = new LiteNetLibServerHost(settings))
            using (var firstClient = new LiteNetLibClientHost(settings))
            {
                var firstEndpoint = WaitForAccept(server, firstClient);
                using (var secondClient = new LiteNetLibClientHost(settings))
                {
                    var secondEndpoint = WaitForAccept(server, secondClient);
                    using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                    {
                        Assert.That(firstClient.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        Assert.That(firstClient.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        firstClient.Flush();

                        Assert.That(secondClient.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        secondClient.Flush();

                        for (var i = 0; i < 800; i++)
                        {
                            firstClient.Update();
                            firstClient.Flush();
                            secondClient.Update();
                            secondClient.Flush();
                            server.Update();
                            server.Flush();
                            if (server.CaptureDiagnostics().ReliableReceiveOverflowDisconnects > 0)
                                break;
                            Thread.Sleep(1);
                        }

                        var isolated = WaitForReceive(server, secondClient, secondEndpoint);
                        isolated.Dispose();
                        Assert.That(server.CaptureDiagnostics().ReliableReceiveOverflowDisconnects,
                            Is.GreaterThanOrEqualTo(1));
                        Assert.That(server.CaptureDiagnostics().Connections, Is.EqualTo(1));
                        Assert.That(server.TryDequeueDisconnected(out var disconnected),
                            Is.True);
                        Assert.That(disconnected.Value, Is.EqualTo(firstEndpoint.Connection.Value));
                    }
                }
            }
        }

        [Test]
        public void DisconnectAndReconnectReclaimsQueuedReceiveLeases()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            {
                var firstConnection = default(ConnectionId);
                using (var firstClient = new LiteNetLibClientHost(settings))
                {
                    var firstEndpoint = WaitForAccept(server, firstClient);
                    firstConnection = firstEndpoint.Connection;
                    using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                    {
                        Assert.That(firstClient.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        for (var i = 0; i < 500; i++)
                        {
                            Pump(server, firstClient);
                            if (server.CaptureDiagnostics().QueuedPackets > 0)
                                break;
                            Thread.Sleep(1);
                        }
                        Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(1));
                        firstClient.Dispose();
                        firstEndpoint.Dispose();

                        for (var i = 0; i < 500; i++)
                        {
                            server.Update();
                            server.Flush();
                            if (server.CaptureDiagnostics().Connections == 0)
                                break;
                            Thread.Sleep(1);
                        }

                        Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                        Assert.That(server.CaptureDiagnostics().Connections, Is.Zero);

                    }
                }

                using (var secondClient = new LiteNetLibClientHost(settings))
                {
                    var secondEndpoint = WaitForAccept(server, secondClient);
                    Assert.That(secondEndpoint.Connection.Value, Is.GreaterThan(firstConnection.Value));
                    using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                    {
                        Assert.That(secondClient.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        var packet = WaitForReceive(server, secondClient, secondEndpoint);
                        packet.Dispose();
                        WaitForDeliveryAtLeast(server, secondClient, 1);
                        Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                        Assert.That(secondClient.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                    }
                }
            }
        }
        private static INetworkTransport WaitForNativeAccept(
            LiteNetLibServerHost server, NativePeerHarness native)
        {
            INetworkTransport accepted = null;
            for (var i = 0; i < 800; i++)
            {
                native.Pump();
                server.Update();
                server.Flush();
                if (accepted == null)
                    server.TryAccept(out accepted);
                if (accepted != null && native.IsConnected)
                    return accepted;
                Thread.Sleep(1);
            }
            Assert.Fail("Native LiteNetLib peer was not accepted.");
            return null;
        }

        private static void WaitForDeliveryAtLeast(
            LiteNetLibServerHost server, LiteNetLibClientHost client, long callbacks)
        {
            for (var i = 0; i < 800; i++)
            {
                var diagnostics = client.CaptureDiagnostics();
                if (diagnostics.NativeReliableBytes == 0 &&
                    diagnostics.DeliveryCallbacks >= callbacks)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib delivery callbacks did not drain reliable ownership.");
        }

        private static long MeasureWarmedNativePacketPoolAllocations(
            int nativePacketPoolSize, out int measuredLowWater)
        {
            const int BurstCount = 1100;
            const int PayloadBytes = 64;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = BurstCount + 64;
            settings.NativePacketPoolSize = nativePacketPoolSize;
            settings.NativeReliableFragmentsCapacity = BurstCount + 64;
            settings.NativeReliableBytesCapacity =
                (PacketHeader.Size + PayloadBytes) * (long)(BurstCount + 64);

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
            {
                var serverEndpoint = WaitForAccept(server, client);
                var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;

                SendReliableBurst(client, pool, BurstCount, PayloadBytes, null);
                DrainReliableBurst(server, client, serverEndpoint, BurstCount,
                    callbacks + BurstCount);

                var warmed = client.CaptureDiagnostics();
                Assert.That(warmed.NativePacketPoolCapacity, Is.EqualTo(nativePacketPoolSize));
                Assert.That(warmed.NativePacketPoolCount,
                    Is.InRange(Math.Min(BurstCount, nativePacketPoolSize),
                        nativePacketPoolSize));

                var measured = new NetworkBufferLease[BurstCount];
                for (var i = 0; i < measured.Length; i++)
                {
                    measured[i] = CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, PayloadBytes);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetAllocatedBytesForCurrentThread();
                var accepted = SendReliableBurst(client, pool, BurstCount, PayloadBytes, measured);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(accepted, Is.EqualTo(BurstCount));
                measuredLowWater = client.CaptureDiagnostics().NativePacketPoolLowWater;

                callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                DrainReliableBurst(server, client, serverEndpoint, BurstCount,
                    callbacks + BurstCount);
                Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                return allocated;
            }
        }

        private static int SendReliableBurst(LiteNetLibClientHost client,
            NetworkBufferPool pool, int count, int payloadBytes, NetworkBufferLease[] packets)
        {
            var accepted = 0;
            for (var i = 0; i < count; i++)
            {
                var packet = packets == null
                    ? CreatePacket(pool, PacketKind.Ping, PacketFlags.ReliableOrdered, payloadBytes)
                    : packets[i];
                if (client.Endpoint.TrySend(packet))
                    accepted++;
            }
            Assert.That(accepted, Is.EqualTo(count));
            return accepted;
        }

        private static void DrainReliableBurst(LiteNetLibServerHost server,
            LiteNetLibClientHost client, INetworkTransport endpoint, int packets, long callbacks)
        {
            var received = 0;
            for (var i = 0; i < 5000; i++)
            {
                while (endpoint.TryReceive(out var packet))
                {
                    packet.Dispose();
                    received++;
                }
                var diagnostics = client.CaptureDiagnostics();
                if (received == packets && diagnostics.NativeReliableBytes == 0 &&
                    diagnostics.DeliveryCallbacks >= callbacks)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail($"Reliable burst did not drain: received={received}/{packets}, " +
                $"callbacks={client.CaptureDiagnostics().DeliveryCallbacks}/{callbacks}.");
        }

        private sealed class NativePeerHarness : IDisposable
        {
            private readonly Native.EventBasedNetListener _listener;
            private readonly Native.NetManager _manager;
            private readonly Native.NetPeer _peer;

            public NativePeerHarness(ushort port, int maxFragmentsCount)
            {
                _listener = new Native.EventBasedNetListener();
                _listener.PeerConnectedEvent += peer => IsConnected = true;
                _listener.PeerDisconnectedEvent += (peer, info) => IsConnected = false;
                _manager = new Native.NetManager(_listener)
                {
                    AutoRecycle = true,
                    ChannelsCount = 1,
                    MtuOverride = LiteNetLibLimits.Mtu,
                    MtuDiscovery = false,
                    MaxFragmentsCount = checked((ushort)maxFragmentsCount),
                    MaxPacketPerManualReceive = 256,
                    UnsyncedEvents = false,
                    UnsyncedReceiveEvent = false,
                    UnsyncedDeliveryEvent = false
                };
                Assert.That(_manager.StartInManualMode(
                    IPAddress.Loopback, IPAddress.IPv6Any, 0), Is.True);
                _peer = _manager.Connect("127.0.0.1", port, string.Empty);
                Assert.That(_peer, Is.Not.Null);
            }

            public bool IsConnected { get; private set; }

            public void Pump()
            {
                if (_manager.IsRunning)
                {
                    _manager.PollEvents();
                    _manager.ManualUpdate(1f);
                }
            }

            public void Send(byte[] payload, Native.DeliveryMethod method)
            {
                Assert.That(_peer, Is.Not.Null);
                _peer.Send(payload, 0, method);
            }

            public void Dispose()
            {
                if (_manager.IsRunning)
                    _manager.Stop(false);
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
