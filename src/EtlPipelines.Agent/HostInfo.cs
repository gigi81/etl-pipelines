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
    /// <summary>
    /// The details to register with, as a free-form object the Server stores as JSON without
    /// interpreting it - add a key here and it reaches <c>Agents.Info</c> and the WebUI's agents
    /// page with no schema or proto change. Keys are camelCase.
    /// </summary>
    public static Struct Describe()
    {
        var info = new Struct();
        info.Fields["osDescription"] = Value.ForString(RuntimeInformation.OSDescription);
        info.Fields["osArchitecture"] = Value.ForString(RuntimeInformation.OSArchitecture.ToString());
        info.Fields["processorCount"] = Value.ForNumber(Environment.ProcessorCount);

        // Every address, IPv4 and IPv6 alike - a host commonly has both, and several of each.
        info.Fields["ipAddresses"] = Value.ForList(GetIpAddresses().Select(Value.ForString).ToArray());

        try
        {
            using var process = Process.GetCurrentProcess();
            info.Fields["startedAt"] = Value.ForString(process.StartTime.ToUniversalTime().ToString("O"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Process start time is best-effort; the UI just shows no uptime.
        }

        return info;
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
