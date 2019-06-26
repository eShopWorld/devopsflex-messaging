﻿using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Azure.Management.ServiceBus.Fluent;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Newtonsoft.Json;

namespace Eshopworld.Messaging
{
    /// <summary>
    /// Generics based message queue router from <see cref="IObservable{T}"/> through to the <see cref="QueueClient"/>.
    /// </summary>
    /// <typeparam name="T">The type of the message that we are routing.</typeparam>
    internal sealed class QueueAdapter<T> : ServiceBusAdapter<T>
        where T : class
    {
        internal readonly string ConnectionString;
        internal readonly Messenger Messenger;
        internal readonly IQueue AzureQueue;

        internal MessageSender Sender;

        /// <summary>
        /// Initializes a new instance of <see cref="QueueAdapter{T}"/>.
        /// </summary>
        /// <param name="connectionString">The Azure Service Bus connection string.</param>
        /// <param name="subscriptionId">The ID of the subscription where the service bus namespace lives.</param>
        /// <param name="messagesIn">The <see cref="IObserver{IMessage}"/> used to push received messages into the pipeline.</param>
        /// <param name="batchSize">The size of the batch when reading for a queue - used as the pre-fetch parameter of the </param>
        /// <param name="messenger">The <see cref="Messenger"/> instance that created this adapter.</param>
        public QueueAdapter([NotNull]string connectionString, [NotNull]string subscriptionId, [NotNull]IObserver<T> messagesIn, int batchSize, Messenger messenger)
            : base(messagesIn, batchSize)
        {
            ConnectionString = connectionString;
            Messenger = messenger;
            AzureQueue = Messenger.GetRefreshedServiceBusNamespace().CreateQueueIfNotExists(typeof(T).GetEntityName()).Result;

            LockInSeconds = AzureQueue.LockDurationInSeconds;
            LockTickInSeconds = (long)Math.Floor(LockInSeconds * 0.8); // renew at 80% to cope with load

            RebuildReceiver();
            RebuildSender();
        }

        /// <summary>
        /// Starts pooling the queue in order to read messages in Peek Lock mode.
        /// </summary>
        internal void StartReading()
        {
            if (ReadTimer != null) return;

            ReadTimer = new Timer(
                async _ => await Read(_).ConfigureAwait(false),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Sends a single message.
        /// </summary>
        /// <param name="message">The message we want to send.</param>
        internal async Task Send([NotNull]T message)
        {
            var qMessage = new Message(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)))
            {
                ContentType = "application/json",
                Label = message.GetType().FullName
            };

            await SendPolicy.ExecuteAsync(async () =>
            {
                try
                {
                    await Sender.SendAsync(qMessage).ConfigureAwait(false);
                }
                catch
                {
                    RebuildSender();
                    throw;
                }
            });
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Receiver.CloseAsync().Wait();
                Sender.CloseAsync().Wait();

                ReadTimer?.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        protected override void RebuildReceiver()
        {
            Receiver?.CloseAsync().Wait();
            Receiver = new MessageReceiver(ConnectionString, AzureQueue.Name, ReceiveMode.PeekLock, new RetryExponential(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), 3), BatchSize);
        }

        /// <inheritdoc />
        protected override void RebuildSender()
        {
            Sender?.CloseAsync().Wait();
            Sender = new MessageSender(ConnectionString, AzureQueue.Name, new RetryExponential(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), 3));
        }
    }
}
