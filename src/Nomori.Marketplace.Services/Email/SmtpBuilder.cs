using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Email;

namespace Nomori.Marketplace.Services.Email;

public sealed class SmtpBuilder(IOptions<EmailOptions> emailOptions) : ISmtpBuilder
{
    public async Task<SmtpClient> BuildAsync(CancellationToken cancellationToken)
    {
        var options = emailOptions.Value;
        if (!options.Enabled)
            throw new InvalidOperationException("Email delivery is disabled.");

        if (string.IsNullOrWhiteSpace(options.SmtpHost))
            throw new InvalidOperationException("Email:SmtpHost must be configured when email delivery is enabled.");
        if (string.IsNullOrWhiteSpace(options.FromAddress))
            throw new InvalidOperationException("Email:FromAddress must be configured when email delivery is enabled.");

        var client = new SmtpClient();
        try
        {
            var socketOptions = options.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : options.UseStartTls
                    ? SecureSocketOptions.StartTls
                    : SecureSocketOptions.None;

            await client.ConnectAsync(options.SmtpHost, options.SmtpPort, socketOptions, cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.Username))
                await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
