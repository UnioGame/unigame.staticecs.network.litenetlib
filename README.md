# Static ECS LiteNetLib transport

Install LiteNetLib 2.1.4 for Unity with NuGetForUnity. The DLL must be restored as
Assets/Packages/LiteNetLib.2.1.4/lib/netstandard2.1/LiteNetLib.dll; the package
nuspec records upstream source revision
4d3de1e93abaead30199bf572f4a3363f854e14b.

This package adapts LiteNetLib 2.1.4 to the exact packet contract from
com.unigame.staticecs.network. The .NET SDK project uses the matching NuGet
package reference; Unity uses the restored netstandard2.1 DLL.

LiteNetLibClientHost and LiteNetLibServerHost use manual pumping. Call
Update() before protocol receives, call Flush() after protocol sends, and
dispose every received NetworkBufferLease after use. TrySend consumes the
supplied lease on every result.

The adapter fixes native MTU at 1200 bytes, uses one channel, and exposes a
64 KiB complete reliable packet capability. Reliable packets are fragmented by
LiteNetLib up to the native fragment limit; sequenced packets are capped by
NetPeer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced).

The receive queue, adapter reliable FIFO, native delivery-ticket fragments, and
bytes are bounded per connection. Reliable receive overflow disconnects its
peer; sequenced receive overflow drops only that packet.
LiteNetLibDiagnostics.NativeReliableQueuePackets is LiteNetLib's queue-only
count and does not include in-flight delivery ownership. NativeSentPackets and
NativeReceivedPackets are cumulative native UDP datagram counts for the host
lifetime; NativeSentBytes and NativeReceivedBytes count datagram payload bytes,
excluding IP/UDP link headers and including LiteNetLib control, fragment, and
retransmission traffic. NativePacketLoss is LiteNetLib's cumulative detected or
retransmit loss accounting, not an independently measured link-loss percentage.
