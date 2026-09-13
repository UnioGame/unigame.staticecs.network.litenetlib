using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using UniGame.StaticEcs.Network;
using global::LiteNetLib;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("unigame.staticecs.network.litenetlib.tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Adapter.Tests")]

namespace UniGame.StaticEcs.Network.LiteNetLib
{
    /// <summary>Defines the fixed LiteNetLib packet and fragmentation limits.</summary>
    public static class LiteNetLibLimits
    {
        /// <summary>Default UDP port used by the transport hosts.</summary>
        public const ushort DefaultPort = 7777;
        /// <summary>MTU forced on every LiteNetLib peer.</summary>
        public const int Mtu = 1200;
        /// <summary>Native header bytes for one channeled packet.</summary>
        public const int ChanneledHeaderSize = 4;
        /// <summary>Native header bytes added to one fragment.</summary>
        public const int FragmentHeaderSize = 6;
        /// <summary>Native overhead used by a fragmented channeled packet.</summary>
        public const int FragmentationHeaderSize = ChanneledHeaderSize + FragmentHeaderSize;
        /// <summary>Maximum complete reliable packet bytes, including the protocol header.</summary>
        public const int MaximumReliableBytes = 64 * 1024;
        /// <summary>Maximum complete packet bytes accepted without native fragmentation.</summary>
        public const int MaximumSequencedBytes = Mtu - ChanneledHeaderSize;
        /// <summary>Maximum bytes carried by one reliable native fragment.</summary>
        public const int ReliableFragmentPayloadBytes = Mtu - FragmentationHeaderSize;
        /// <summary>Maximum fragments needed for one complete reliable packet.</summary>
        public const int MaximumFragmentsCount =
            (MaximumReliableBytes + ReliableFragmentPayloadBytes - 1) / ReliableFragmentPayloadBytes;

        /// <summary>Alias for the native single-packet capability.</summary>
        public const int MaximumUnreliableBytes = MaximumSequencedBytes;
    }

    /// <summary>Configures one bounded LiteNetLib host and its exact-packet endpoints.</summary>
    [Serializable]
    public struct LiteNetLibSettings
    {
        /// <summary>Address used by a client or listener.</summary>
        public string Address;
        /// <summary>Remote client port or listener port.</summary>
        public ushort Port;
        /// <summary>Maximum complete sequenced packet bytes, including <see cref="PacketHeader"/>.</summary>
        public int MaximumUnreliableBytes;
        /// <summary>Maximum queued received packets per connection.</summary>
        public int ReceiveQueueCapacity;
        /// <summary>Maximum accepted server connections.</summary>
        public int MaximumConnections;
        /// <summary>Maximum reliable packets held by the adapter FIFO per connection.</summary>
        public int ReliableSendQueueCapacity;
        /// <summary>Maximum reliable bytes held by the adapter FIFO per connection.</summary>
        public long ReliableSendBytesCapacity;
        /// <summary>Maximum native reliable fragments accounted as queued or in flight per connection.</summary>
        public int NativeReliableFragmentsCapacity;
        /// <summary>Maximum native reliable bytes accounted as queued or in flight per connection.</summary>
        public long NativeReliableBytesCapacity;
        /// <summary>Native LiteNetLib packet pool size; zero selects a role-based default.</summary>
        public int NativePacketPoolSize;

        /// <summary>Gets conservative defaults for one separated endpoint.</summary>
        public static LiteNetLibSettings Default => new LiteNetLibSettings
        {
            Address = "127.0.0.1",
            Port = LiteNetLibLimits.DefaultPort,
            MaximumUnreliableBytes = LiteNetLibLimits.MaximumSequencedBytes,
            ReceiveQueueCapacity = 256,
            MaximumConnections = 128,
            ReliableSendQueueCapacity = 64,
            ReliableSendBytesCapacity = LiteNetLibLimits.MaximumReliableBytes * 4L,
            NativeReliableFragmentsCapacity = LiteNetLibLimits.MaximumFragmentsCount * NetConstants.DefaultWindowSize,
            NativeReliableBytesCapacity = LiteNetLibLimits.MaximumReliableBytes * (long)NetConstants.DefaultWindowSize,
        };

        /// <summary>Maximum complete reliable packet bytes supported by this adapter.</summary>
        public const int MaximumReliableBytes = LiteNetLibLimits.MaximumReliableBytes;
        /// <summary>Smallest native packet pool size this adapter is allowed to configure.</summary>
        public const int MinimumNativePacketPoolSize = 1000;
        /// <summary>Largest native packet pool size this adapter is allowed to configure.</summary>
        public const int MaximumNativePacketPoolSize = 32768;

        /// <summary>Validates and fills optional zero values.</summary>
        public LiteNetLibSettings Normalize(bool listener)
        {
            var value = this;
            if (string.IsNullOrWhiteSpace(value.Address))
                value.Address = listener ? "0.0.0.0" : "127.0.0.1";
            if (value.Port == 0)
                value.Port = LiteNetLibLimits.DefaultPort;
            if (value.MaximumUnreliableBytes <= PacketHeader.Size ||
                value.MaximumUnreliableBytes > LiteNetLibLimits.MaximumSequencedBytes)
                value.MaximumUnreliableBytes = LiteNetLibLimits.MaximumSequencedBytes;
            if (value.ReceiveQueueCapacity <= 0)
                value.ReceiveQueueCapacity = 256;
            if (value.MaximumConnections <= 0)
                value.MaximumConnections = 128;
            var nativePoolSize = value.NativePacketPoolSize;
            if (nativePoolSize <= 0)
            {
                nativePoolSize = listener
                    ? (int)Math.Min(int.MaxValue,
                        value.MaximumConnections * (long)NetConstants.DefaultWindowSize)
                    : MinimumNativePacketPoolSize;
            }
            if (nativePoolSize < MinimumNativePacketPoolSize)
                nativePoolSize = MinimumNativePacketPoolSize;
            if (nativePoolSize > MaximumNativePacketPoolSize)
                nativePoolSize = MaximumNativePacketPoolSize;
            value.NativePacketPoolSize = nativePoolSize;
            if (value.ReliableSendQueueCapacity <= 0)
                value.ReliableSendQueueCapacity = 64;
            if (value.ReliableSendBytesCapacity <= 0)
                value.ReliableSendBytesCapacity = LiteNetLibLimits.MaximumReliableBytes * 4L;
            if (value.NativeReliableFragmentsCapacity <= 0)
                value.NativeReliableFragmentsCapacity = LiteNetLibLimits.MaximumFragmentsCount * NetConstants.DefaultWindowSize;
            if (value.NativeReliableBytesCapacity <= 0)
                value.NativeReliableBytesCapacity = LiteNetLibLimits.MaximumReliableBytes * (long)NetConstants.DefaultWindowSize;
            return value;
        }
    }

    /// <summary>Captures adapter counters and cumulative LiteNetLib native datagram statistics.</summary>
    public struct LiteNetLibDiagnostics
    {
        /// <summary>Number of active endpoint objects.</summary>
        public int Connections;
        /// <summary>Number of complete packets accepted from LiteNetLib.</summary>
        public long ReceivedPackets;
        /// <summary>Number of reliable packets accepted from LiteNetLib.</summary>
        public long ReliableReceivedPackets;
        /// <summary>Number of reliable bytes accepted from LiteNetLib.</summary>
        public long ReliableReceivedBytes;
        /// <summary>Number of sequenced packets accepted from LiteNetLib.</summary>
        public long UnreliableReceivedPackets;
        /// <summary>Number of sequenced bytes accepted from LiteNetLib.</summary>
        public long UnreliableReceivedBytes;
        /// <summary>Number of complete packets submitted to LiteNetLib.</summary>
        public long SentPackets;
        /// <summary>Number of reliable packets submitted to LiteNetLib.</summary>
        public long ReliableSentPackets;
        /// <summary>Number of reliable bytes submitted to LiteNetLib.</summary>
        public long ReliableSentBytes;
        /// <summary>Number of sequenced packets submitted to LiteNetLib.</summary>
        public long UnreliableSentPackets;
        /// <summary>Number of sequenced bytes submitted to LiteNetLib.</summary>
        public long UnreliableSentBytes;
        /// <summary>Number of packets rejected by adapter limits or native errors.</summary>
        public long DroppedPackets;
        /// <summary>Number of packets rejected because the bounded receive queue was full.</summary>
        public long ReceiveQueueOverflows;
        /// <summary>Number of packets rejected because framing or channel validation failed.</summary>
        public long MalformedPackets;
        /// <summary>Number of packets rejected while attempting to send.</summary>
        public long SendFailures;
        /// <summary>Number of observed transport disconnects.</summary>
        public long Disconnects;
        /// <summary>Number of reliable packets currently held by adapter FIFOs.</summary>
        public int PendingReliablePackets;
        /// <summary>Number of reliable bytes currently held by adapter FIFOs.</summary>
        public long PendingReliableBytes;
        /// <summary>Highest observed adapter FIFO packet count.</summary>
        public int PendingReliablePacketsHighWater;
        /// <summary>Highest observed adapter FIFO byte count.</summary>
        public long PendingReliableBytesHighWater;
        /// <summary>Number of reliable packets rejected because an adapter FIFO was full.</summary>
        public long ReliableSendQueueOverflows;
        /// <summary>Number of complete packets currently queued for receive.</summary>
        public int QueuedPackets;
        /// <summary>Number of receive leases currently owned outside the transport pool.</summary>
        public int OutstandingLeases;
        /// <summary>Native reliable fragments accounted as queued or in flight by delivery tickets.</summary>
        public int NativeReliableFragments;
        /// <summary>Native reliable bytes accounted as queued or in flight by delivery tickets.</summary>
        public long NativeReliableBytes;
        /// <summary>Highest native fragment count accounted by delivery tickets.</summary>
        public int NativeReliableFragmentsHighWater;
        /// <summary>Highest native byte count accounted by delivery tickets.</summary>
        public long NativeReliableBytesHighWater;
        /// <summary>Native queue-only reliable packet count; this excludes in-flight window ownership.</summary>
        public int NativeReliableQueuePackets;
        /// <summary>Cumulative native UDP datagrams sent during the host lifetime.</summary>
        public long NativeSentPackets;
        /// <summary>Cumulative native UDP datagrams received during the host lifetime.</summary>
        public long NativeReceivedPackets;
        /// <summary>Cumulative native UDP datagram payload bytes sent during the host lifetime.</summary>
        public long NativeSentBytes;
        /// <summary>Cumulative native UDP datagram payload bytes received during the host lifetime.</summary>
        public long NativeReceivedBytes;
        /// <summary>Cumulative LiteNetLib detected or retransmit packet loss during the host lifetime.</summary>
        public long NativePacketLoss;
        /// <summary>Number of reliable delivery callbacks observed.</summary>
        public long DeliveryCallbacks;
        /// <summary>Number of reliable receive overflows that requested peer disconnect.</summary>
        public long ReliableReceiveOverflowDisconnects;
        /// <summary>Number of sequenced packets dropped because a receive queue was full.</summary>
        public long UnreliableReceiveDrops;
        /// <summary>Current number of packets held by the native LiteNetLib packet pool.</summary>
        public int NativePacketPoolCount;
        /// <summary>Configured native LiteNetLib packet pool capacity.</summary>
        public int NativePacketPoolCapacity;
        /// <summary>Lowest observed native pool count, or -1 until the pool is first seen with packets.</summary>
        public int NativePacketPoolLowWater;
    }

    /// <summary>Owns one manual-pump LiteNetLib client and exact-packet endpoint.</summary>
    public sealed class LiteNetLibClientHost : IDisposable
    {
        private readonly LiteNetLibDriver _driver;

        /// <summary>Creates and starts a client host.</summary>
        public LiteNetLibClientHost(LiteNetLibSettings settings)
        {
            _driver = new LiteNetLibDriver(settings.Normalize(false), false);
            Endpoint = _driver.ClientEndpoint;
        }

        /// <summary>Gets the protocol-facing client endpoint.</summary>
        public INetworkTransport Endpoint { get; }
        /// <summary>Gets whether the native peer is connected.</summary>
        public bool Connected => _driver.Connected;
        /// <summary>Receives native events and complete packets.</summary>
        public void Update() => _driver.Update();
        /// <summary>Advances native reliable sends after protocol sends.</summary>
        public void Flush() => _driver.Flush();
        /// <summary>Captures adapter and pool counters.</summary>
        public LiteNetLibDiagnostics CaptureDiagnostics() => _driver.CaptureDiagnostics();
        internal LiteNetLibDriver Driver => _driver;
        /// <inheritdoc />
        public void Dispose() => _driver.Dispose();
    }

    /// <summary>Owns one manual-pump LiteNetLib listener and accepted endpoints.</summary>
    public sealed class LiteNetLibServerHost : IDisposable
    {
        private readonly LiteNetLibDriver _driver;

        /// <summary>Creates and starts a listening host.</summary>
        public LiteNetLibServerHost(LiteNetLibSettings settings)
        {
            _driver = new LiteNetLibDriver(settings.Normalize(true), true);
        }

        /// <summary>Receives accepts, disconnects, and complete packets.</summary>
        public void Update() => _driver.Update();
        /// <summary>Returns the next newly accepted endpoint.</summary>
        public bool TryAccept(out INetworkTransport endpoint) => _driver.TryAccept(out endpoint);
        /// <summary>Returns the next disconnected connection identity.</summary>
        public bool TryDequeueDisconnected(out ConnectionId connection) =>
            _driver.TryDequeueDisconnected(out connection);
        /// <summary>Advances native reliable sends after protocol sends.</summary>
        public void Flush() => _driver.Flush();
        /// <summary>Captures adapter and pool counters.</summary>
        public LiteNetLibDiagnostics CaptureDiagnostics() => _driver.CaptureDiagnostics();
        internal LiteNetLibDriver Driver => _driver;
        /// <inheritdoc />
        public void Dispose() => _driver.Dispose();
    }

    internal sealed class LiteNetLibDriver : IDisposable
    {
        private readonly LiteNetLibSettings _settings;
        private readonly bool _listener;
        private readonly NetworkBufferPool _pool;
        private readonly NetManager _manager;
        private readonly Dictionary<NetPeer, LiteNetLibEndpoint> _endpoints =
            new Dictionary<NetPeer, LiteNetLibEndpoint>();
        private readonly List<LiteNetLibEndpoint> _drainOrder;
        private readonly Queue<LiteNetLibEndpoint> _accepted = new Queue<LiteNetLibEndpoint>();
        private readonly Queue<ConnectionId> _disconnected = new Queue<ConnectionId>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastPumpTicks;
        private LiteNetLibEndpoint _clientEndpoint;
        private uint _nextConnection;
        private bool _disposed;
        private long _received;
        private long _reliableReceivedPackets;
        private long _reliableReceivedBytes;
        private long _unreliableReceivedPackets;
        private long _unreliableReceivedBytes;
        private long _sent;
        private long _reliableSentPackets;
        private long _reliableSentBytes;
        private long _unreliableSentPackets;
        private long _unreliableSentBytes;
        private long _dropped;
        private long _receiveQueueOverflows;
        private long _malformedPackets;
        private long _sendFailures;
        private long _disconnects;
        private long _reliableSendQueueOverflows;
        private long _deliveryCallbacks;
        private long _reliableReceiveOverflowDisconnects;
        private long _unreliableReceiveDrops;
        private int _pendingReliablePacketsHighWater;
        private long _pendingReliableBytesHighWater;
        private int _nativeReliableFragments;
        private long _nativeReliableBytes;
        private int _nativeReliableFragmentsHighWater;
        private long _nativeReliableBytesHighWater;
        private int _nativePacketPoolLowWater = -1;
        private bool _nativePacketPoolObserved;
        private readonly int _nativeFragmentAdmissionBudget;
        private int _drainCursor;
        private readonly Queue<DeliveryTicket> _deliveryTickets = new Queue<DeliveryTicket>();
        private int _deliveryTicketsCreated;
        private int _deliveryTicketsRented;
        private int _deliveryTicketsReused;
        private int _deliveryTicketsDiscarded;

        internal const int DeliveryTicketPoolCapacity = 4096;

        internal int DeliveryTicketsCreated => _deliveryTicketsCreated;
        internal int DeliveryTicketsRented => _deliveryTicketsRented;
        internal int DeliveryTicketsReused => _deliveryTicketsReused;
        internal int DeliveryTicketsDiscarded => _deliveryTicketsDiscarded;
        internal int DeliveryTicketsRetained => _deliveryTickets.Count;

        internal int NativeFragmentAdmissionBudget => _nativeFragmentAdmissionBudget;

        /// <summary>Computes the driver-wide native fragment admission budget for a packet pool.</summary>
        internal static int ComputeNativeFragmentAdmissionBudget(int nativePacketPoolSize)
        {
            if (nativePacketPoolSize <= 0)
                return LiteNetLibLimits.MaximumFragmentsCount;
            var scaled = (int)Math.Min(int.MaxValue, nativePacketPoolSize * 3L / 4L);
            return Math.Min(nativePacketPoolSize,
                Math.Max(LiteNetLibLimits.MaximumFragmentsCount, scaled));
        }

        internal bool ThrowOnNextReliableSubmit { get; set; }

        internal bool ContainsRetainedDeliveryTicket(DeliveryTicket ticket) =>
            _deliveryTickets.Contains(ticket);

        internal LiteNetLibDriver(LiteNetLibSettings settings, bool listener)
        {
            _settings = settings;
            _listener = listener;
            _nativeFragmentAdmissionBudget =
                ComputeNativeFragmentAdmissionBudget(settings.NativePacketPoolSize);
            var drainCapacity = settings.MaximumConnections <= 0
                ? 1
                : (int)Math.Min((long)settings.MaximumConnections + 1L, 4096L);
            _drainOrder = new List<LiteNetLibEndpoint>(drainCapacity);
            _pool = new NetworkBufferPool(listener
                ? NetworkBufferPool.DefaultServerRetainedBytes
                : NetworkBufferPool.DefaultClientRetainedBytes);
            var nativeListener = new Listener(this);
            _manager = new NetManager(nativeListener)
            {
                EnableStatistics = true,
                AutoRecycle = true,
                ChannelsCount = 1,
                MtuOverride = LiteNetLibLimits.Mtu,
                MtuDiscovery = false,
                MaxFragmentsCount = LiteNetLibLimits.MaximumFragmentsCount,
                MaxPacketPerManualReceive = Math.Max(1, settings.ReceiveQueueCapacity),
                PacketPoolSize = settings.NativePacketPoolSize,
                UnsyncedEvents = false,
                UnsyncedReceiveEvent = false,
                UnsyncedDeliveryEvent = false,
            };
            try
            {
                var ipv4 = IPAddress.Any;
                var ipv6 = IPAddress.IPv6Any;
                var port = 0;
                if (listener)
                {
                    var address = ParseAddress(settings.Address, true);
                    ipv4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        ? address : IPAddress.Any;
                    ipv6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? address : IPAddress.IPv6Any;
                    port = settings.Port;
                }
                if (!_manager.StartInManualMode(ipv4, ipv6, port))
                    throw new InvalidOperationException("Unable to start LiteNetLib manual mode.");
                if (!listener)
                {
                    var peer = _manager.Connect(settings.Address, settings.Port, string.Empty);
                    if (peer == null)
                        throw new InvalidOperationException("Unable to create a LiteNetLib connection.");
                    _clientEndpoint = CreateEndpoint(peer, new ConnectionId(1));
                    _endpoints.Add(peer, _clientEndpoint);
                    _drainOrder.Add(_clientEndpoint);
                }
                _lastPumpTicks = _clock.ElapsedTicks;
            }
            catch
            {
                _manager.Stop(false);
                _pool.Dispose();
                throw;
            }
        }

        internal INetworkTransport ClientEndpoint => _clientEndpoint;
        internal bool Connected => _clientEndpoint != null && _clientEndpoint.IsConnected;
        internal LiteNetLibSettings Settings => _settings;

        internal void Update()
        {
            ThrowIfDisposed();
            using var nativeScope = NetworkDiagnosticMarkers.Measure(NetworkDiagnosticPhase.NativeUpdate);
            _manager.PollEvents();
            ObserveNativePacketPool();
        }

        internal void Flush()
        {
            ThrowIfDisposed();
            using var drainScope = NetworkDiagnosticMarkers.Measure(NetworkDiagnosticPhase.ReliableDrain);
            DrainReliableEndpoints();
            var now = _clock.ElapsedTicks;
            var elapsed = (float)((now - _lastPumpTicks) * 1000.0 / Stopwatch.Frequency);
            _lastPumpTicks = now;
            if (elapsed <= 0)
                elapsed = 0.001f;
            using var nativeScope = NetworkDiagnosticMarkers.Measure(NetworkDiagnosticPhase.NativeUpdate);
            _manager.ManualUpdate(elapsed);
            ObserveNativePacketPool();
        }

        internal bool TryAccept(out INetworkTransport endpoint)
        {
            while (_accepted.Count > 0)
            {
                var next = _accepted.Dequeue();
                if (!next.IsDisposed && next.IsConnected)
                {
                    endpoint = next;
                    return true;
                }
            }
            endpoint = null;
            return false;
        }

        internal bool TryDequeueDisconnected(out ConnectionId connection)
        {
            if (_disconnected.Count > 0)
            {
                connection = _disconnected.Dequeue();
                return true;
            }
            connection = default;
            return false;
        }

        internal bool TrySend(LiteNetLibEndpoint endpoint, NetworkBufferLease packet)
        {
            if (packet == null)
                return false;
            try
            {
                if (_disposed || endpoint.IsDisposed || !endpoint.IsConnected ||
                    packet.Length < PacketHeader.Size ||
                    !NetworkPacket.TryDecode(packet, out var header, out _))
                {
                    RejectSend();
                    return false;
                }

                var reliable = header.Flags == PacketFlags.ReliableOrdered;
                var sequenced = header.Flags == PacketFlags.UnreliableSequenced;
                if (!reliable && !sequenced)
                {
                    RejectMalformedSend();
                    return false;
                }
                if (reliable)
                {
                    var accepted = TrySendReliable(endpoint, packet, header, out var transferred);
                    if (transferred)
                        packet = null;
                    return accepted;
                }
                if (packet.Length > endpoint.MaxUnreliablePayloadBytes)
                {
                    RejectSend();
                    return false;
                }
                try
                {
                    endpoint.Peer.Send(packet.Span, DeliveryMethod.Sequenced);
                    _sent++;
                    _unreliableSentPackets++;
                    _unreliableSentBytes += packet.Length;
                    return true;
                }
                catch
                {
                    RejectSend();
                    return false;
                }
            }
            finally
            {
                packet?.Dispose();
            }
        }

        private bool TrySendReliable(LiteNetLibEndpoint endpoint,
            NetworkBufferLease packet, PacketHeader header, out bool transferred)
        {
            transferred = false;
            if (packet.Length > LiteNetLibLimits.MaximumReliableBytes)
            {
                RejectSend();
                return false;
            }
            var fragments = FragmentCount(packet.Length);
            if (fragments > LiteNetLibLimits.MaximumFragmentsCount)
            {
                RejectSend();
                return false;
            }
            if (!endpoint.ValidateSnapshotTick(header))
                return endpoint.SnapshotTickIsNewer(header) ? DropAccepted(packet) : false;
            if (endpoint.PendingReliablePackets > 0)
            {
                var queued = endpoint.TryQueue(packet, header, fragments);
                transferred = queued;
                return queued;
            }
            if (!CanRegisterNative(endpoint, fragments, packet.Length))
            {
                var queued = endpoint.TryQueue(packet, header, fragments);
                transferred = queued;
                return queued;
            }
            var submitted = SubmitReliable(endpoint, packet, header, fragments, false);
            transferred = true;
            return submitted;
        }

        private bool SubmitReliable(LiteNetLibEndpoint endpoint,
            NetworkBufferLease packet, PacketHeader header, int fragments, bool alreadyPending)
        {
            DeliveryTicket ticket = null;
            if (!alreadyPending)
                endpoint.TrackSnapshot(header);
            try
            {
                ticket = RentDeliveryTicket(endpoint, fragments, packet.Length,
                    header.Kind == PacketKind.SnapshotChunk);
                endpoint.TrackTicket(ticket);
                RegisterNative(ticket);
                if (ThrowOnNextReliableSubmit)
                {
                    ThrowOnNextReliableSubmit = false;
                    throw new InvalidOperationException("Injected reliable submit failure.");
                }
                endpoint.Peer.SendWithDeliveryEvent(packet.Span, DeliveryMethod.ReliableOrdered, ticket);
                ObserveNativePacketPool();
                _sent++;
                _reliableSentPackets++;
                _reliableSentBytes += packet.Length;
                return true;
            }
            catch
            {
                ticket?.Retire();
                RejectSend();
                return false;
            }
            finally
            {
                packet.Dispose();
            }
        }

        private DeliveryTicket RentDeliveryTicket(LiteNetLibEndpoint endpoint,
            int fragments, int bytes, bool snapshot)
        {
            DeliveryTicket ticket;
            if (_deliveryTickets.Count > 0)
            {
                ticket = _deliveryTickets.Dequeue();
                _deliveryTicketsReused++;
            }
            else
            {
                ticket = new DeliveryTicket(this);
                _deliveryTicketsCreated++;
            }
            _deliveryTicketsRented++;
            ticket.Reset(endpoint, fragments, bytes, snapshot);
            return ticket;
        }

        internal void ReturnDeliveryTicket(DeliveryTicket ticket)
        {
            if (_disposed || _deliveryTickets.Count >= DeliveryTicketPoolCapacity)
            {
                _deliveryTicketsDiscarded++;
                return;
            }
            _deliveryTickets.Enqueue(ticket);
        }

        private static int FragmentCount(int bytes) =>
            (bytes + LiteNetLibLimits.ReliableFragmentPayloadBytes - 1) /
            LiteNetLibLimits.ReliableFragmentPayloadBytes;

        private void DrainReliableEndpoints()
        {
            var count = _drainOrder.Count;
            while (count > 0)
            {
                var progress = false;
                for (var visit = 0; visit < count; visit++)
                {
                    if (_drainOrder.Count == 0)
                        break;
                    if (_drainCursor >= _drainOrder.Count)
                        _drainCursor = 0;
                    var endpoint = _drainOrder[_drainCursor];
                    _drainCursor++;
                    if (endpoint.DrainOneReliable())
                        progress = true;
                }
                if (!progress)
                    break;
                count = _drainOrder.Count;
            }
            if (_drainOrder.Count == 0)
                _drainCursor = 0;
        }

        private void RemoveFromDrainOrder(LiteNetLibEndpoint endpoint)
        {
            for (var i = 0; i < _drainOrder.Count; i++)
            {
                if (!ReferenceEquals(_drainOrder[i], endpoint))
                    continue;
                _drainOrder.RemoveAt(i);
                if (i < _drainCursor)
                    _drainCursor--;
                if (_drainCursor < 0)
                    _drainCursor = 0;
                if (_drainCursor >= _drainOrder.Count)
                    _drainCursor = 0;
                return;
            }
        }

        internal bool CanRegisterNative(LiteNetLibEndpoint endpoint, int fragments, int bytes) =>
            _nativeReliableFragments <= _nativeFragmentAdmissionBudget - fragments &&
            endpoint.NativeReliableFragments <= _settings.NativeReliableFragmentsCapacity - fragments &&
            endpoint.NativeReliableBytes <= _settings.NativeReliableBytesCapacity - bytes;

        internal void RegisterNative(DeliveryTicket ticket)
        {
            ticket.Endpoint.RegisterNative(ticket);
            _nativeReliableFragments = checked(_nativeReliableFragments + ticket.Fragments);
            _nativeReliableBytes = checked(_nativeReliableBytes + ticket.Bytes);
            if (_nativeReliableFragments > _nativeReliableFragmentsHighWater)
                _nativeReliableFragmentsHighWater = _nativeReliableFragments;
            if (_nativeReliableBytes > _nativeReliableBytesHighWater)
                _nativeReliableBytesHighWater = _nativeReliableBytes;
        }

        internal void ReleaseNative(DeliveryTicket ticket)
        {
            ticket.Endpoint.ReleaseNative(ticket);
            _nativeReliableFragments -= ticket.Fragments;
            _nativeReliableBytes -= ticket.Bytes;
        }

        internal void RecordPendingHighWater(int packets, long bytes)
        {
            if (packets > _pendingReliablePacketsHighWater)
                _pendingReliablePacketsHighWater = packets;
            if (bytes > _pendingReliableBytesHighWater)
                _pendingReliableBytesHighWater = bytes;
        }

        internal int GetMaxUnreliableBytes(LiteNetLibEndpoint endpoint)
        {
            if (endpoint.Peer == null)
                return _settings.MaximumUnreliableBytes;
            var native = endpoint.Peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
            return Math.Min(_settings.MaximumUnreliableBytes, native);
        }

        internal void OnPeerConnected(NetPeer peer)
        {
            if (_disposed)
            {
                peer.Disconnect();
                return;
            }
            if (_endpoints.TryGetValue(peer, out var existing))
            {
                existing.MarkConnected();
                if (_listener)
                    _accepted.Enqueue(existing);
                return;
            }
            if (_endpoints.Count >= _settings.MaximumConnections)
            {
                _dropped++;
                peer.Disconnect();
                return;
            }
            var endpoint = CreateEndpoint(peer, new ConnectionId(++_nextConnection));
            endpoint.MarkConnected();
            _endpoints.Add(peer, endpoint);
            _drainOrder.Add(endpoint);
            _accepted.Enqueue(endpoint);
        }

        internal void OnConnectionRequest(ConnectionRequest request)
        {
            if (!_listener || _disposed || _endpoints.Count >= _settings.MaximumConnections)
            {
                _dropped++;
                request.RejectForce();
                return;
            }
            request.Accept();
        }

        internal void OnPeerDisconnected(NetPeer peer)
        {
            if (_endpoints.TryGetValue(peer, out var endpoint))
            {
                endpoint.CloseFromDriver();
                _endpoints.Remove(peer);
                RemoveFromDrainOrder(endpoint);
                _disconnects++;
                if (_disconnected.Count < _settings.MaximumConnections)
                    _disconnected.Enqueue(endpoint.Connection);
                else
                    _dropped++;
            }
        }

        internal void OnReceive(NetPeer peer, NetPacketReader reader,
            byte channelNumber, DeliveryMethod deliveryMethod)
        {
            using var receiveScope = NetworkDiagnosticMarkers.Measure(NetworkDiagnosticPhase.ReceiveCallback);
            if (!_endpoints.TryGetValue(peer, out var endpoint) || endpoint.IsDisposed)
                return;
            var reliable = deliveryMethod == DeliveryMethod.ReliableOrdered;
            var sequenced = deliveryMethod == DeliveryMethod.Sequenced;
            var limit = reliable ? LiteNetLibLimits.MaximumReliableBytes : endpoint.MaxUnreliablePayloadBytes;
            if (channelNumber != 0 || (!reliable && !sequenced) || reader.IsNull ||
                reader.UserDataSize < PacketHeader.Size || reader.UserDataSize > limit)
            {
                RejectReceive(reliable, endpoint, reader.UserDataSize > LiteNetLibLimits.MaximumReliableBytes);
                return;
            }
            if (endpoint.QueuedPackets >= _settings.ReceiveQueueCapacity)
            {
                _dropped++;
                _receiveQueueOverflows++;
                if (reliable)
                {
                    _reliableReceiveOverflowDisconnects++;
                    endpoint.DisconnectFromOverflow();
                }
                else
                    _unreliableReceiveDrops++;
                return;
            }
            var packet = _pool.Copy(new ReadOnlySpan<byte>(reader.RawData,
                reader.UserDataOffset, reader.UserDataSize));
            if (!NetworkPacket.TryDecode(packet, out var header, out _) ||
                (header.Flags == PacketFlags.ReliableOrdered) != reliable ||
                (header.Flags == PacketFlags.UnreliableSequenced) != sequenced)
            {
                packet.Dispose();
                RejectReceive(reliable, endpoint, false);
                return;
            }
            endpoint.EnqueueReceive(packet);
            _received++;
            if (reliable)
            {
                _reliableReceivedPackets++;
                _reliableReceivedBytes += packet.Length;
            }
            else
            {
                _unreliableReceivedPackets++;
                _unreliableReceivedBytes += packet.Length;
            }
        }

        private void RejectReceive(bool reliable, LiteNetLibEndpoint endpoint, bool oversized)
        {
            _dropped++;
            _malformedPackets++;
            if (oversized && reliable)
                endpoint.DisconnectFromOverflow();
        }

        internal void OnDelivery(NetPeer peer, object userData)
        {
            if (userData is DeliveryTicket ticket)
            {
                ticket.CompleteByCallback();
                _deliveryCallbacks++;
            }
        }

        internal void DisposeEndpoint(LiteNetLibEndpoint endpoint)
        {
            if (endpoint.IsDisposed)
                return;
            endpoint.MarkDisposed();
            RemoveFromDrainOrder(endpoint);
            if (endpoint.Peer != null && endpoint.Peer.ConnectionState != ConnectionState.Disconnected)
                endpoint.Peer.Disconnect();
        }

        private LiteNetLibEndpoint CreateEndpoint(NetPeer peer, ConnectionId connection) =>
            new LiteNetLibEndpoint(this, peer, connection, _settings.ReceiveQueueCapacity);

        internal bool SubmitQueuedReliable(LiteNetLibEndpoint endpoint,
            NetworkBufferLease packet, PacketHeader header, int fragments) =>
            SubmitReliable(endpoint, packet, header, fragments, true);

        private bool DropAccepted(NetworkBufferLease packet)
        {
            _dropped++;
            return true;
        }

        private void RejectSend()
        {
            _dropped++;
            _sendFailures++;
        }

        private void RejectMalformedSend()
        {
            _dropped++;
            _sendFailures++;
            _malformedPackets++;
        }

        internal void RecordReliableQueueOverflow()
        {
            _dropped++;
            _sendFailures++;
            _reliableSendQueueOverflows++;
        }

        private void ObserveNativePacketPool()
        {
            var count = _manager.PoolCount;
            if (count < 0)
                return;
            if (!_nativePacketPoolObserved)
            {
                if (count <= 0)
                    return;
                _nativePacketPoolObserved = true;
                _nativePacketPoolLowWater = count;
                return;
            }
            if (count < _nativePacketPoolLowWater)
                _nativePacketPoolLowWater = count;
        }

        internal LiteNetLibDiagnostics CaptureDiagnostics()
        {
            var pendingPackets = 0;
            long pendingBytes = 0;
            var queuedPackets = 0;
            var nativeQueuePackets = 0;
            foreach (var endpoint in _endpoints.Values)
            {
                pendingPackets += endpoint.PendingReliablePackets;
                pendingBytes += endpoint.PendingReliableBytes;
                queuedPackets += endpoint.QueuedPackets;
                if (!endpoint.IsDisposed)
                    nativeQueuePackets += endpoint.Peer.GetPacketsCountInReliableQueue(0, true);
            }
            var buffers = _pool.CaptureDiagnostics();
            return new LiteNetLibDiagnostics
            {
                Connections = _endpoints.Count,
                ReceivedPackets = _received,
                ReliableReceivedPackets = _reliableReceivedPackets,
                ReliableReceivedBytes = _reliableReceivedBytes,
                UnreliableReceivedPackets = _unreliableReceivedPackets,
                UnreliableReceivedBytes = _unreliableReceivedBytes,
                SentPackets = _sent,
                ReliableSentPackets = _reliableSentPackets,
                ReliableSentBytes = _reliableSentBytes,
                UnreliableSentPackets = _unreliableSentPackets,
                UnreliableSentBytes = _unreliableSentBytes,
                DroppedPackets = _dropped,
                ReceiveQueueOverflows = _receiveQueueOverflows,
                MalformedPackets = _malformedPackets,
                SendFailures = _sendFailures,
                Disconnects = _disconnects,
                PendingReliablePackets = pendingPackets,
                PendingReliableBytes = pendingBytes,
                PendingReliablePacketsHighWater = _pendingReliablePacketsHighWater,
                PendingReliableBytesHighWater = _pendingReliableBytesHighWater,
                ReliableSendQueueOverflows = _reliableSendQueueOverflows,
                QueuedPackets = queuedPackets,
                OutstandingLeases = buffers.OutstandingLeases,
                NativeReliableFragments = _nativeReliableFragments,
                NativeReliableBytes = _nativeReliableBytes,
                NativeReliableFragmentsHighWater = _nativeReliableFragmentsHighWater,
                NativeReliableBytesHighWater = _nativeReliableBytesHighWater,
                NativeReliableQueuePackets = nativeQueuePackets,
                NativeSentPackets = _manager.Statistics.PacketsSent,
                NativeReceivedPackets = _manager.Statistics.PacketsReceived,
                NativeSentBytes = _manager.Statistics.BytesSent,
                NativeReceivedBytes = _manager.Statistics.BytesReceived,
                NativePacketLoss = _manager.Statistics.PacketLoss,
                DeliveryCallbacks = _deliveryCallbacks,
                ReliableReceiveOverflowDisconnects = _reliableReceiveOverflowDisconnects,
                UnreliableReceiveDrops = _unreliableReceiveDrops,
                NativePacketPoolCount = _manager.PoolCount,
                NativePacketPoolCapacity = _manager.PacketPoolSize,
                NativePacketPoolLowWater = _nativePacketPoolLowWater,
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var endpoint in _endpoints.Values)
                endpoint.CloseFromDriver();
            _endpoints.Clear();
            _drainOrder.Clear();
            _drainCursor = 0;
            _accepted.Clear();
            _disconnected.Clear();
            _deliveryTickets.Clear();
            _manager.Stop(false);
            _pool.Dispose();
        }

        private static IPAddress ParseAddress(string address, bool listener)
        {
            if (IPAddress.TryParse(address, out var parsed))
                return parsed;
            return listener ? IPAddress.Any : IPAddress.Loopback;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LiteNetLibDriver));
        }

        private sealed class Listener : INetEventListener
        {
            private readonly LiteNetLibDriver _owner;

            public Listener(LiteNetLibDriver owner) => _owner = owner;
            public void OnPeerConnected(NetPeer peer) => _owner.OnPeerConnected(peer);
            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) => _owner.OnPeerDisconnected(peer);
            public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) => _owner._dropped++;
            public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) =>
                _owner.OnReceive(peer, reader, channelNumber, deliveryMethod);
            public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) =>
                _owner._dropped++;
            public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
            public void OnConnectionRequest(ConnectionRequest request) => _owner.OnConnectionRequest(request);
            public void OnMessageDelivered(NetPeer peer, object userData) => _owner.OnDelivery(peer, userData);
        }

        internal enum DeliveryTicketState
        {
            Active = 0,
            CompletedByCallback = 1,
            Retired = 2,
        }

        internal sealed class DeliveryTicket
        {
            private readonly LiteNetLibDriver _driver;
            private LiteNetLibEndpoint _endpoint;
            private int _state;

            internal DeliveryTicket(LiteNetLibDriver driver)
            {
                _driver = driver;
                _state = (int)DeliveryTicketState.Active;
            }

            internal LiteNetLibEndpoint Endpoint => _endpoint;
            public int Fragments { get; private set; }
            public int Bytes { get; private set; }
            public bool Snapshot { get; private set; }
            internal DeliveryTicketState State => (DeliveryTicketState)_state;

            internal void Reset(LiteNetLibEndpoint endpoint,
                int fragments, int bytes, bool snapshot)
            {
                _endpoint = endpoint;
                Fragments = fragments;
                Bytes = bytes;
                Snapshot = snapshot;
                _state = (int)DeliveryTicketState.Active;
            }

            internal bool CompleteByCallback()
            {
                if (System.Threading.Interlocked.CompareExchange(ref _state,
                        (int)DeliveryTicketState.CompletedByCallback,
                        (int)DeliveryTicketState.Active) != (int)DeliveryTicketState.Active)
                    return false;
                _endpoint.CompleteTicket(this);
                ClearOwnership();
                _driver.ReturnDeliveryTicket(this);
                return true;
            }

            internal bool Retire()
            {
                if (System.Threading.Interlocked.CompareExchange(ref _state,
                        (int)DeliveryTicketState.Retired,
                        (int)DeliveryTicketState.Active) != (int)DeliveryTicketState.Active)
                    return false;
                _endpoint.CompleteTicket(this);
                ClearOwnership();
                return true;
            }

            private void ClearOwnership()
            {
                _endpoint = null;
                Fragments = 0;
                Bytes = 0;
                Snapshot = false;
            }
        }
    }

    internal sealed class LiteNetLibEndpoint : INetworkTransport
    {
        private readonly LiteNetLibDriver _owner;
        private readonly Queue<NetworkBufferLease> _incoming;
        private readonly Queue<PendingReliable> _pendingReliable = new Queue<PendingReliable>();
        private readonly HashSet<LiteNetLibDriver.DeliveryTicket> _tickets =
            new HashSet<LiteNetLibDriver.DeliveryTicket>();
        private bool _connected;
        private bool _disposed;
        private bool _overflowDisconnected;
        private long _pendingReliableBytes;
        private int _nativeReliableFragments;
        private long _nativeReliableBytes;
        private int _pendingSnapshotChunks;
        private uint _pendingSnapshotTick;

        internal LiteNetLibEndpoint(LiteNetLibDriver owner, NetPeer peer,
            ConnectionId connection, int receiveQueueCapacity)
        {
            _owner = owner;
            Peer = peer;
            Connection = connection;
            _incoming = new Queue<NetworkBufferLease>(receiveQueueCapacity);
        }

        public ConnectionId Connection { get; }
        internal NetPeer Peer { get; }
        internal bool IsConnected => _connected && !_disposed;
        internal bool IsDisposed => _disposed;
        internal int QueuedPackets => _incoming.Count;
        internal int PendingReliablePackets => _pendingReliable.Count;
        internal long PendingReliableBytes => _pendingReliableBytes;
        internal int NativeReliableFragments => _nativeReliableFragments;
        internal long NativeReliableBytes => _nativeReliableBytes;
        internal int MaxUnreliablePayloadBytes => _owner.GetMaxUnreliableBytes(this);
        public int MaxReliablePayloadBytes => LiteNetLibLimits.MaximumReliableBytes;
        public bool TrySend(NetworkBufferLease packet) => _owner.TrySend(this, packet);

        int INetworkTransportCapabilities.MaxUnreliablePayloadBytes => MaxUnreliablePayloadBytes;

        public bool TryReceive(out NetworkBufferLease packet)
        {
            if (!_disposed && _incoming.Count > 0)
            {
                packet = _incoming.Dequeue();
                return true;
            }
            packet = null;
            return false;
        }

        public void Dispose() => _owner.DisposeEndpoint(this);

        internal void MarkConnected() => _connected = true;

        internal void EnqueueReceive(NetworkBufferLease packet)
        {
            if (_disposed)
            {
                packet.Dispose();
                return;
            }
            _incoming.Enqueue(packet);
        }

        internal bool ValidateSnapshotTick(PacketHeader header)
        {
            if (header.Kind != PacketKind.SnapshotChunk || _pendingSnapshotChunks == 0)
                return true;
            return header.ServerTick >= _pendingSnapshotTick;
        }

        internal bool SnapshotTickIsNewer(PacketHeader header) =>
            header.Kind == PacketKind.SnapshotChunk && _pendingSnapshotChunks != 0 &&
            header.ServerTick > _pendingSnapshotTick;

        internal void TrackSnapshot(PacketHeader header)
        {
            if (header.Kind != PacketKind.SnapshotChunk)
                return;
            if (_pendingSnapshotChunks == 0)
                _pendingSnapshotTick = header.ServerTick;
            _pendingSnapshotChunks++;
        }

        internal bool TryQueue(NetworkBufferLease packet, PacketHeader header, int fragments)
        {
            if (_pendingReliable.Count >= _owner.Settings.ReliableSendQueueCapacity ||
                _pendingReliableBytes > _owner.Settings.ReliableSendBytesCapacity - packet.Length)
            {
                _owner.RecordReliableQueueOverflow();
                return false;
            }
            TrackSnapshot(header);
            _pendingReliable.Enqueue(new PendingReliable(packet, header, fragments));
            _pendingReliableBytes += packet.Length;
            _owner.RecordPendingHighWater(_pendingReliable.Count, _pendingReliableBytes);
            return true;
        }

        internal bool DrainOneReliable()
        {
            if (_disposed || _pendingReliable.Count == 0)
                return false;
            var next = _pendingReliable.Peek();
            if (!_owner.CanRegisterNative(this, next.Fragments, next.Packet.Length))
                return false;
            _pendingReliable.Dequeue();
            _pendingReliableBytes -= next.Packet.Length;
            _owner.SubmitQueuedReliable(this, next.Packet, next.Header, next.Fragments);
            return true;
        }

        internal void RegisterNative(LiteNetLibDriver.DeliveryTicket ticket)
        {
            _nativeReliableFragments = checked(_nativeReliableFragments + ticket.Fragments);
            _nativeReliableBytes = checked(_nativeReliableBytes + ticket.Bytes);
        }

        internal void ReleaseNative(LiteNetLibDriver.DeliveryTicket ticket)
        {
            _nativeReliableFragments -= ticket.Fragments;
            _nativeReliableBytes -= ticket.Bytes;
        }

        internal void TrackTicket(LiteNetLibDriver.DeliveryTicket ticket) => _tickets.Add(ticket);

        internal bool TryGetActiveTicket(out LiteNetLibDriver.DeliveryTicket ticket)
        {
            foreach (var candidate in _tickets)
            {
                ticket = candidate;
                return true;
            }
            ticket = null;
            return false;
        }

        internal void CompleteTicket(LiteNetLibDriver.DeliveryTicket ticket)
        {
            if (!_tickets.Remove(ticket))
                return;
            _owner.ReleaseNative(ticket);
            if (ticket.Snapshot)
                ReleaseSnapshotCount();
        }

        internal void DisconnectFromOverflow()
        {
            if (_overflowDisconnected || _disposed)
                return;
            _overflowDisconnected = true;
            _connected = false;
            Peer.Disconnect();
        }

        internal void MarkDisposed()
        {
            _disposed = true;
            _connected = false;
            ReleaseOwnedBuffers();
        }

        internal void CloseFromDriver()
        {
            if (_disposed)
                return;
            _disposed = true;
            _connected = false;
            ReleaseOwnedBuffers();
        }

        private void ReleaseOwnedBuffers()
        {
            while (_incoming.Count > 0)
                _incoming.Dequeue().Dispose();
            while (_pendingReliable.Count > 0)
            {
                var pending = _pendingReliable.Dequeue();
                pending.Packet.Dispose();
                if (pending.Header.Kind == PacketKind.SnapshotChunk)
                    ReleaseSnapshotCount();
            }
            _pendingReliableBytes = 0;
            if (_tickets.Count > 0)
            {
                var tickets = new List<LiteNetLibDriver.DeliveryTicket>(_tickets);
                foreach (var ticket in tickets)
                    ticket.Retire();
            }
            _pendingSnapshotChunks = 0;
            _pendingSnapshotTick = 0;
        }

        private void ReleaseSnapshotCount()
        {
            if (_pendingSnapshotChunks == 0)
                return;
            _pendingSnapshotChunks--;
            if (_pendingSnapshotChunks == 0)
                _pendingSnapshotTick = 0;
        }

        private readonly struct PendingReliable
        {
            public PendingReliable(NetworkBufferLease packet, PacketHeader header, int fragments)
            {
                Packet = packet;
                Header = header;
                Fragments = fragments;
            }

            public NetworkBufferLease Packet { get; }
            public PacketHeader Header { get; }
            public int Fragments { get; }
        }
    }

}
