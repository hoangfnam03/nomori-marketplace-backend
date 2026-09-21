using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;
using Nomori.Marketplace.Core.Email;

namespace Nomori.Marketplace.Services.Email;

public sealed class SmtpEmailSender(
    ISmtpBuilder smtpBuilder,
    IOptions<EmailOptions> emailOptions) : IEmailSender
{
    public async Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = emailOptions.Value;
        if (!options.Enabled)
            return;

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(message.ToAddress));
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        }.ToMessageBody();

        using var smtpClient = await smtpBuilder.BuildAsync(cancellationToken);
        try
        {
            await smtpClient.SendAsync(mimeMessage, cancellationToken);
        }
        finally
        {
            if (smtpClient.IsConnected)
                await smtpClient.DisconnectAsync(true, CancellationToken.None);
        }
    }
}
