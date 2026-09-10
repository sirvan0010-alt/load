using MailKit.Net.Smtp;
using MailKit.Security;

namespace MailLoadTester;

public static class SmtpConnectivityTester
{
    public static async Task<string> TestAsync(MailTestOptions o, CancellationToken ct)
    {
        Validation.Validate(o);
        if (o.DryRun)
            return $"DRY-RUN: simulace připojení k {o.SmtpHost}:{o.Port} (Zabezpečení={o.Security}) — žádné reálné spojení.";

        using var client = new SmtpClient
        {
            Timeout = o.ConnectTimeoutMs
        };
        if (o.IgnoreCertificateErrors)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        // X509Certificate2 is IDisposable; SmtpClient does not own ClientCertificates entries.
        using var cert = ClientCertificateHelper.Load(o.ClientCertificatePath, o.ClientCertificatePassword);
        if (cert != null)
            client.ClientCertificates.Add(cert);

        string proxyLabel = "žádná";
        if (!string.IsNullOrWhiteSpace(o.ProxyList))
        {
            var list = ProxyClientFactory.ParseList(o.ProxyList);
            if (list.Count > 0)
            {
                client.ProxyClient = ProxyClientFactory.Create(list[0]);
                proxyLabel = list[0].DisplayKey + (list.Count > 1 ? $" (+{list.Count - 1} v seznamu, testuje se první)" : "");
            }
        }
        else if (o.UseSocks5Proxy)
        {
            var ep = new ProxyEndpoint(
                ProxyType.Socks5, o.ProxyHost, o.ProxyPort,
                string.IsNullOrEmpty(o.ProxyUsername) ? null : o.ProxyUsername,
                string.IsNullOrEmpty(o.ProxyPassword) ? null : o.ProxyPassword);
            client.ProxyClient = ProxyClientFactory.Create(ep);
            proxyLabel = ep.DisplayKey;
        }

        var socketOpts = ToSocketOptions(o.Security);
        var localEp = IpBindingHelper.ResolveLocalEndPoint(o.SourceIp, o.IpVersion);
        if (localEp != null)
        {
            var s = IpBindingHelper.CreateBoundSocket(localEp, o.IpVersion);
            try
            {
                await client.ConnectAsync(s, o.SmtpHost, o.Port, socketOpts, ct).ConfigureAwait(false);
            }
            catch
            {
                try { s.Dispose(); } catch { /* idempotent */ }
                throw;
            }
        }
        else
        {
            await client.ConnectAsync(o.SmtpHost, o.Port, socketOpts, ct).ConfigureAwait(false);
        }

        var auth = "Autentizace netestována";
        if (o.UseAuthentication)
        {
            if (o.AuthMethod == SmtpAuthMethod.Auto)
                await client.AuthenticateAsync(o.Username, o.Password, ct).ConfigureAwait(false);
            else
            {
                var sasl = AuthMethodHelper.CreateSasl(o.AuthMethod, o.Username, o.Password);
                await client.AuthenticateAsync(sasl, ct).ConfigureAwait(false);
            }
            auth = "Autentizace OK";
        }

        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return $"SMTP spojení OK\r\nServer: {o.SmtpHost}:{o.Port}\r\nZabezpečení: {o.Security}\r\nIgnoreCertErrors: {o.IgnoreCertificateErrors}\r\nProxy: {proxyLabel}\r\nSource IP: {(string.IsNullOrEmpty(o.SourceIp) ? "výchozí" : o.SourceIp)}\r\nmTLS: {(cert != null ? "ano" : "ne")}\r\nAuth method: {o.AuthMethod}\r\n{auth}";
    }

    internal static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None
    };
}
