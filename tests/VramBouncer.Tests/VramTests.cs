using System.Net;
using System.Net.Sockets;
using VramBouncer.Features;

namespace VramBouncer.Tests;

[TestClass]
public class VramTests
{
    [TestMethod]
    public void ListenerPidFindsOurOwnSocket()
    {
        foreach (var addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var l = new TcpListener(addr, 0);
            l.Start();
            try { Assert.AreEqual(Environment.ProcessId, TcpPorts.ListenerPid(((IPEndPoint)l.LocalEndpoint).Port)); }
            finally { l.Stop(); }
        }
    }

    [TestMethod]
    public void PdhInstanceGivesPid()
    {
        Assert.AreEqual(23928, VramMonitor.PidOf("pid_23928_luid_0x00000000_0x000127A3_phys_0"));
        Assert.IsNull(VramMonitor.PidOf("_Total"));
    }
}
