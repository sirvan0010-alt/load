using MailKit.Security;

namespace MailLoadTester;

public static class AuthMethodHelper
{
    /// <summary>
    /// Pro OAuth2 se do parametru "pass" očekává hotový access token
    /// (tenhle nástroj token sám nezískává — to je mimo rozsah SMTP load testeru).
    /// </summary>
    public static SaslMechanism CreateSasl(SmtpAuthMethod method, string user, string pass)
    {
        return method switch
        {
            SmtpAuthMethod.Plain => new SaslMechanismPlain(user, pass),
            SmtpAuthMethod.Login => new SaslMechanismLogin(user, pass),
            SmtpAuthMethod.CramMd5 => new SaslMechanismCramMd5(user, pass),
            SmtpAuthMethod.ScramSha1 => new SaslMechanismScramSha1(user, pass),
            SmtpAuthMethod.Ntlm => new SaslMechanismNtlm(user, pass),
            SmtpAuthMethod.OAuth2 => new SaslMechanismOAuth2(user, pass),
            _ => new SaslMechanismPlain(user, pass) // Auto fallback
        };
    }
}
