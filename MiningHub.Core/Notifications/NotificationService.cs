﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MiningHub.Core.Configuration;
using MiningHub.Core.Contracts;
using MiningHub.Core.Messaging;
using MiningHub.Core.Notifications.Messages;
using Newtonsoft.Json;
using NLog;

namespace MiningHub.Core.Notifications
{
    public class NotificationService
    {
        public NotificationService(
            ClusterConfig clusterConfig,
            JsonSerializerSettings serializerSettings,
            IMessageBus messageBus)
        {
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));
            Contract.RequiresNonNull(messageBus, nameof(messageBus)); 

            this.clusterConfig = clusterConfig;
            this.serializerSettings = serializerSettings;

            poolConfigs = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

            adminEmail = clusterConfig.Notifications?.Admin?.EmailAddress;
            //adminPhone = null;

            if (clusterConfig.Notifications?.Enabled == true)
            {
                queue = new BlockingCollection<QueuedNotification>();

                queueSub = queue.GetConsumingEnumerable()
                    .ToObservable(TaskPoolScheduler.Default)
                    .Select(notification => Observable.FromAsync(() => SendNotificationAsync(notification)))
                    .Concat()
                    .Subscribe();

                messageBus.Listen<BlockNotification>()
                    .Subscribe(x =>
                    {
                        queue?.Add(new QueuedNotification
                        {
                            Category = NotificationCategory.Block,
                            PoolId = x.PoolId,
                            Subject = "Block Notification",
                            Msg = $"Pool {x.PoolId} found block candidate {x.BlockHeight}"
                        });
                    });
            }
        }

        private readonly ILogger logger = LogManager.GetCurrentClassLogger();
        private readonly ClusterConfig clusterConfig;
        private readonly JsonSerializerSettings serializerSettings;
        private readonly Dictionary<string, PoolConfig> poolConfigs;
        private readonly string adminEmail;
        private readonly BlockingCollection<QueuedNotification> queue;
        private readonly Regex regexStripHtml = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private IDisposable queueSub;

        private readonly HttpClient httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
        });

        enum NotificationCategory
        {
            Admin,
            Block,
            PaymentSuccess,
            PaymentFailure,
        }

        struct QueuedNotification
        {
            public NotificationCategory Category;
            public string PoolId;
            public string Subject;
            public string Msg;
        }

        #region API-Surface

        public void NotifyPaymentSuccess(string poolId, decimal amount, int recpientsCount, string txInfo, decimal? txFee)
        {
            queue?.Add(new QueuedNotification
            {
                Category = NotificationCategory.PaymentSuccess,
                PoolId = poolId,
                Subject = "Payout Success Notification",
                Msg = $"Paid {FormatAmount(amount, poolId)} from pool {poolId} to {recpientsCount} recipients in Transaction(s) {txInfo}."
            });
        }

        public void NotifyPaymentFailure(string poolId, decimal amount, string message)
        {
            queue?.Add(new QueuedNotification
            {
                Category = NotificationCategory.PaymentFailure,
                PoolId = poolId,
                Subject = "Payout Failure Notification",
                Msg = $"Failed to pay out {amount} {poolConfigs[poolId].Coin.Type} from pool {poolId}: {message}"
            });
        }

        public void NotifyAdmin(string subject, string message)
        {
            queue?.Add(new QueuedNotification
            {
                Category = NotificationCategory.Admin,
                Subject = subject,
                Msg = message
            });
        }

        #endregion // API-Surface

        public string FormatAmount(decimal amount, string poolId)
        {
            return $"{amount:0.#####} {poolConfigs[poolId].Coin.Type}";
        }

        private async Task SendNotificationAsync(QueuedNotification notification)
        {
            logger.Debug(() => $"SendNotificationAsync");

            try
            {
                var poolConfig = !string.IsNullOrEmpty(notification.PoolId) ? poolConfigs[notification.PoolId] : null;

                switch (notification.Category)
                {
                    case NotificationCategory.Admin:
                        if (clusterConfig.Notifications?.Admin?.Enabled == true)
                            await SendEmailAsync(adminEmail, notification.Subject, notification.Msg);
                        break;

                    case NotificationCategory.Block:
                        // notify admin
                        if (clusterConfig.Notifications?.Admin?.Enabled == true &&
                            clusterConfig.Notifications?.Admin?.NotifyBlockFound == true)
                            await SendEmailAsync(adminEmail, notification.Subject, notification.Msg);

                        break;

                    case NotificationCategory.PaymentSuccess:
                        // notify admin
                        if (clusterConfig.Notifications?.Admin?.Enabled == true &&
                            clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess == true)
                            await SendEmailAsync(adminEmail, notification.Subject, notification.Msg);

                        break;

                    case NotificationCategory.PaymentFailure:
                        // notify admin
                        if (clusterConfig.Notifications?.Admin?.Enabled == true &&
                            clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess == true)
                            await SendEmailAsync(adminEmail, notification.Subject, notification.Msg);
                        break;
                }
            }

            catch (Exception ex)
            {
                logger.Error(ex, $"Error sending notification");
            }
        }

        public async Task SendEmailAsync(string recipient, string subject, string body)
        {
            logger.Info(() => $"Sending '{subject.ToLower()}' email to {recipient}");

            var emailSenderConfig = clusterConfig.Notifications.Email;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(emailSenderConfig.FromName, emailSenderConfig.FromAddress));
            message.To.Add(new MailboxAddress("", recipient));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = body };

            using (var client = new SmtpClient())
            {
                await client.ConnectAsync(emailSenderConfig.Host, emailSenderConfig.Port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(emailSenderConfig.User, emailSenderConfig.Password);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
            }

            logger.Info(() => $"Sent '{subject.ToLower()}' email to {recipient}");
        }
    }
}
