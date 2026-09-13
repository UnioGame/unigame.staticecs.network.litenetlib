using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
        public void SharedAdmissionBudgetBoundsNativeFragmentsAndProtectsPool()
        {
            const int BurstCount = 1100;
            const int PayloadBytes = 32;
            var packetBytes = PacketHeader.Size + PayloadBytes;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = BurstCount + 64;
            settings.NativePacketPoolSize = 1000;
            settings.NativeReliableFragmentsCapacity = BurstCount + 64;
            settings.NativeReliableBytesCapacity = packetBytes * (long)(BurstCount + 64);
            settings.ReliableSendQueueCapacity = BurstCount + 64;
            settings.ReliableSendBytesCapacity = packetBytes * (long)(BurstCount + 64);

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                var driver = client.Driver;
                var budget = driver.NativeFragmentAdmissionBudget;
                Assert.That(budget, Is.EqualTo(750));
                Assert.That(budget, Is.LessThan(BurstCount));

                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    Assert.That(SendReliableBurst(client, pool, BurstCount, PayloadBytes, null),
                        Is.EqualTo(BurstCount));

                    var staged = client.CaptureDiagnostics();
                    Assert.That(staged.NativeReliableFragments, Is.EqualTo(budget));
                    Assert.That(staged.NativeReliableFragments,
                        Is.LessThanOrEqualTo(staged.NativePacketPoolCapacity));
                    Assert.That(staged.PendingReliablePackets, Is.EqualTo(BurstCount - budget));

                    DrainReliableBurst(server, client, serverEndpoint, BurstCount,
                        callbacks + BurstCount);

                    var complete = client.CaptureDiagnostics();
                    Assert.That(complete.NativeReliableFragments, Is.Zero);
                    Assert.That(complete.NativeReliableBytes, Is.Zero);
                    Assert.That(complete.PendingReliablePackets, Is.Zero);
                    Assert.That(complete.NativePacketPoolLowWater, Is.GreaterThan(0),
                        "The shared admission budget must leave native pool headroom.");
                    Assert.That(complete.OutstandingLeases, Is.Zero);
                }
            }
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

        [Test]
        public void RepeatedReliableSendsReachWarmTicketReuseSteadyState()
        {
            const int WarmCount = 64;
            const int PayloadBytes = 32;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = WarmCount + 8;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var driver = client.Driver;
                    var firstCallbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    var accepted = SendReliableBurst(client, pool, WarmCount, PayloadBytes, null);
                    Assert.That(accepted, Is.EqualTo(WarmCount));
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(WarmCount));
                    DrainReliableBurst(server, client, serverEndpoint, WarmCount,
                        firstCallbacks + WarmCount);
                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(WarmCount));
                    Assert.That(driver.DeliveryTicketsReused, Is.Zero);

                    var secondCallbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    SendReliableBurst(client, pool, WarmCount, PayloadBytes, null);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(WarmCount),
                        "Warm reuse must not allocate new delivery tickets.");
                    DrainReliableBurst(server, client, serverEndpoint, WarmCount,
                        secondCallbacks + WarmCount);

                    Assert.That(driver.DeliveryTicketsReused, Is.EqualTo(WarmCount));
                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(WarmCount));
                    Assert.That(driver.DeliveryTicketsRetained,
                        Is.LessThanOrEqualTo(LiteNetLibDriver.DeliveryTicketPoolCapacity));
                    Assert.That(client.CaptureDiagnostics().NativeReliableBytes, Is.Zero);
                    serverEndpoint.Dispose();
                }
            }
        }

        [Test]
        public void DeliveryTicketReuseCrossesEndpointsAndResetsAccounting()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 16;
            settings.MaximumConnections = 2;

            using (var server = new LiteNetLibServerHost(settings))
            using (var clientA = new LiteNetLibClientHost(settings))
            {
                var endpointA = WaitForAccept(server, clientA);
                using (var clientB = new LiteNetLibClientHost(settings))
                {
                    var endpointB = WaitForAcceptPair(server, clientA, clientB);
                    Assert.That(endpointA, Is.Not.SameAs(endpointB));
                    var endpointANative = (LiteNetLibEndpoint)endpointA;
                    var endpointBNative = (LiteNetLibEndpoint)endpointB;
                    var driver = server.Driver;
                    using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                    {
                        var callbacks = server.CaptureDiagnostics().DeliveryCallbacks;
                        Assert.That(endpointA.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        WaitForServerDelivery(server, clientA, callbacks + 1);
                        Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                        Assert.That(driver.DeliveryTicketsReused, Is.Zero);
                        Assert.That(endpointANative.NativeReliableFragments, Is.Zero);
                        Assert.That(endpointANative.NativeReliableBytes, Is.Zero);

                        callbacks = server.CaptureDiagnostics().DeliveryCallbacks;
                        Assert.That(endpointB.TrySend(CreatePacket(pool, PacketKind.SnapshotChunk,
                            PacketFlags.ReliableOrdered, 60000, 40)), Is.True);
                        WaitForServerDelivery(server, clientB, callbacks + 1);
                        Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1),
                            "Cross-endpoint reuse must not allocate a new ticket.");
                        Assert.That(driver.DeliveryTicketsReused, Is.GreaterThanOrEqualTo(1));
                        Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(1));
                        Assert.That(endpointANative.NativeReliableFragments, Is.Zero);
                        Assert.That(endpointANative.NativeReliableBytes, Is.Zero);
                        Assert.That(endpointBNative.NativeReliableFragments, Is.Zero);
                        Assert.That(endpointBNative.NativeReliableBytes, Is.Zero);

                        callbacks = server.CaptureDiagnostics().DeliveryCallbacks;
                        Assert.That(endpointB.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        WaitForServerDelivery(server, clientB, callbacks + 1);
                        Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                        Assert.That(driver.DeliveryTicketsReused, Is.GreaterThanOrEqualTo(2));
                        Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(1));
                        Assert.That(endpointANative.NativeReliableFragments, Is.Zero);
                        Assert.That(endpointANative.NativeReliableBytes, Is.Zero);
                        Assert.That(server.CaptureDiagnostics().NativeReliableBytes, Is.Zero);
                        Assert.That(server.CaptureDiagnostics().NativeReliableFragments, Is.Zero);
                    }
                }
            }
        }

        [Test]
        public void DuplicateDeliveryCallbackBeforeReuseDoesNotDoubleEnqueue()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);

                    driver.OnDelivery(null, ticket);
                    driver.OnDelivery(null, ticket);

                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(1),
                        "A duplicate callback must not enqueue the ticket twice.");
                    Assert.That(driver.DeliveryTicketsReused, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);
                    Assert.That(ticket.Endpoint, Is.Null,
                        "A completed ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(driver.DeliveryTicketsReused, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var reused), Is.True);
                    Assert.That(reused, Is.SameAs(ticket));
                }
            }
        }

        [Test]
        public void InjectedReliableSubmitFailureRetiresTicketWithoutRetention()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    driver.ThrowOnNextReliableSubmit = true;
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.False);

                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRented, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero,
                        "A failed submit must retire its ticket without pooling it.");
                    Assert.That(clientEndpoint.TryGetActiveTicket(out _), Is.False);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);

                    var diagnostics = client.CaptureDiagnostics();
                    Assert.That(diagnostics.SendFailures, Is.EqualTo(1));
                    Assert.That(diagnostics.NativeReliableBytes, Is.Zero);
                    Assert.That(diagnostics.OutstandingLeases, Is.Zero);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(2),
                        "A retired failed ticket must not be pooled or reused.");
                    Assert.That(driver.DeliveryTicketsReused, Is.Zero);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var active), Is.True);
                    Assert.That(active.State,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketState.Active));
                }
            }
        }

        [Test]
        public void LateDeliveryCallbackAfterDisconnectAndDisposeIsHarmless()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);

                    serverEndpoint.Dispose();
                    WaitForEndpointDisposed(server, client, clientEndpoint);

                    Assert.That(ticket.State,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketState.Retired));
                    driver.OnDelivery(null, ticket);
                    Assert.That(ticket.Endpoint, Is.Null,
                        "A retired ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(driver.ContainsRetainedDeliveryTicket(ticket), Is.False);
                }
            }

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);

                    client.Endpoint.Dispose();

                    Assert.That(ticket.State,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketState.Retired));
                    driver.OnDelivery(null, ticket);
                    Assert.That(ticket.Endpoint, Is.Null,
                        "A retired ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(driver.ContainsRetainedDeliveryTicket(ticket), Is.False);
                }
            }

            using (var server = new LiteNetLibServerHost(settings))
            {
                var client = new LiteNetLibClientHost(settings);
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);

                    client.Dispose();

                    Assert.That(ticket.State,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketState.Retired));
                    driver.OnDelivery(null, ticket);
                    Assert.That(ticket.Endpoint, Is.Null,
                        "A retired ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(driver.ContainsRetainedDeliveryTicket(ticket), Is.False);
                }
                client.Dispose();
            }
        }

        [Test]
        public void FragmentedReliableDeliveryCompletesTicketAccountingExactlyOnce()
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
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 60000, 20)), Is.True);

                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);
                    Assert.That(ticket.Fragments, Is.GreaterThan(1));
                    Assert.That(ticket.Snapshot, Is.True);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);

                    client.Update();
                    client.Flush();
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.GreaterThan(1));
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.GreaterThan(60000));

                    var received = WaitForReceive(server, client, serverEndpoint);
                    received.Dispose();
                    WaitForDeliveryAtLeast(server, client, callbacks + 1);

                    var complete = client.CaptureDiagnostics();
                    Assert.That(complete.NativeReliableFragments, Is.Zero);
                    Assert.That(complete.NativeReliableBytes, Is.Zero);
                    Assert.That(complete.DeliveryCallbacks - callbacks, Is.EqualTo(1));
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(1));
                    Assert.That(ticket.Endpoint, Is.Null,
                        "A completed ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);
                    Assert.That(ticket.CompleteByCallback(), Is.False,
                        "A completed ticket must not complete twice.");
                }
            }
        }

        [Test]
        public void ReliableBurstAbovePoolCapacityBoundsRetentionAndAcceptance()
        {
            const int BurstCount = 4200;
            const int PayloadBytes = 32;
            var packetBytes = PacketHeader.Size + PayloadBytes;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = BurstCount + 64;
            settings.NativePacketPoolSize = 8192;
            settings.NativeReliableFragmentsCapacity = BurstCount + 64;
            settings.NativeReliableBytesCapacity = packetBytes * (long)(BurstCount + 64);
            settings.ReliableSendQueueCapacity = BurstCount + 64;
            settings.ReliableSendBytesCapacity = packetBytes * (long)(BurstCount + 64);

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                var driver = client.Driver;
                Assert.That(driver.NativeFragmentAdmissionBudget,
                    Is.GreaterThanOrEqualTo(BurstCount));
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(LiteNetLibDriver.DeliveryTicketPoolCapacity, Is.EqualTo(4096));
                    var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    var accepted = SendReliableBurst(client, pool, BurstCount, PayloadBytes, null);
                    Assert.That(accepted, Is.EqualTo(BurstCount));
                    Assert.That(client.CaptureDiagnostics().SendFailures, Is.Zero);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(BurstCount));
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);

                    DrainReliableBurst(server, client, serverEndpoint, BurstCount,
                        callbacks + BurstCount);

                    Assert.That(driver.DeliveryTicketsRetained,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketPoolCapacity));
                    Assert.That(driver.DeliveryTicketsCreated,
                        Is.GreaterThan(LiteNetLibDriver.DeliveryTicketPoolCapacity));
                    Assert.That(driver.DeliveryTicketsDiscarded,
                        Is.EqualTo(BurstCount - LiteNetLibDriver.DeliveryTicketPoolCapacity));

                    var afterCallbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, PayloadBytes)), Is.True);
                    Assert.That(driver.DeliveryTicketsReused, Is.EqualTo(1));
                    Assert.That(driver.DeliveryTicketsRetained,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketPoolCapacity - 1));
                    DrainReliableBurst(server, client, serverEndpoint, 1,
                        afterCallbacks + 1);
                    Assert.That(driver.DeliveryTicketsRetained,
                        Is.EqualTo(LiteNetLibDriver.DeliveryTicketPoolCapacity));
                }
            }
        }

        [Test]
        public void DriverDisposeClearsRetainedDeliveryTickets()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 16;

            using (var server = new LiteNetLibServerHost(settings))
            {
                var client = new LiteNetLibClientHost(settings);
                var serverEndpoint = WaitForAccept(server, client);
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    SendReliableBurst(client, pool, 8, 32, null);
                    DrainReliableBurst(server, client, serverEndpoint, 8, callbacks + 8);
                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(8));

                    client.Dispose();

                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(8));
                    Assert.That(driver.DeliveryTicketsDiscarded, Is.Zero);
                }
                client.Dispose();
            }
        }

        [Test]
        public void RetainedDeliveryTicketDoesNotKeepDisconnectedEndpointAlive()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 16;

            using (var server = new LiteNetLibServerHost(settings))
            {
                var driver = server.Driver;
                var endpointRef = CreateDisconnectedServerEndpoint(server, settings,
                    out var retained);

                Assert.That(retained.Endpoint, Is.Null,
                    "The pooled ticket must not retain its disconnected endpoint.");
                Assert.That(retained.Fragments, Is.Zero);
                Assert.That(retained.Bytes, Is.Zero);
                Assert.That(retained.Snapshot, Is.False);
                Assert.That(driver.DeliveryTicketsRetained, Is.GreaterThan(0),
                    "At least one cleared ticket must remain in the pool.");
                Assert.That(driver.ContainsRetainedDeliveryTicket(retained), Is.True);

                // Unity's conservative Boehm collector cannot prove unreachability deterministically,
                // so the direct ownership-release assertions above are the Unity guarantee. The
                // collector proof is deterministic on the net8 runtime and runs there.
#if UNITY_5_3_OR_NEWER
                Assert.That(endpointRef, Is.Not.Null);
#else
                ForceGarbageCollection(endpointRef, 8);

                Assert.That(endpointRef.IsAlive, Is.False,
                    "A disconnected endpoint must be collectible while its ticket stays pooled.");
                Assert.That(driver.DeliveryTicketsRetained, Is.GreaterThan(0),
                    "Collection must not discard the pooled ticket.");
                Assert.That(driver.ContainsRetainedDeliveryTicket(retained), Is.True);
#endif
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateDisconnectedServerEndpoint(
            LiteNetLibServerHost server, LiteNetLibSettings settings,
            out LiteNetLibDriver.DeliveryTicket ticket)
        {
            using (var client = new LiteNetLibClientHost(settings))
            {
                var endpoint = (LiteNetLibEndpoint)WaitForAccept(server, client);
                using (var pool = new NetworkBufferPool(NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacks = server.CaptureDiagnostics().DeliveryCallbacks;
                    Assert.That(endpoint.TrySend(CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered, 64, 20)), Is.True);
                    Assert.That(endpoint.TryGetActiveTicket(out ticket), Is.True);

                    WaitForServerDelivery(server, client, callbacks + 1);

                    Assert.That(ticket.Endpoint, Is.Null,
                        "A completed ticket must release its endpoint.");
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);
                    Assert.That(ticket.Snapshot, Is.False);
                    Assert.That(server.Driver.ContainsRetainedDeliveryTicket(ticket), Is.True);
                }

                var reference = new WeakReference(endpoint);
                endpoint.Dispose();
                WaitForServerConnections(server, client, 0);
                endpoint = null;
                client.Dispose();
                return reference;
            }
        }

        private static void WaitForServerConnections(LiteNetLibServerHost server,
            LiteNetLibClientHost client, int expected)
        {
            for (var i = 0; i < 800; i++)
            {
                if (server.CaptureDiagnostics().Connections == expected)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("Server connections did not reach " + expected + ".");
        }

        private static void ForceGarbageCollection(WeakReference reference, int attempts)
        {
            for (var i = 0; i < attempts && reference.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
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

        [Test]
        public void DriverRoundRobinAdmissionSharesSharedBudgetAcrossThreeEndpoints()
        {
            const int PerEndpoint = 1500;
            const int PayloadBytes = 32;
            var packetBytes = PacketHeader.Size + PayloadBytes;
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 4096;
            settings.MaximumConnections = 4;
            settings.NativePacketPoolSize = 1000;
            settings.NativeReliableFragmentsCapacity = 1000;
            settings.NativeReliableBytesCapacity = packetBytes * 2000L;
            settings.ReliableSendQueueCapacity = 2048;
            settings.ReliableSendBytesCapacity = packetBytes * 2048L;

            using (var server = new LiteNetLibServerHost(settings))
            {
                using (var clientA = new LiteNetLibClientHost(settings))
                {
                    var endpointA = (LiteNetLibEndpoint)WaitForAccept(server, clientA);
                    using (var clientB = new LiteNetLibClientHost(settings))
                    {
                        var endpointB = (LiteNetLibEndpoint)WaitForAccept(server, clientB);
                        using (var clientC = new LiteNetLibClientHost(settings))
                        {
                            var endpointC = (LiteNetLibEndpoint)WaitForAccept(server, clientC);
                            var driver = server.Driver;
                            var budget = driver.NativeFragmentAdmissionBudget;
                            Assert.That(budget, Is.EqualTo(750));

                            using (var pool = new NetworkBufferPool(
                                       NetworkBufferPool.DefaultServerRetainedBytes))
                            {
                                SendReliableEndpointBurst(endpointA, pool, PerEndpoint);
                                SendReliableEndpointBurst(endpointB, pool, PerEndpoint);
                                SendReliableEndpointBurst(endpointC, pool, PerEndpoint);

                                Assert.That(endpointA.NativeReliableFragments,
                                    Is.EqualTo(budget));
                                Assert.That(endpointB.NativeReliableFragments, Is.Zero);
                                Assert.That(endpointC.NativeReliableFragments, Is.Zero);

                                while (endpointA.TryGetActiveTicket(out var ticket))
                                    ticket.Retire();

                                server.Flush();

                                Assert.That(server.CaptureDiagnostics().NativeReliableFragments,
                                    Is.EqualTo(budget));
                                Assert.That(endpointA.NativeReliableFragments,
                                    Is.EqualTo(budget / 3));
                                Assert.That(endpointB.NativeReliableFragments,
                                    Is.EqualTo(budget / 3));
                                Assert.That(endpointC.NativeReliableFragments,
                                    Is.EqualTo(budget / 3));

                                var baselineB = endpointB.NativeReliableFragments;
                                var baselineC = endpointC.NativeReliableFragments;
                                for (var cycle = 0; cycle < 3; cycle++)
                                {
                                    Assert.That(endpointA.TryGetActiveTicket(out var ticket),
                                        Is.True,
                                        "Endpoint A must keep an active ticket available for retirement.");
                                    Assert.That(ticket.Retire(), Is.True);
                                    server.Flush();
                                    Assert.That(server.CaptureDiagnostics().NativeReliableFragments,
                                        Is.EqualTo(budget),
                                        "A single freed slot must be refilled exactly once.");
                                }

                                Assert.That(endpointB.NativeReliableFragments,
                                    Is.GreaterThan(baselineB),
                                    "Service must reach B when free quota is smaller than the waiting endpoint count.");
                                Assert.That(endpointC.NativeReliableFragments,
                                    Is.GreaterThan(baselineC),
                                    "Service must reach C when free quota is smaller than the waiting endpoint count.");
                                AssertEndpointAdmissionNonNegative(endpointA);
                                AssertEndpointAdmissionNonNegative(endpointB);
                                AssertEndpointAdmissionNonNegative(endpointC);
                                AssertAdmissionCountersNonNegative(
                                    server.CaptureDiagnostics());
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void MaximumFragmentedPacketFitsSharedAdmissionBudget()
        {
            Assert.That(LiteNetLibDriver.ComputeNativeFragmentAdmissionBudget(60),
                Is.EqualTo(LiteNetLibLimits.MaximumFragmentsCount));
            Assert.That(LiteNetLibDriver.ComputeNativeFragmentAdmissionBudget(
                    LiteNetLibSettings.MinimumNativePacketPoolSize),
                Is.GreaterThanOrEqualTo(LiteNetLibLimits.MaximumFragmentsCount));

            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.ReceiveQueueCapacity = 8;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                var serverEndpoint = WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                Assert.That(driver.NativeFragmentAdmissionBudget,
                    Is.GreaterThanOrEqualTo(LiteNetLibLimits.MaximumFragmentsCount));

                using (var pool = new NetworkBufferPool(
                           NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    var callbacks = client.CaptureDiagnostics().DeliveryCallbacks;
                    var maximum = CreatePacket(pool, PacketKind.SnapshotChunk,
                        PacketFlags.ReliableOrdered,
                        LiteNetLibLimits.MaximumReliableBytes - PacketHeader.Size, 20);
                    Assert.That(client.Endpoint.TrySend(maximum), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);
                    Assert.That(ticket.Fragments,
                        Is.EqualTo(LiteNetLibLimits.MaximumFragmentsCount));
                    Assert.That(client.CaptureDiagnostics().NativeReliableFragments,
                        Is.EqualTo(LiteNetLibLimits.MaximumFragmentsCount));

                    var received = WaitForReceive(server, client, serverEndpoint);
                    received.Dispose();
                    WaitForDeliveryAtLeast(server, client, callbacks + 1);

                    var complete = client.CaptureDiagnostics();
                    Assert.That(complete.NativeReliableFragments, Is.Zero);
                    Assert.That(complete.NativeReliableBytes, Is.Zero);
                    Assert.That(complete.OutstandingLeases, Is.Zero);
                    Assert.That(driver.DeliveryTicketsCreated, Is.EqualTo(1));
                    AssertAdmissionCountersNonNegative(complete);
                }
            }
        }

        [Test]
        public void DisconnectDuringQueuedWorkReleasesAdmissionAndKeepsOrder()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;
            settings.MaximumConnections = 2;

            using (var server = new LiteNetLibServerHost(settings))
            using (var clientA = new LiteNetLibClientHost(settings))
            {
                var endpointA = (LiteNetLibEndpoint)WaitForAccept(server, clientA);
                using (var clientB = new LiteNetLibClientHost(settings))
                {
                    var endpointB = (LiteNetLibEndpoint)WaitForAccept(server, clientB);
                    using (var pool = new NetworkBufferPool(
                               NetworkBufferPool.DefaultServerRetainedBytes))
                    {
                        Assert.That(endpointA.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        Assert.That(endpointA.TrySend(CreatePacket(pool, PacketKind.Pong,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        Assert.That(endpointA.PendingReliablePackets, Is.EqualTo(1));
                        Assert.That(endpointA.NativeReliableFragments, Is.EqualTo(1));

                        endpointA.Dispose();

                        Assert.That(endpointA.IsDisposed, Is.True);
                        Assert.That(endpointA.PendingReliablePackets, Is.Zero);
                        Assert.That(endpointA.PendingReliableBytes, Is.Zero);
                        Assert.That(endpointA.NativeReliableFragments, Is.Zero);
                        Assert.That(endpointA.NativeReliableBytes, Is.Zero);
                        Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                        AssertAdmissionCountersNonNegative(server.CaptureDiagnostics());

                        var callbacks = server.CaptureDiagnostics().DeliveryCallbacks;
                        Assert.That(endpointB.TrySend(CreatePacket(pool, PacketKind.Ping,
                            PacketFlags.ReliableOrdered, 32)), Is.True);
                        var received = WaitForReceive(server, clientB, clientB.Endpoint);
                        received.Dispose();
                        WaitForServerDelivery(server, clientB, callbacks + 1);
                        AssertAdmissionCountersNonNegative(server.CaptureDiagnostics());
                    }
                }
            }
        }

        [Test]
        public void FailedQueuedSubmitContinuesDrainWithoutNegativeCounters()
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
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(
                           NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.PendingReliablePackets, Is.EqualTo(1));

                    driver.ThrowOnNextReliableSubmit = true;
                    WaitForDeliveryAtLeast(server, client, 1);

                    var diagnostics = client.CaptureDiagnostics();
                    Assert.That(diagnostics.SendFailures, Is.EqualTo(1));
                    Assert.That(clientEndpoint.PendingReliablePackets, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);
                    Assert.That(diagnostics.NativeReliableFragments, Is.Zero);
                    Assert.That(diagnostics.OutstandingLeases, Is.Zero);
                    AssertAdmissionCountersNonNegative(diagnostics);

                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    var received = WaitForReceive(server, client, serverEndpoint);
                    received.Dispose();
                    WaitForDeliveryAtLeast(server, client, 2);
                    AssertAdmissionCountersNonNegative(client.CaptureDiagnostics());
                }
            }
        }

        [Test]
        public void DuplicateAndStaleDeliveryCallbacksKeepAdmissionNonNegative()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;

            using (var server = new LiteNetLibServerHost(settings))
            using (var client = new LiteNetLibClientHost(settings))
            {
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(
                           NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.TryGetActiveTicket(out var ticket), Is.True);

                    driver.OnDelivery(null, ticket);
                    driver.OnDelivery(null, ticket);

                    Assert.That(driver.DeliveryTicketsRetained, Is.EqualTo(1));
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(ticket.Endpoint, Is.Null);
                    Assert.That(ticket.Fragments, Is.Zero);
                    Assert.That(ticket.Bytes, Is.Zero);

                    client.Endpoint.Dispose();
                    driver.OnDelivery(null, ticket);

                    Assert.That(ticket.Endpoint, Is.Null);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);
                    AssertAdmissionCountersNonNegative(client.CaptureDiagnostics());
                }
            }
        }

        [Test]
        public void DriverDisposeWithQueuedWorkClearsAdmissionAndOrder()
        {
            var port = FindFreePort();
            var settings = LiteNetLibSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            settings.NativeReliableFragmentsCapacity = 1;

            var client = new LiteNetLibClientHost(settings);
            using (var server = new LiteNetLibServerHost(settings))
            {
                WaitForAccept(server, client);
                var clientEndpoint = (LiteNetLibEndpoint)client.Endpoint;
                var driver = client.Driver;
                using (var pool = new NetworkBufferPool(
                           NetworkBufferPool.DefaultClientRetainedBytes))
                {
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(client.Endpoint.TrySend(CreatePacket(pool, PacketKind.Pong,
                        PacketFlags.ReliableOrdered, 32)), Is.True);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.EqualTo(1));
                    Assert.That(clientEndpoint.PendingReliablePackets, Is.EqualTo(1));

                    client.Dispose();

                    Assert.That(clientEndpoint.PendingReliablePackets, Is.Zero);
                    Assert.That(clientEndpoint.PendingReliableBytes, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableFragments, Is.Zero);
                    Assert.That(clientEndpoint.NativeReliableBytes, Is.Zero);
                    Assert.That(pool.CaptureDiagnostics().OutstandingLeases, Is.Zero);
                    Assert.That(driver.DeliveryTicketsRetained, Is.Zero);
                    Assert.That(driver.NativeFragmentAdmissionBudget, Is.EqualTo(750));
                }
                client.Dispose();
            }
        }

        private static void SendReliableEndpointBurst(LiteNetLibEndpoint endpoint,
            NetworkBufferPool pool, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.That(endpoint.TrySend(CreatePacket(pool, PacketKind.Ping,
                    PacketFlags.ReliableOrdered, 32)), Is.True);
            }
        }

        private static void AssertAdmissionCountersNonNegative(
            LiteNetLibDiagnostics diagnostics)
        {
            Assert.That(diagnostics.NativeReliableFragments, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.NativeReliableBytes, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.PendingReliablePackets, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.PendingReliableBytes, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.QueuedPackets, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.OutstandingLeases, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.SendFailures, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.DroppedPackets, Is.GreaterThanOrEqualTo(0));
            Assert.That(diagnostics.Connections, Is.GreaterThanOrEqualTo(0));
        }

        private static void AssertEndpointAdmissionNonNegative(LiteNetLibEndpoint endpoint)
        {
            Assert.That(endpoint.NativeReliableFragments, Is.GreaterThanOrEqualTo(0));
            Assert.That(endpoint.NativeReliableBytes, Is.GreaterThanOrEqualTo(0));
            Assert.That(endpoint.PendingReliablePackets, Is.GreaterThanOrEqualTo(0));
            Assert.That(endpoint.PendingReliableBytes, Is.GreaterThanOrEqualTo(0));
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

        private static INetworkTransport WaitForAcceptPair(LiteNetLibServerHost server,
            LiteNetLibClientHost first, LiteNetLibClientHost second)
        {
            INetworkTransport accepted = null;
            for (var i = 0; i < 400; i++)
            {
                Pump(server, first);
                second.Update();
                second.Flush();
                if (accepted == null)
                    server.TryAccept(out accepted);
                if (accepted != null && first.Connected && second.Connected)
                    return accepted;
                Thread.Sleep(1);
            }
            Assert.Fail("The second LiteNetLib loopback connection was not accepted.");
            return null;
        }

        private static void WaitForServerDelivery(LiteNetLibServerHost server,
            LiteNetLibClientHost client, long callbacks)
        {
            for (var i = 0; i < 800; i++)
            {
                var diagnostics = server.CaptureDiagnostics();
                if (diagnostics.NativeReliableBytes == 0 &&
                    diagnostics.DeliveryCallbacks >= callbacks)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib server delivery callbacks did not drain reliable ownership.");
        }

        private static void WaitForEndpointDisposed(LiteNetLibServerHost server,
            LiteNetLibClientHost client, LiteNetLibEndpoint endpoint)
        {
            for (var i = 0; i < 800; i++)
            {
                if (endpoint.IsDisposed)
                    return;
                Pump(server, client);
                Thread.Sleep(1);
            }
            Assert.Fail("LiteNetLib endpoint was not disposed after disconnect.");
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
