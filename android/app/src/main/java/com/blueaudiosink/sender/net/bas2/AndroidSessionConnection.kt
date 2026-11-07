package com.blueaudiosink.sender.net.bas2

/** Android Network binding is deliberately outside the pure session/route state machines. */
internal class AndroidSessionConnection(
    route: RouteCandidate,
    private val expectedId: String?,
    private val instanceId: String,
    private val phoneName: String,
    private val phase: (ConnectionPhase) -> Unit,
) : SessionConnection {
    private val control = ControlConnection(route)
    override val deviceId get() = control.deviceId
    override val deviceName get() = control.deviceName
    override val sessionId get() = control.sessionId
    override val usingTcp get() = control.usingTcp

    override fun verify() {
        control.connect(expectedId, instanceId, phoneName) { phase(ConnectionPhase.IDENTITY) }
        phase(ConnectionPhase.VERIFYING)
        control.verifyTransport()
    }
    override fun start() = control.start()
    override fun stop() = control.stop()
    override fun heartbeat() = control.heartbeat()
    override fun sendAudio(payload: ByteArray, timestampUs: Int, discontinuity: Boolean) = control.sendAudio(payload, timestampUs, discontinuity)
    override fun close() = control.close()
}
