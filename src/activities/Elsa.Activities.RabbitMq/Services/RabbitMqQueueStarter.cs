using Elsa.Activities.RabbitMq.Configuration;
using Elsa.Services;
using Elsa.Services.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Activities.RabbitMq.Bookmarks;

namespace Elsa.Activities.RabbitMq.Services
{
    public class RabbitMqQueueStarter : IRabbitMqQueueStarter
    {
        private readonly SemaphoreSlim _semaphore = new(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly ICollection<Worker> _workers;
        private readonly IMessageReceiverClientFactory _messageReceiverClientFactory;
        private readonly ILogger _logger;
        private readonly IBookmarkSerializer _bookmarkSerializer;

        public RabbitMqQueueStarter(
            IMessageReceiverClientFactory messageReceiverClientFactory,
            IServiceScopeFactory scopeFactory, 
            IServiceProvider serviceProvider, 
            ILogger<RabbitMqQueueStarter> logger,
            IBookmarkSerializer bookmarkSerializer)
        {
            _messageReceiverClientFactory = messageReceiverClientFactory;
            _scopeFactory = scopeFactory;
            _serviceProvider = serviceProvider;
            _logger = logger;
            _bookmarkSerializer = bookmarkSerializer;
            _workers = new List<Worker>();
        }

        public async Task CreateWorkersAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);

            try
            {
                await DisposeExistingWorkersAsync();

                var receiverConfigs = (await GetConfigurationsAsync<RabbitMqMessageReceived>(null, cancellationToken).ToListAsync(cancellationToken)).GroupBy(c => c.GetHashCode()).Select(x => x.First());

                foreach (var config in receiverConfigs)
                {
                    try
                    {
                        _workers.Add(await CreateWorkerAsync(config, cancellationToken));
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning(e, "Failed to create a receiver for routing key {RoutingKey}", config.RoutingKey);
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<Worker> CreateWorkerAsync(RabbitMqBusConfiguration config, CancellationToken cancellationToken = default)
        {
            var receiver = await _messageReceiverClientFactory.GetReceiverAsync(config, cancellationToken);
            return ActivatorUtilities.CreateInstance<Worker>(_serviceProvider, (Func<IClient, Task>)DisposeWorkerAsync, receiver);
        }

        private async Task DisposeWorkerAsync(IClient messageReceiver) => await _messageReceiverClientFactory.DisposeReceiverAsync(messageReceiver);

        public async IAsyncEnumerable<RabbitMqBusConfiguration> GetConfigurationsAsync<T>(Func<IWorkflowBlueprint, bool>? predicate, [EnumeratorCancellation] CancellationToken cancellationToken) where T : IRabbitMqActivity
        {
            using var scope = _scopeFactory.CreateScope();
            var triggerFinder = scope.ServiceProvider.GetRequiredService<ITriggerFinder>();
            var triggers = await triggerFinder.FindTriggersByTypeAsync<MessageReceivedBookmark>(cancellationToken: cancellationToken);

            foreach (var trigger in triggers)
            {
                var bookmark =_bookmarkSerializer.Deserialize<MessageReceivedBookmark>(trigger.Model);
                
                var connectionString = bookmark.ConnectionString;
                var exchangeName = bookmark.ExchangeName;
                var routingKey = bookmark.RoutingKey;
                var headers = bookmark.Headers;

                yield return new RabbitMqBusConfiguration(connectionString!, exchangeName!, routingKey!, headers);
            }
        }
        
        private async Task DisposeExistingWorkersAsync()
        {
            foreach (var worker in _workers.ToList())
            {
                await worker.DisposeAsync();
                _workers.Remove(worker);
            }
        }
    }
}