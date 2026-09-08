# Static ECS LiteNetLib transport

The Unity source dependency is resolved by the project manifest from https://github.com/RevenantX/LiteNetLib.git?path=/LiteNetLib#4d3de1e93abaead30199bf572f4a3363f854e14b; the package manifest keeps the upstream UPM version 1.0.1-1.

This package adapts LiteNetLib 2.1.4 to the exact packet contract from `com.unigame.staticecs.network`.

`LiteNetLibClientHost` and `LiteNetLibServerHost` use manual pumping. Call `Update()` before protocol receives, call `Flush()` after protocol sends, and dispose every received `NetworkBufferLease` after use. `TrySend` consumes the supplied lease on every result.

The adapter fixes native MTU at 1200 bytes, uses one channel, and exposes a 64 KiB complete reliable packet capability. Reliable packets are fragmented by LiteNetLib up to the native fragment limit; sequenced packets are capped by `NetPeer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced)`.

The receive queue, adapter reliable FIFO, native delivery-ticket fragments, and bytes are bounded per connection. Reliable receive overflow disconnects its peer; sequenced receive overflow drops only that packet. `LiteNetLibDiagnostics.NativeReliableQueuePackets` is LiteNetLib's queue-only count and does not include in-flight delivery ownership.
