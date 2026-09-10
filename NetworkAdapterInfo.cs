using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MailLoadTester;

public static class NetworkAdapterInfo
{
    public sealed record AdapterSummary(
        string DisplayName,
        string Description,
        string IPv4,
        bool IsLikelyDefault,
        bool IsUp,
        long? SpeedBps,
        string TypeName);

    public static string GetStatusLine()
    {
        try
        {
            var list = GetAdapters();
            var preferred = list.FirstOrDefault(a => a.IsLikelyDefault)
                            ?? list.FirstOrDefault(a => a.IsUp && !string.IsNullOrEmpty(a.IPv4));
            if (preferred is null)
                return "Síť: žádný aktivní adaptér s IPv4";
            var speed = preferred.SpeedBps is > 0
                ? $" · {FormatSpeed(preferred.SpeedBps.Value)}"
                : "";
            return $"Síť: {preferred.DisplayName} ({preferred.IPv4}){speed}";
        }
        catch (Exception ex)
        {
            return "Síť: nelze zjistit (" + ex.Message + ")";
        }
    }

    /// <summary>
    /// Vrátí seznam aktivních lokálních IPv4 adres (pro ComboBox Source IP).
    /// </summary>
    public static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(ua.Address.ToString());
            }
        }
        return list.Distinct().OrderBy(x => x).ToList();
    }

    public static string GetHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SÍŤOVÝ ADAPTÉR (informativní)");
        sb.AppendLine("MailLoadTester neumožňuje přepnout kartu uvnitř aplikace.");
        sb.AppendLine("SMTP jde přes výchozí směrování Windows (stejně jako prohlížeč).");
        sb.AppendLine();
        sb.AppendLine("Jak Windows vybírá adaptér:");
        sb.AppendLine("• Použije se rozhraní s výchozí bránou (0.0.0.0) a nejnižší metrikou.");
        sb.AppendLine("• Ethernet má často nižší metriku než Wi‑Fi → má přednost.");
        sb.AppendLine("• USB/externí Ethernet se chová jako další síťová karta.");
        sb.AppendLine("• VPN může vytvořit vlastní výchozí trasu a „převzít“ provoz.");
        sb.AppendLine();
        sb.AppendLine("Jak zvolit JINÝ adaptér (když to nejde v aplikaci):");
        sb.AppendLine("1) Nastavení → Síť a Internet → Pokročilá nastavení sítě");
        sb.AppendLine("   → Další možnosti adaptéru (nebo ncpa.cpl)");
        sb.AppendLine("2) U NECHTĚNÉ karty: pravý klik → Zakázat");
        sb.AppendLine("   (nebo Vlastnosti → IPv4 → Upřesnit → zrušit „Automatická metrika“");
        sb.AppendLine("    a nastavit vyšší číslo metriky = nižší priorita)");
        sb.AppendLine("3) U POŽADOVANÉ karty nechte bránu a nižší metriku.");
        sb.AppendLine("4) Ověření v cmd:  route print -4");
        sb.AppendLine("   Řádek „0.0.0.0“ ukazuje, které rozhraní je výchozí.");
        sb.AppendLine();
        sb.AppendLine("Detekované adaptéry:");
        try
        {
            foreach (var a in GetAdapters())
            {
                var mark = a.IsLikelyDefault ? " ← pravděpodobně výchozí" : "";
                var up = a.IsUp ? "UP" : "down";
                sb.AppendLine($"• [{up}] {a.DisplayName} | {a.IPv4} | {a.TypeName}{mark}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("(seznam nedostupný: " + ex.Message + ")");
        }
        return sb.ToString();
    }

    public static IReadOnlyList<AdapterSummary> GetAdapters()
    {
        var result = new List<AdapterSummary>();
        var defaultIfIndex = TryGetDefaultInterfaceIndex();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = ni.GetIPProperties();
            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                     && !IPAddress.IsLoopback(u.Address));
            if (ipv4 is null && ni.OperationalStatus != OperationalStatus.Up)
                continue;

            var ipStr = ipv4?.Address.ToString() ?? "—";
            var isUp = ni.OperationalStatus == OperationalStatus.Up;
            var isDefault = false;

            // Try to match by interface index (most reliable)
            if (defaultIfIndex.HasValue && ipv4 != null)
            {
                try
                {
                    var v4 = props.GetIPv4Properties();
                    if (v4 != null && v4.Index == defaultIfIndex.Value)
                        isDefault = true;
                }
                catch { }
            }

            // Fallback: first UP adapter with IPv4 gateway if no index match
            if (!isDefault && isUp && defaultIfIndex is null)
            {
                if (props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)))
                {
                    isDefault = result.All(r => !r.IsLikelyDefault);
                }
            }

            long? speed = null;
            try { if (ni.Speed > 0) speed = ni.Speed; }
            catch { }

            result.Add(new AdapterSummary(
                string.IsNullOrWhiteSpace(ni.Name) ? ni.Description : ni.Name,
                ni.Description, ipStr, isDefault && isUp, isUp, speed,
                ni.NetworkInterfaceType.ToString()));
        }

        // If still no default, mark first UP with IPv4
        if (result.All(r => !r.IsLikelyDefault))
        {
            var candidate = result.FirstOrDefault(r => r.IsUp && r.IPv4 != "—");
            if (candidate is not null)
            {
                var i = result.IndexOf(candidate);
                result[i] = candidate with { IsLikelyDefault = true };
            }
        }

        return result
            .OrderByDescending(a => a.IsLikelyDefault)
            .ThenByDescending(a => a.IsUp)
            .ToList();
    }

    static int? TryGetDefaultInterfaceIndex()
    {
        try
        {
            // Use route table to find the interface with the default route (0.0.0.0/0)
            // that has the lowest metric. This is more accurate than just picking first gateway.
            var candidates = new List<(int Index, int Metric)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var p = ni.GetIPProperties();
                var v4 = p.GetIPv4Properties();
                if (v4 is null) continue;
                var gw = p.GatewayAddresses.FirstOrDefault(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)
                    && !g.Address.Equals(IPAddress.None));
                if (gw is null) continue;
                // We can't easily read metric from managed API, so we use index as proxy
                // and rely on Windows ordering. Better than nothing.
                candidates.Add((v4.Index, v4.Index));
            }
            return candidates.OrderBy(c => c.Metric).FirstOrDefault().Index;
        }
        catch { }
        return null;
    }

    static string FormatSpeed(long bps)
    {
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:0.#} Gb/s";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.#} Mb/s";
        if (bps >= 1_000) return $"{bps / 1_000.0:0.#} kb/s";
        return $"{bps} b/s";
    }
}
