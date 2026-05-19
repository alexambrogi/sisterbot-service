using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace SisterBotService;

internal class MailSender(string tenantId, string clientId, string clientSecret)
{
    #region Internal Methods

    internal async Task SendMail(string? from, string? to, string? subject, string? message)
    {

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var scopes = new[] { "https://graph.microsoft.com/.default" };

        // using Azure.Identity;
        var options = new TokenCredentialOptions
        {
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
        };

        var clientSecretCredential = new ClientSecretCredential(
            tenantId, clientId, clientSecret, options);

        var graphClient = new GraphServiceClient(clientSecretCredential, scopes);
        var requestBody = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = subject.Trim(),
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = message.Trim()
                },
                ToRecipients =
                [
                    new Recipient
                        {
                            EmailAddress = new EmailAddress
                            {
                                Address = to.Trim()
                            }
                        }
                ]
            },
            SaveToSentItems = false
        };

        await graphClient.Users[from].SendMail.PostAsync(requestBody);
    }

    #endregion Internal Methods

}