namespace Nomori.Marketplace.Core.Email;

public interface IEmailSender
{
    Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken);
}
