using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace WinPalm.Services;

/// Why a phone on the same Wi-Fi might not reach this PC: Windows blocks
/// incoming connections on Public networks, and dismissing the firewall
/// prompt silently adds a Block rule for the app.
internal static class NetworkCheck
{
  internal sealed record Problem(string Message, string ButtonText, string Target);
  private const int Public = 0, Private = 1, Domain = 2;
  internal static Problem? Find()
  {
    var connected = ConnectedNetworks();
    if (connected.Count == 0) return null;
    var publicNames = connected.Where(n => n.Category == Public).Select(n => n.Name).ToArray();
    var blocked = BlockedProfiles(connected.Select(n => n.Category).ToHashSet());
    if (blocked.Count > 0)
      return new($"Windows Firewall is blocking WinPalm on {string.Join(" and ", blocked)} networks, so phones can't connect. Allow WinPalm on Private networks in the firewall's app list.", "Allow WinPalm in Windows Firewall", "firewall");
    if (publicNames.Length > 0 && publicNames.Length == connected.Count)
      return new($"Windows treats “{publicNames[0]}” as a public network and blocks phones from reaching this PC. If it's your home or work network, set its network profile type to Private.", "Open network settings", "ms-settings:network-status");
    return null;
  }
  private static List<(string Name, int Category)> ConnectedNetworks()
  {
    var result = new List<(string, int)>();
    object? manager = null; IEnumNetworks? networks = null;
    try
    {
      manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
      if (manager is not INetworkListManager list || list.GetNetworks(1, out networks) < 0) return result; // NLM_ENUM_NETWORK_CONNECTED
      for (var i = 0; i < 16 && networks.Next(1, out var network, out var fetched) == 0 && fetched == 1; i++)
        try
        {
          if (network.GetName(out var name) >= 0 && network.GetCategory(out var category) >= 0) result.Add((name ?? "this network", category));
        }
        finally { Marshal.FinalReleaseComObject(network); }
    }
    catch (Exception error) when (error is COMException or InvalidCastException) { Diagnostics.Record(error); }
    finally
    {
      if (networks is not null) Marshal.FinalReleaseComObject(networks);
      if (manager is not null) Marshal.FinalReleaseComObject(manager);
    }
    return result;
  }
  /// Enabled inbound Block rules for this executable that cover a profile
  /// in use and whose firewall is on. Rules are stored as text in the
  /// registry, which standard users can read.
  private static List<string> BlockedProfiles(HashSet<int> categories)
  {
    var blocked = new List<string>();
    var executable = Environment.ProcessPath;
    if (executable is null) return blocked;
    try
    {
      const string policy = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
      using var rules = Registry.LocalMachine.OpenSubKey(policy + "FirewallRules");
      if (rules is null) return blocked;
      foreach (var (category, name, key) in new[] { (Private, "Private", "StandardProfile"), (Public, "Public", "PublicProfile"), (Domain, "Domain", "DomainProfile") })
      {
        if (!categories.Contains(category)) continue;
        using var profile = Registry.LocalMachine.OpenSubKey(policy + key);
        if (profile?.GetValue("EnableFirewall") is int enabled && enabled == 0) continue;
        foreach (var valueName in rules.GetValueNames())
        {
          if (rules.GetValue(valueName) is not string rule) continue;
          var fields = rule.Split('|');
          string? Field(string field) => fields.FirstOrDefault(f => f.StartsWith(field + "=", StringComparison.OrdinalIgnoreCase))?[(field.Length + 1)..];
          var app = Field("App");
          if (app is null || !string.Equals(Environment.ExpandEnvironmentVariables(app), executable, StringComparison.OrdinalIgnoreCase)) continue;
          if (!string.Equals(Field("Action"), "Block", StringComparison.OrdinalIgnoreCase) || !string.Equals(Field("Dir"), "In", StringComparison.OrdinalIgnoreCase) || string.Equals(Field("Active"), "FALSE", StringComparison.OrdinalIgnoreCase)) continue;
          var profiles = fields.Where(f => f.StartsWith("Profile=", StringComparison.OrdinalIgnoreCase)).Select(f => f[8..]).ToArray();
          if (profiles.Length == 0 || profiles.Contains(name, StringComparer.OrdinalIgnoreCase)) { blocked.Add(name); break; }
        }
      }
    }
    catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    return blocked;
  }
  [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
  private interface INetworkListManager
  {
    [PreserveSig] int GetNetworks(int flags, out IEnumNetworks networks);
  }
  [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
  private interface IEnumNetworks
  {
    [PreserveSig] int NewEnum(out nint enumerator);
    [PreserveSig] int Next(uint count, out INetwork network, out uint fetched);
  }
  [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
  private interface INetwork
  {
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string? name);
    [PreserveSig] int SetName([MarshalAs(UnmanagedType.BStr)] string name);
    [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string? description);
    [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
    [PreserveSig] int GetNetworkId(out Guid id);
    [PreserveSig] int GetDomainType(out int type);
    [PreserveSig] int GetNetworkConnections(out nint connections);
    [PreserveSig] int GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
    [PreserveSig] int IsConnectedToInternet(out short connected);
    [PreserveSig] int IsConnected(out short connected);
    [PreserveSig] int GetCategory(out int category);
  }
}
