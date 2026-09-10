using System.ComponentModel.DataAnnotations;

namespace MailSenderEngine.Configuration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    [Required]
    public string User { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    public string FromAddress { get; init; } = string.Empty;

    public string FromDisplayName { get; init; } = "MailSenderEngine";
}
