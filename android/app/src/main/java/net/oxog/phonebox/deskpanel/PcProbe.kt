package net.oxog.phonebox.deskpanel

/**
 * Whether the paired computer is still on while its agent is silent, found out by the phone alone: a TCP connect to
 * the computer's address on the phone's own network. The answer that settles it is the local network's own:
 * "connection refused" (or a connection) comes from a running computer, and "no route to host" means nothing answered
 * the network's address lookup (ARP) at all, so the computer is off or asleep. A timeout settles nothing: a running
 * Windows firewall drops the attempt silently, and so does a sleeping network card that still answers ARP for its host.
 */
internal object PcProbe {
    enum class Host { UP, DOWN, UNKNOWN }

    /** Long enough for the kernel to give up on a stale ARP entry (about 5 s, then 3 probes 1 s apart). */
    const val CONNECT_TIMEOUT_MS = 10_000

    /** Ports tried on each address: the agent's own, then SMB and RPC, which a running Windows computer usually answers. */
    fun ports(agentPort: Int): List<Int> = listOf(agentPort, 445, 135).distinct()

    /** One attempt's outcome, from the errno name Android reports ("ECONNREFUSED"); null when it connected. */
    fun classify(errno: String?, timedOut: Boolean): Host = when {
        timedOut -> Host.UNKNOWN
        errno == null -> Host.UP
        errno == "ECONNREFUSED" || errno == "ECONNRESET" -> Host.UP
        errno == "EHOSTUNREACH" -> Host.DOWN
        else -> Host.UNKNOWN // ENETUNREACH and the like: the phone's own network, not the computer
    }

    /** One address: any answer from the computer wins; a failed lookup on any port means the address is dark. */
    fun ofAddress(attempts: List<Host>): Host = when {
        Host.UP in attempts -> Host.UP
        Host.DOWN in attempts -> Host.DOWN
        else -> Host.UNKNOWN
    }

    /**
     * Every address together: on if any answers, off only when all are dark (a computer with Ethernet and Wi-Fi may
     * have one stale address), unknown otherwise and when there was nothing to try.
     */
    fun ofComputer(addresses: List<Host>): Host = when {
        Host.UP in addresses -> Host.UP
        addresses.isNotEmpty() && addresses.all { it == Host.DOWN } -> Host.DOWN
        else -> Host.UNKNOWN
    }

    /** The errno name in an exception's cause chain ("EHOSTUNREACH"), or in its message on systems that give no cause. */
    fun errnoName(e: Throwable): String? {
        var t: Throwable? = e
        while (t != null) {
            if (t is android.system.ErrnoException) return android.system.OsConstants.errnoName(t.errno)
            t = t.cause
        }
        return Regex("\\b(E[A-Z]{3,})\\b").find(e.message ?: "")?.groupValues?.get(1)
    }
}
