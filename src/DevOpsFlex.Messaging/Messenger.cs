﻿namespace DevOpsFlex.Messaging
{
    using System;
    using System.Collections.Generic;
    using System.Reactive;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading.Tasks;
    using JetBrains.Annotations;

    /// <summary>
    /// The main messenger entry point.
    /// It is recommended that only one messenger exists per application so you should always give this a singleton lifecycle.
    /// </summary>
    /// <remarks>
    /// Some checks are only valuable if you have a unique messenger class in your application. If you set it up as transient
    /// then some checks will only run against a specific instance and won't really validate the entire behaviour.
    /// </remarks>
    public class Messenger : IMessenger, IReactiveMessenger
    {
        internal readonly object Gate = new object();
        internal readonly string ConnectionString;
        internal readonly string SubscriptionId;

        internal readonly ISubject<object> MessagesIn = new Subject<object>();

        internal Dictionary<Type, IDisposable> MessageSubs = new Dictionary<Type, IDisposable>();
        internal Dictionary<Type, MessageQueueAdapter> QueueAdapters = new Dictionary<Type, MessageQueueAdapter>();

        /// <summary>
        /// Initializes a new instance of <see cref="Messenger"/>.
        /// </summary>
        /// <param name="connectionString">The Azure Service Bus connection string.</param>
        public Messenger([NotNull]string connectionString)
        {
            ConnectionString = connectionString;
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Messenger"/>.
        /// </summary>
        /// <param name="connectionString">The Azure Service Bus connection string.</param>
        /// <param name="subscriptionId">The subscription ID where the service bus namespace lives.</param>
        public Messenger([NotNull]string connectionString, [NotNull]string subscriptionId)
        {
            ConnectionString = connectionString;
            SubscriptionId = subscriptionId;
        }

        /// <summary>
        /// Sends a message.
        /// </summary>
        /// <typeparam name="T">The type of the message that we are sending.</typeparam>
        /// <param name="message">The message that we are sending.</param>
        public async Task Send<T>(T message)
            where T : class
        {
            if (!QueueAdapters.ContainsKey(typeof(T))) // double check, to avoid locking it here to keep it thread safe
            {
                SetupMessageType<T>();
            }

            await ((MessageQueueAdapter<T>)QueueAdapters[typeof(T)]).Send(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets up a call back for receiving any message of type <typeparamref name="T"/>.
        /// If you try to setup more then one callback to the same message type <typeparamref name="T"/> you'll get an <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <typeparam name="T">The type of the message that we are subscribing to receiving.</typeparam>
        /// <param name="callback">The <see cref="Action{T}"/> delegate that will be called for each message received.</param>
        /// <exception cref="InvalidOperationException">Thrown when you attempt to setup multiple callbacks against the same <typeparamref name="T"/> parameter.</exception>
        public void Receive<T>(Action<T> callback)
            where T : class
        {
            lock (Gate)
            {
                if (MessageSubs.ContainsKey(typeof(T)))
                {
                    throw new InvalidOperationException("You already added a callback to this message type. Only one callback per type is supported.");
                }

                SetupMessageType<T>().StartReading();

                MessageSubs.Add(
                    typeof(T),
                    MessagesIn.OfType<T>().Subscribe(callback));
            }
        }

        /// <summary>
        /// Stops receiving a message type by disabling the read pooling on the <see cref="MessageQueueAdapter"/>.
        /// </summary>
        /// <typeparam name="T">The type of the message that we are cancelling the receive on.</typeparam>
        public void CancelReceive<T>()
            where T : class
        {
            lock (Gate)
            {
                SetupMessageType<T>().StopReading();

                MessageSubs[typeof(T)].Dispose();
                MessageSubs.Remove(typeof(T));
            }
        }

        /// <summary>
        /// Setups up the required receive pipeline for the given message type and returns a reactive
        /// <see cref="IObservable{T}"/> that you can plug into.
        /// </summary>
        /// <typeparam name="T">The type of the message we want the reactive pipeline for.</typeparam>
        /// <returns>The typed <see cref="IObservable{T}"/> that you can plug into.</returns>
        public IObservable<T> GetObservable<T>() where T : class
        {
            SetupMessageType<T>().StartReading();

            return MessagesIn.OfType<T>().AsObservable();
        }

        /// <summary>
        /// Creates a perpetual lock on a message by continuously renewing it's lock.
        /// This is usually created at the start of a handler so that we guarantee that we still have a valid lock
        /// and we retain that lock until we finish handling the message.
        /// </summary>
        /// <param name="message">The message that we want to create the lock on.</param>
        /// <returns>The async <see cref="Task"/> wrapper</returns>
        public async Task Lock<T>(T message) where T : class
        {
            var adapter = (MessageQueueAdapter<T>)QueueAdapters[typeof(T)];
            await adapter.Lock(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Completes a message by doing the actual READ from the queue.
        /// </summary>
        /// <param name="message">The message we want to complete.</param>
        /// <returns>The async <see cref="Task"/> wrapper</returns>
        public async Task Complete<T>(T message) where T : class
        {
            var adapter = (MessageQueueAdapter<T>)QueueAdapters[typeof(T)];
            await adapter.Complete(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Abandons a message by returning it to the queue.
        /// </summary>
        /// <param name="message">The message we want to abandon.</param>
        /// <returns>The async <see cref="Task"/> wrapper</returns>
        public async Task Abandon<T>(T message) where T : class
        {
            var adapter = (MessageQueueAdapter<T>) QueueAdapters[typeof(T)];
            await adapter.Abandon(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Errors a message by moving it specifically to the error queue.
        /// </summary>
        /// <param name="message">The message that we want to move to the error queue.</param>
        /// <returns>The async <see cref="Task"/> wrapper</returns>
        public async Task Error<T>(T message) where T : class
        {
            var adapter = (MessageQueueAdapter<T>)QueueAdapters[typeof(T)];
            await adapter.Error(message).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets the messenger up for either sending or receiving a specific message of type <typeparamref name="T"/>.
        /// This will create the <see cref="MessageQueueAdapter"/> but will not set it up for reading the queue.
        /// </summary>
        /// <typeparam name="T">The type of the message we are setting up.</typeparam>
        /// <returns>The message queue wrapper.</returns>
        internal MessageQueueAdapter<T> SetupMessageType<T>() where T : class
        {
            lock (Gate)
            {
                if (!QueueAdapters.ContainsKey(typeof(T)))
                {
                    var queue = new MessageQueueAdapter<T>(ConnectionString, SubscriptionId, MessagesIn.AsObserver());
                    QueueAdapters.Add(typeof(T), queue);
                }
            }

            return (MessageQueueAdapter<T>)QueueAdapters[typeof(T)];
        }

        /// <summary>
        /// Provides a mechanism for releasing resources.
        /// </summary>
        public void Dispose()
        {
            MessageSubs.Release();
            MessageSubs = null;

            QueueAdapters.Release();
            QueueAdapters = null;
        }
    }
}
