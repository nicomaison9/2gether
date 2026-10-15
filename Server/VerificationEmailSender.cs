using System.Net;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using MimeKit;

namespace TwoGether.Server;

public sealed class EmailDeliveryOptions
{
    public string Delivery { get; set; } = "Smtp";
    public string PublicOrigin { get; set; } = "";
    public string PickupDirectory { get; set; } = "App_Data/Mail";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool ImplicitTls { get; set; }
    public string FromAddress { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class EmailDeliveryException(Exception innerException) : Exception("Email delivery failed.", innerException);

public sealed class VerificationEmailSender(EmailDeliveryOptions options, IWebHostEnvironment environment) : IEmailSender<AppUser>
{
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink)
    {
        var link = new Uri(new Uri(options.PublicOrigin), "/confirm-email" + new Uri(WebUtility.HtmlDecode(confirmationLink)).Query).AbsoluteUri;
        return SendAsync(email, "Confirme ton adresse e-mail pour 2gether",
            $"Bonjour !\n\nPour activer ton compte 2gether, ouvre ce lien :\n{link}\n\nCe lien expire dans une heure. Si tu n'as pas demandé ce compte, ignore ce message.",
            $"<p>Bonjour !</p><p>Pour activer ton compte 2gether, confirme ton adresse e-mail.</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Confirmer mon adresse e-mail</a></p><p>Ce lien expire dans une heure. Si tu n'as pas demandé ce compte, ignore ce message.</p>");
    }

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) => SendAsync(email, "Récupération de ton compte 2gether", $"Lien de récupération : {resetLink}", null);
    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) => SendAsync(email, "Récupération de ton compte 2gether",
        $"Bonjour !\n\nTon code de récupération 2gether est :\n{resetCode}\n\nIl expire dans une heure et ne peut être utilisé qu'une seule fois. Si tu n'as rien demandé, ignore ce message.",
        $"<p>Bonjour !</p><p>Ton code de récupération 2gether est :</p><p><strong>{WebUtility.HtmlEncode(resetCode)}</strong></p><p>Il expire dans une heure et ne peut être utilisé qu'une seule fois. Si tu n'as rien demandé, ignore ce message.</p>");

    private async Task SendAsync(string email, string subject, string text, string? html)
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("2gether", options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        try
        {
            if (options.Delivery == "PickupDirectory")
            {
                var directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.PickupDirectory));
                Directory.CreateDirectory(directory);
                await message.WriteToAsync(Path.Combine(directory, $"{Guid.NewGuid():N}.eml"));
                return;
            }

            using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await smtp.ConnectAsync(options.Host, options.Port, options.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
            if (!string.IsNullOrEmpty(options.Username)) await smtp.AuthenticateAsync(options.Username, options.Password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
        }
        catch (Exception exception) when (exception is IOException or MailKit.CommandException or MailKit.ProtocolException or System.Net.Sockets.SocketException or MailKit.Security.AuthenticationException or SslHandshakeException or System.Security.Authentication.AuthenticationException or TimeoutException)
        {
            throw new EmailDeliveryException(exception);
        }
    }
}