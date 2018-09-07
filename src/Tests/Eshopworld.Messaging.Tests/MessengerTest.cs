﻿using Eshopworld.Core;
using Eshopworld.Messaging;
using Eshopworld.Messaging.Tests;
using Eshopworld.Tests.Core;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

[SuppressMessage(
    category: "Usage",
    checkId: "xUnit1026:Theory methods should use all of their parameters",
    Justification = "They are discards to inject member data as a generics type parameter",
    Scope = "type")]
// ReSharper disable once CheckNamespace
// ReSharper disable AccessToDisposedClosure
[Collection(nameof(AzureServiceBusCollection))]
public class MessengerTest
{
    internal readonly AzureServiceBusFixture ServiceBusFixture;

    public MessengerTest(AzureServiceBusFixture serviceBusFixture)
    {
        ServiceBusFixture = serviceBusFixture;
    }

    [Fact, IsIntegration]
    public async Task Test_SendCreatesTheQueue()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (IMessenger msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            await msn.Send(new TestMessage());
            ServiceBusFixture.ServiceBusNamespace.AssertSingleQueueExists(typeof(TestMessage));
        }
    }

    [Fact, IsIntegration]
    public async Task Test_ReceiveCreatesTheQueue()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (IMessenger msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            msn.Receive<TestMessage>(__ => { });
            ServiceBusFixture.ServiceBusNamespace.AssertSingleQueueExists(typeof(TestMessage));
        }
    }

    [Fact, IsIntegration]
    public async Task Test_SendingRandomMessages()
    {
        var sendCount = new Random().Next(1, 10);

        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        var messages = new List<TestMessage>();
        for (var i = 0; i < sendCount; i++) { messages.Add(new TestMessage()); }

        using (IMessenger msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            foreach (var message in messages)
            {
                await msn.Send(message);
            }

            await Task.Delay(TimeSpan.FromSeconds(5)); // wait 5 seconds to flush out all the messages

            var receiver = new MessageReceiver(ServiceBusFixture.ConfigSettings.ConnectionString, typeof(TestMessage).GetEntityName(), ReceiveMode.ReceiveAndDelete, null, sendCount);
            var rMessages = (await receiver.ReadBatchAsync<TestMessage>(sendCount)).ToList();

            rMessages.Should().BeEquivalentTo(messages);
        }
    }

    [Fact, IsIntegration]
    public async Task Test_ReceivingRandomMessages()
    {
        var receiveCount = new Random().Next(1, 10);
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        var rMessages = new List<TestMessage>();
        var messages = new List<TestMessage>();
        for (var i = 0; i < receiveCount; i++) { messages.Add(new TestMessage()); }

        using (var ts = new CancellationTokenSource())
        using (IMessenger msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            // We need to create the messenger before sending the messages to avoid writing unecessary code to create the queue
            // during the test. Receive will create the queue automatically. This breaks the AAA pattern by design.
            // This test flow also ensures that receive will actually create the queue properly.
            msn.Receive<TestMessage>(
                m =>
                {
                    rMessages.Add(m);
                    if (rMessages.Count == messages.Count) ts.Cancel(); // kill switch
                });

            var sender = new MessageSender(ServiceBusFixture.ConfigSettings.ConnectionString, typeof(TestMessage).GetEntityName());
            await sender.WriteBatchAsync(messages);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ts.Token);
            }
            catch (TaskCanceledException) { /* soak the kill switch */ }

            rMessages.Should().BeEquivalentTo(messages);
        }
    }

    [Fact, IsIntegration]
    public async Task Test_LockMessage_ForFiveMinutes()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (IMessenger msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            await msn.Send(new TestMessage());

            var message = default(TestMessage);
            msn.Receive<TestMessage>(
                async m =>
                {
                    message = m;
                    await msn.Lock(m);
                });

            await Task.Delay(TimeSpan.FromMinutes(5));

            message.Should().NotBeNull();
            await msn.Complete(message); // If this throws, Lock failed
        }
    }

    [Fact, IsIntegration]
    public async Task Test_LockAbandon_MessageFlow()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (var ts = new CancellationTokenSource())
        using (var msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            await msn.Send(new TestMessage());

            msn.Receive<TestMessage>(
                async m =>
                {
                    await msn.Lock(m);
                    msn.CancelReceive<TestMessage>();
                    await Task.Delay(TimeSpan.FromSeconds(5), ts.Token);
                    await msn.Abandon(m);

                    ts.Cancel(); // kill switch
                });

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ts.Token);
            }
            catch (TaskCanceledException) { /* soak the kill switch */ }

            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).Messages.Should().BeEmpty();
            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).LockTimers.Should().BeEmpty();
        }
    }

    [Fact, IsIntegration]
    public async Task Test_LockComplete_MessageFlow()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (var ts = new CancellationTokenSource())
        using (var msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            await msn.Send(new TestMessage());

            msn.Receive<TestMessage>(
                async m =>
                {
                    await msn.Lock(m);
                    await Task.Delay(TimeSpan.FromSeconds(5), ts.Token);
                    await msn.Complete(m);

                    ts.Cancel(); // kill switch
                });

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ts.Token);
            }
            catch (TaskCanceledException) { /* soak the kill switch */ }

            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).Messages.Should().BeEmpty();
            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).LockTimers.Should().BeEmpty();
        }
    }

    [Fact, IsIntegration]
    public async Task Test_LockError_MessageFlow()
    {
        await ServiceBusFixture.ServiceBusNamespace.ScorchNamespace();

        using (var ts = new CancellationTokenSource())
        using (var msn = new Messenger(ServiceBusFixture.ConfigSettings.ConnectionString, ServiceBusFixture.ConfigSettings.SubscriptionId))
        {
            var message = new TestMessage();
            await msn.Send(message);

            msn.Receive<TestMessage>(
                async m =>
                {
                    await msn.Lock(m);
                    await Task.Delay(TimeSpan.FromSeconds(5), ts.Token);
                    await msn.Error(m);

                    ts.Cancel(); // kill switch
                });

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ts.Token);
            }
            catch (TaskCanceledException) { /* soak the kill switch */ }

            var receiver = new MessageReceiver(ServiceBusFixture.ConfigSettings.ConnectionString, EntityNameHelper.FormatDeadLetterPath(typeof(TestMessage).GetEntityName()), ReceiveMode.ReceiveAndDelete);
            var rMessage = (await receiver.ReadBatchAsync<TestMessage>(1)).FirstOrDefault();

            rMessage.Should().NotBeNull();
            rMessage.Should().BeEquivalentTo(message);

            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).Messages.Should().BeEmpty();
            ((QueueAdapter<TestMessage>)msn.QueueAdapters[typeof(TestMessage)]).LockTimers.Should().BeEmpty();
        }
    }
}

/// <summary>
/// A convenient way to generate random Lorem text around a test message implementation.
/// </summary>
public class TestMessage : IEquatable<TestMessage>
{
    private static readonly Random Rng = new Random();

    public string Name { get; set; }

    public string Stuff { get; set; }

    public float Price { get; set; }

    public TestMessage()
    {
        Name = Lorem.GetSentence();
        Stuff = Lorem.GetParagraph();
        Price = Rng.Next(100);
    }

    public TestMessage(string name, string stuff, float price)
    {
        Name = name;
        Stuff = stuff;
        Price = price;
    }

    public bool Equals(TestMessage other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Name, other.Name) &&
               string.Equals(Stuff, other.Stuff) &&
               Price.Equals(other.Price);
    }
}
