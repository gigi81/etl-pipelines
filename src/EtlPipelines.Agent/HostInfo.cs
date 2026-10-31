using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using EtlPipelines.AgentExecution.V1;
using Google.Protobuf.WellKnownTypes;

namespace EtlPipelines.Agent;

/// <summary>Gathers the host details an agent reports when it registers, for the WebUI's agents page.</summary>
public static class HostInfo
{
    /// <summary>Fills the host-detail fields of <paramref name="request"/>.</summary>
    public static void Describe(RegisterAgentRequest request)
    {
        request.OsDescription = RuntimeInformation.OSDescription;
        request.OsArchitecture = RuntimeInformation.OSArchitecture.ToString();
        request.ProcessorCount = Environment.ProcessorCount;
        request.IpAddresses.AddRange(GetIpAddresses());

        try
        {
            using var process = Process.GetCurrentProcess();
            request.StartedAt = Timestamp.FromDateTime(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Process start time is best-effort; the UI just shows no uptime.
            request.StartedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        }
    }

    /// <summary>The unicast IPv4/IPv6 addresses of every operational, non-loopback interface (link-local IPv6 excluded).</summary>
    public static IReadOnlyList<string> GetIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
                    && !address.IsIPv6LinkLocal)
                .Select(address => address.ToString())
                .Distinct()
                .Order()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
