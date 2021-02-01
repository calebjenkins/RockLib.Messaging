﻿using Confluent.Kafka;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RockLib.Messaging.Kafka
{
    /// <summary>
    /// An implementation of <see cref="IReceiver"/> that receives messages from Kafka.
    /// </summary>
    public class KafkaReceiver : Receiver
    {
        private readonly Lazy<Thread> _pollingThread;
        private readonly Lazy<IConsumer<string, byte[]>> _consumer;
        private readonly CancellationTokenSource _disposeSource = new CancellationTokenSource();
        private readonly BlockingCollection<Task> _trackingCollection = new BlockingCollection<Task>();
        private readonly Lazy<Thread> _trackingThread;

        private bool _stopped;
        private bool _disposed;
        private bool? _connected;

        /// <summary>
        /// Initializes a new instance of the <see cref="KafkaReceiver"/> class.
        /// </summary>
        /// <param name="name">The name of the receiver.</param>
        /// <param name="topic">
        /// The topic to subscribe to. A regex can be specified to subscribe to the set of
        /// all matching topics (which is updated as topics are added / removed from the
        /// cluster). A regex must be front anchored to be recognized as a regex. e.g. ^myregex
        /// </param>
        /// <param name="groupId">
        /// Client group id string. All clients sharing the same group.id belong to the same group.
        /// </param>
        /// <param name="bootstrapServers">
        /// List of brokers as a CSV list of broker host or host:port.
        /// </param>
        /// <param name="enableAutoOffsetStore">
        /// Whether to automatically store offset of last message provided to application.
        /// </param>
        /// <param name="autoOffsetReset">
        /// Action to take when there is no initial offset in offset store or the desired
        /// offset is out of range: 'smallest','earliest' - automatically reset the offset
        /// to the smallest offset, 'largest','latest' - automatically reset the offset to
        /// the largest offset, 'error' - trigger an error which is retrieved by consuming
        /// messages and checking 'message->err'.
        /// </param>
        public KafkaReceiver(string name, string topic, string groupId, string bootstrapServers,
            bool enableAutoOffsetStore = false, AutoOffsetReset autoOffsetReset = Confluent.Kafka.AutoOffsetReset.Latest)
            : base(name)
        {
            Topic = topic ?? throw new ArgumentNullException(nameof(topic));
            GroupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
            BootstrapServers = bootstrapServers ?? throw new ArgumentNullException(nameof(bootstrapServers));
            EnableAutoOffsetStore = enableAutoOffsetStore;
            AutoOffsetReset = autoOffsetReset;

            var config = GetConsumerConfig(groupId, bootstrapServers, enableAutoOffsetStore, autoOffsetReset);
            var builder = new ConsumerBuilder<string, byte[]>(config);
            builder.SetErrorHandler(OnError);

            _consumer = new Lazy<IConsumer<string, byte[]>>(() => builder.Build());

            _pollingThread = new Lazy<Thread>(() => new Thread(PollForMessages) { IsBackground = true });
            _trackingThread = new Lazy<Thread>(() => new Thread(TrackMessageHandling) { IsBackground = true });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KafkaReceiver"/> class.
        /// </summary>
        /// <param name="name">The name of the receiver.</param>
        /// <param name="topic">
        /// The topic to subscribe to. A regex can be specified to subscribe to the set of
        /// all matching topics (which is updated as topics are added / removed from the
        /// cluster). A regex must be front anchored to be recognized as a regex. e.g. ^myregex
        /// </param>
        /// <param name="consumerConfig">The configuration used in creation of the Kafka consumer.</param>
        public KafkaReceiver(string name, string topic, ConsumerConfig consumerConfig)
            : base(name)
        {
            if (consumerConfig is null)
                throw new ArgumentNullException(nameof(consumerConfig));

            if (consumerConfig.EnableAutoCommit is false)
                throw new ArgumentOutOfRangeException(nameof(consumerConfig), "The 'EnableAutoCommit' setting must be true.");

            Topic = topic ?? throw new ArgumentNullException(nameof(topic));
            GroupId = consumerConfig.GroupId;
            BootstrapServers = consumerConfig.BootstrapServers;
            EnableAutoOffsetStore = consumerConfig.EnableAutoOffsetStore;
            AutoOffsetReset = consumerConfig.AutoOffsetReset;

            var builder = new ConsumerBuilder<string, byte[]>(consumerConfig);
            builder.SetErrorHandler(OnError);

            _consumer = new Lazy<IConsumer<string, byte[]>>(() => builder.Build());

            _pollingThread = new Lazy<Thread>(() => new Thread(PollForMessages) { IsBackground = true });
            _trackingThread = new Lazy<Thread>(() => new Thread(TrackMessageHandling) { IsBackground = true });
        }

        /// <summary>
        /// Gets the topic to subscribe to.
        /// </summary>
        public string Topic { get; }

        /// <summary>
        /// Client group id string. All clients sharing the same group.id belong to the same group.
        /// </summary>
        public string GroupId { get; }

        /// <summary>
        /// List of brokers as a CSV list of broker host or host:port.
        /// </summary>
        public string BootstrapServers { get; }

        /// <summary>
        /// Whether to automatically store offset of last message provided to application.
        /// </summary>
        public bool? EnableAutoOffsetStore { get; }

        /// <summary>
        /// Action to take when there is no initial offset in offset store or the desired
        /// offset is out of range: 'smallest','earliest' - automatically reset the offset
        /// to the smallest offset, 'largest','latest' - automatically reset the offset to
        /// the largest offset, 'error' - trigger an error which is retrieved by consuming
        /// messages and checking 'message->err'.
        /// </summary>
        public AutoOffsetReset? AutoOffsetReset { get; }

        /// <summary>
        /// Gets the <see cref="IConsumer{TKey, TValue}" /> for this instance of <see cref="KafkaReceiver"/>.
        /// </summary>
        public IConsumer<string, byte[]> Consumer => _consumer.Value;

        /// <summary>
        /// Starts the background threads and subscribes to the topic.
        /// </summary>
        protected override void Start()
        {
            _trackingThread.Value.Start();
            _consumer.Value.Subscribe(Topic);
            _pollingThread.Value.Start();
        }

        /// <summary>
        /// Stops polling for messages, waits for current messages to be handled, then
        /// closes and disposes the consumer.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            _stopped = true;
            _disposeSource.Cancel();

            if (_pollingThread.IsValueCreated)
                _pollingThread.Value.Join();

            if (_trackingThread.IsValueCreated)
                _trackingThread.Value.Join();

            if (_consumer.IsValueCreated)
            {
                _consumer.Value.Close();
                _consumer.Value.Dispose();
            }

            base.Dispose(disposing);
        }

        private void PollForMessages()
        {
            while (!_stopped)
            {
                try
                {
                    var result = _consumer.Value.Consume(_disposeSource.Token);
                    var message = new KafkaReceiverMessage(_consumer.Value, result, EnableAutoOffsetStore ?? false);

                    if (_connected != true)
                    {
                        _connected = true;
                        OnConnected();
                    }

                    var task = MessageHandler.OnMessageReceivedAsync(this, message);
                    _trackingCollection.Add(task);
                }
                catch (OperationCanceledException) when (_disposeSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    OnError("Error in polling loop.", ex);
                    // TODO: Delay the loop?
                }
            }
        }

        private void TrackMessageHandling()
        {
            foreach (var task in _trackingCollection)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    OnError("Error while tracking message handler task.", ex);
                }
            }
        }

        private void OnError(IConsumer<string, byte[]> consumer, Error error)
        {
            OnError(error.Reason, new KafkaException(error) { Data = { ["kafka_consumer"] = consumer } });

            if (_connected != false)
            {
                OnDisconnected(error.Reason);
                _connected = false;
            }
        }

        internal static ConsumerConfig GetConsumerConfig(string groupId, string bootstrapServers, bool enableAutoOffsetStore, AutoOffsetReset autoOffsetReset) =>
            new ConsumerConfig
            {
                GroupId = groupId,
                BootstrapServers = bootstrapServers,
                EnableAutoOffsetStore = enableAutoOffsetStore,
                AutoOffsetReset = autoOffsetReset
            };
    }
}
