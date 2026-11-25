package net.oxog.phonebox.deskpanel

import net.oxog.phonebox.deskpanel.PcProbe.Host
import org.junit.Assert.assertEquals
import org.junit.Test

class PcProbeTest {
    @Test
    fun onlyTheLocalNetworksOwnAnswersDecide() {
        assertEquals(Host.UP, PcProbe.classify(null, timedOut = false))           // connected
        assertEquals(Host.UP, PcProbe.classify("ECONNREFUSED", timedOut = false)) // a running computer, no listener
        assertEquals(Host.DOWN, PcProbe.classify("EHOSTUNREACH", timedOut = false)) // nobody answered ARP
        // a firewall, or a sleeping card that still answers ARP, drops the attempt: that settles nothing
        assertEquals(Host.UNKNOWN, PcProbe.classify(null, timedOut = true))
        assertEquals(Host.UNKNOWN, PcProbe.classify("ENETUNREACH", timedOut = false)) // the phone's own network
    }

    @Test
    fun oneAddressIsOnWhenAnyPortAnswers() {
        assertEquals(Host.UP, PcProbe.ofAddress(listOf(Host.UNKNOWN, Host.UP, Host.DOWN)))
        assertEquals(Host.DOWN, PcProbe.ofAddress(listOf(Host.UNKNOWN, Host.DOWN)))
        assertEquals(Host.UNKNOWN, PcProbe.ofAddress(listOf(Host.UNKNOWN, Host.UNKNOWN)))
    }

    @Test
    fun theComputerIsOffOnlyWhenEveryAddressIsDark() {
        assertEquals(Host.UP, PcProbe.ofComputer(listOf(Host.DOWN, Host.UP)))
        assertEquals(Host.DOWN, PcProbe.ofComputer(listOf(Host.DOWN, Host.DOWN)))
        // a stale second address next to one that only timed out
        assertEquals(Host.UNKNOWN, PcProbe.ofComputer(listOf(Host.DOWN, Host.UNKNOWN)))
        assertEquals(Host.UNKNOWN, PcProbe.ofComputer(emptyList()))
    }

    @Test
    fun theAgentPortComesFirstAndIsNotTriedTwice() {
        assertEquals(listOf(8766, 445, 135), PcProbe.ports(8766))
        assertEquals(listOf(445, 135), PcProbe.ports(445))
    }
}
