﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ.Events;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EasyNetQ.Producer
{
    /// <summary>
    /// Handles publisher confirms.
    /// http://www.rabbitmq.com/blog/2011/02/10/introducing-publisher-confirms/
    /// http://www.rabbitmq.com/confirms.html
    /// 
    /// Note, this class is designed to be called sequentially from a single thread. It is NOT
    /// thread safe.
    /// </summary>
    public class PublisherConfirms : IPublisherConfirms
    {
        private readonly IConnectionConfiguration configuration;
        private readonly IEasyNetQLogger logger;
        private readonly IDictionary<ulong, ConfirmActions> dictionary = 
            new ConcurrentDictionary<ulong, ConfirmActions>();

        private IModel cachedModel;
        private readonly int timeoutSeconds;

        public PublisherConfirms(IConnectionConfiguration configuration, IEasyNetQLogger logger, IEventBus eventBus)
        {
            Preconditions.CheckNotNull(configuration, "configuration");
            Preconditions.CheckNotNull(logger, "logger");
            Preconditions.CheckNotNull(eventBus, "eventBus");

            this.configuration = configuration;
            timeoutSeconds = configuration.Timeout;
            this.logger = logger;

            eventBus.Subscribe<PublishChannelCreatedEvent>(OnPublishChannelCreated);
        }

        private void OnPublishChannelCreated(PublishChannelCreatedEvent publishChannelCreatedEvent)
        {
            Preconditions.CheckNotNull(publishChannelCreatedEvent.Channel, "model");

            var outstandingConfirms = new List<ConfirmActions>(dictionary.Values);

            foreach (var outstandingConfirm in outstandingConfirms)
            {
                outstandingConfirm.Cancel();
                PublishWithConfirmInternal(
                    publishChannelCreatedEvent.Channel,
                    outstandingConfirm.PublishAction,
                    outstandingConfirm.TaskCompletionSource);
            }
            
        }

        private void SetModel(IModel model)
        {
            // we only need to set up the channel once, but the persistent channel can change
            // the IModel instance underneath us, so check on each publish.
            if (cachedModel == model) return;

            if (cachedModel != null)
            {
                // the old model has been closed and we're now using a new model, so remove
                // any existing callback entries in the dictionary
                dictionary.Clear();

                cachedModel.BasicAcks -= ModelOnBasicAcks;
                cachedModel.BasicNacks -= ModelOnBasicNacks;
            }

            cachedModel = model;

            // switch channel to confirms mode.
            model.ConfirmSelect();

            model.BasicAcks += ModelOnBasicAcks;
            model.BasicNacks += ModelOnBasicNacks;
        }

        private void ModelOnBasicNacks(IModel model, BasicNackEventArgs args)
        {
            HandleConfirm(args.DeliveryTag, args.Multiple, x => x.OnNack());
        }

        private void ModelOnBasicAcks(IModel model, BasicAckEventArgs args)
        {
            HandleConfirm(args.DeliveryTag, args.Multiple, x => x.OnAck());
        }

        private void HandleConfirm(ulong sequenceNumber, bool multiple, Action<ConfirmActions> confirmAction)
        {
            if(multiple)
            {
                //Console.Out.WriteLine(">>>>>>>>>>>>> multiple {0}", sequenceNumber);
                foreach (var match in dictionary.Keys.Where(key => key <= sequenceNumber))
                {
                    confirmAction(dictionary[match]);
                    dictionary.Remove(match);
                }
            }
            else
            {
                if(dictionary.ContainsKey(sequenceNumber))
                {
                    confirmAction(dictionary[sequenceNumber]);
                    dictionary.Remove(sequenceNumber);
                }
            }

        }

        public Task PublishWithConfirm(IModel model, Action<IModel> publishAction)
        {
            var tcs = new TaskCompletionSource<NullStruct>();
            return PublishWithConfirmInternal(model, publishAction, tcs);
        }

        private Task PublishWithConfirmInternal(IModel model, Action<IModel> publishAction, TaskCompletionSource<NullStruct> tcs)
        {
            if (!configuration.PublisherConfirms)
            {
                return ExecutePublishActionDirectly(model, publishAction);
            }

            SetModel(model);

            var sequenceNumber = model.NextPublishSeqNo;

            // make sure we don't get a race condition when the ack/nack and timeout
            // occur at the same time.
            var responseLock = new object();

            // there are three possible outcomes from a publish with confirms:

            // 1. We time out without an ack or a nack being received. Throw a timeout exception.
            var timer = new Timer(state =>
                {
                    lock (responseLock)
                    {
                        if (tcs.Task.IsCompleted) return;
                        logger.ErrorWrite("Publish timed out. Sequence number: {0}", sequenceNumber);
                        dictionary.Remove(sequenceNumber);
                        tcs.SetException(new TimeoutException(string.Format(
                            "Publisher confirms timed out after {0} seconds " + 
                            "waiting for ACK or NACK from sequence number {1}",
                            timeoutSeconds, sequenceNumber)));
                    }
                }, null, timeoutSeconds * 1000, Timeout.Infinite);

            dictionary.Add(sequenceNumber, new ConfirmActions
                {
                    // 2. An ack is received, so complete normally.
                    OnAck = () =>
                        {
                            lock (responseLock)
                            {
                                if (tcs.Task.IsCompleted) return;
                                timer.Dispose();
                                tcs.SetResult(new NullStruct());
                            }
                        },

                    // 3. A Nack is received, so throw an exception.
                    OnNack = () =>
                        {
                            lock (responseLock)
                            {
                                if (tcs.Task.IsCompleted) return;
                                timer.Dispose();
                                logger.ErrorWrite("Publish was nacked by broker. Sequence number: {0}", sequenceNumber);
                                tcs.SetException(new PublishNackedException(string.Format(
                                    "Broker has signalled that publish {0} was unsuccessful", sequenceNumber)));
                            }
                        },

                    Cancel = () => timer.Dispose(),

                    PublishAction = publishAction,

                    TaskCompletionSource = tcs
                });

            publishAction(model);

            return tcs.Task;
        }

        private Task ExecutePublishActionDirectly(IModel model, Action<IModel> publishAction)
        {
            var tcs = new TaskCompletionSource<NullStruct>();
            publishAction(model);
            tcs.SetResult(new NullStruct());
            return tcs.Task;
        }

        private struct NullStruct { }

        private class ConfirmActions
        {
            public Action OnAck { get; set; }
            public Action OnNack { get; set; }
            public Action Cancel { get; set; }
            public Action<IModel> PublishAction { get; set; }
            public TaskCompletionSource<NullStruct> TaskCompletionSource { get; set; } 
        }
    }
}