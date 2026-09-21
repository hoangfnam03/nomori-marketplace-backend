using MailKit.Net.Smtp;

namespace Nomori.Marketplace.Services.Email;

public interface ISmtpBuilder
{
    Task<SmtpClient> BuildAsync(CancellationToken cancellationToken);
}
