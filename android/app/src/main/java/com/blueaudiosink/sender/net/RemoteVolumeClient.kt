package com.blueaudiosink.sender.net

/** Optional control channel; all methods serialize UI, receive and retry callers. */
class RemoteVolumeClient(
    private val send: (Int, Int, ByteArray) -> Unit,
    private val changed: (Int?, Boolean) -> Unit,
) {
    private data class Pending(val id: Int, val epoch: Int, val revision: Int, val value: Int,
        var at: Long, var retries: Int = 0)
    private var token = 0
    private var epoch = 0
    private var revision = 0
    private var value: Int? = null
    private var desired: Int? = null
    private var pending: Pending? = null
    private var nextId = 0
    private var lastState = -1L
    private var lastQuery = -1000L
    private var lastSend = -100L
    private var selected = false

    @Synchronized fun reset(selected: Boolean) {
        this.selected = selected
        token = kotlin.random.Random.nextInt().let { if (it == 0) 1 else it }
        epoch = 0; revision = 0; value = null; desired = null; pending = null
        lastState = -1; lastQuery = -1000; lastSend = -100
        changed(null, false)
    }

    @Synchronized fun set(value: Int, now: Long) {
        if (!selected || lastState < 0 || now - lastState > 3000) return
        desired = value.coerceIn(0, 100)
        publish(true)
    }

    @Synchronized fun tick(now: Long) {
        if (!selected) return
        if (lastState >= 0 && now - lastState > 3000) {
            reset(true) // Reconnect never replays a drag from the previous connection.
        }
        if (pending == null && desired == null && now - lastQuery >= 1000) {
            lastQuery = now
            send(PacketCodec.TYPE_VOLUME_GET, 0, ints(token))
        }
        val p = pending
        if (p != null) {
            if (now - p.at >= 250) {
                if (p.retries >= 3) { reset(true); return }
                p.retries++; p.at = now; sendPending(p)
            }
            return
        }
        val wanted = desired ?: return
        if (lastState < 0 || now - lastSend < 100) return
        if (wanted == value) { desired = null; publish(true); return }
        nextId++; if (nextId == 0) nextId++
        pending = Pending(nextId, epoch, revision, wanted, now).also {
            lastSend = now; sendPending(it)
        }
    }

    @Synchronized fun receive(data: ByteArray, seq: Int, now: Long) {
        if (!selected || data.size != 14 || PacketCodec.getIntLE(data, 0) != token) return
        val incomingEpoch = PacketCodec.getIntLE(data, 4)
        val incomingRevision = PacketCodec.getIntLE(data, 8)
        val incomingValue = data[12].toInt() and 255
        if (incomingEpoch == 0 || incomingValue > 100) return
        if (epoch == incomingEpoch && lastState >= 0 && incomingRevision - revision < 0) return
        if (epoch != incomingEpoch) { pending = null; desired = null }
        val p = pending
        if (p != null && (seq == p.id || incomingRevision != p.revision)) {
            val accepted = seq == p.id && (data[13].toInt() and 2) != 0 && incomingEpoch == p.epoch && incomingRevision == p.revision + 1 && incomingValue == p.value
            pending = null
            if (!accepted || desired == p.value) desired = null
        } else if (p == null && incomingRevision != revision) {
            desired = null // A local PC edit wins over an unsent slider value.
        }
        epoch = incomingEpoch; revision = incomingRevision; value = incomingValue; lastState = now
        publish(true)
    }

    private fun publish(ready: Boolean) = changed(desired ?: pending?.value ?: value, ready)
    private fun sendPending(p: Pending) {
        val data = ByteArray(13)
        PacketCodec.putIntLE(data, 0, token); PacketCodec.putIntLE(data, 4, p.epoch)
        PacketCodec.putIntLE(data, 8, p.revision); data[12] = p.value.toByte()
        send(PacketCodec.TYPE_VOLUME_SET, p.id, data)
    }
    private fun ints(value: Int) = ByteArray(4).also { PacketCodec.putIntLE(it, 0, value) }
}
