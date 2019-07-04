﻿namespace Microsoft.Extensions.Caching.Cosmos
{
    using System;
    using System.Collections.ObjectModel;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Distributed cache implementation over Azure Cosmos DB
    /// </summary>
    public class CosmosCache : IDistributedCache, IDisposable
    {
        private const string UseUserAgentSuffix = "Microsoft.Extensions.Caching.Cosmos";
        private const string ContainerPartitionKeyPath = "/id";
        private const int DefaultTimeToLive = 60000;

        private CosmosClient cosmosClient;
        private Container cosmosContainer;
        private readonly SemaphoreSlim connectionLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        private bool initializedClient;

        private readonly CosmosCacheOptions options;

        public CosmosCache(IOptions<CosmosCacheOptions> optionsAccessor)
        {
            if (optionsAccessor == null)
            {
                throw new ArgumentNullException(nameof(optionsAccessor));
            }

            if (string.IsNullOrEmpty(optionsAccessor.Value.DatabaseName))
            {
                throw new ArgumentNullException(nameof(optionsAccessor.Value.DatabaseName));
            }

            if (string.IsNullOrEmpty(optionsAccessor.Value.ContainerName))
            {
                throw new ArgumentNullException(nameof(optionsAccessor.Value.ContainerName));
            }

            if (optionsAccessor.Value.Configuration == null && optionsAccessor.Value.CosmosClient == null)
            {
                throw new ArgumentNullException("You need to specify either a CosmosConfiguration or an existing CosmosClient in the CosmosCacheOptions.");
            }

            this.options = optionsAccessor.Value;
        }

        public void Dispose()
        {
            if (this.initializedClient && this.cosmosClient != null)
            {
                this.cosmosClient.Dispose();
            }
        }

        public byte[] Get(string key)
        {
            return GetAsync(key).GetAwaiter().GetResult();
        }

        public async Task<byte[]> GetAsync(string key, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            await this.ConnectAsync();

            ItemResponse<CosmosCacheSession> cosmosCacheSessionResponse = await this.cosmosContainer.ReadItemAsync<CosmosCacheSession>(
                partitionKey: new PartitionKey(key),
                id: key,
                requestOptions: null,
                cancellationToken: token).ConfigureAwait(false);

            if (cosmosCacheSessionResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            try
            {
                await this.cosmosContainer.ReplaceItemAsync(
                        partitionKey: new PartitionKey(key),
                        id: key,
                        item: cosmosCacheSessionResponse.Resource,
                        requestOptions: new ItemRequestOptions()
                        {
                            IfMatchEtag = cosmosCacheSessionResponse.ETag
                        },
                        cancellationToken: token).ConfigureAwait(false);
            }
            catch (CosmosException cosmosException) 
                when (cosmosException.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Race condition on replace, we need to get the latest version of the item
                return await this.GetAsync(key, token).ConfigureAwait(false);
            }

            return cosmosCacheSessionResponse.Resource.Content;
        }

        public void Refresh(string key)
        {
            Get(key);
        }

        public Task RefreshAsync(string key, CancellationToken token = default(CancellationToken))
        {
            return GetAsync(key, token);
        }

        public void Remove(string key)
        {
            RemoveAsync(key).GetAwaiter().GetResult();
        }

        public async Task RemoveAsync(string key, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            await this.ConnectAsync();

            await this.cosmosContainer.DeleteItemAsync<CosmosCacheSession>(
                partitionKey: new PartitionKey(key),
                id: key,
                requestOptions: null,
                cancellationToken: token).ConfigureAwait(false);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            this.SetAsync(
                key,
                value,
                options).GetAwaiter().GetResult();
        }

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            await this.ConnectAsync();

            await this.cosmosContainer.UpsertItemAsync(
                partitionKey: new PartitionKey(key),
                item: CosmosCache.BuildCosmosCacheSession(
                    key,
                    value,
                    options),
                requestOptions: null,
                cancellationToken: token).ConfigureAwait(false);
        }

        private async Task ConnectAsync(CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (this.cosmosContainer != null)
            {
                return;
            }

            await this.connectionLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (this.cosmosContainer == null)
                {
                    this.cosmosContainer = await this.CosmosContainerInitializeAsync();
                }
            }
            finally
            {
                this.connectionLock.Release();
            }
        }

        private async Task<Container> CosmosContainerInitializeAsync()
        {
            this.initializedClient = this.options.CosmosClient == null;
            this.cosmosClient = this.GetClientInstance();
            if (this.options.CreateIfNotExists)
            {
                await this.cosmosClient.CreateDatabaseIfNotExistsAsync(this.options.DatabaseName).ConfigureAwait(false);

                int defaultTimeToLive = options.DefaultTimeToLiveInMs.HasValue
                    && options.DefaultTimeToLiveInMs.Value > 0 ? options.DefaultTimeToLiveInMs.Value : CosmosCache.DefaultTimeToLive;

                // Container is optimized as Key-Value store excluding all properties
                await this.cosmosClient.GetDatabase(this.options.DatabaseName).DefineContainer(this.options.ContainerName, CosmosCache.ContainerPartitionKeyPath)
                    .WithDefaultTimeToLive(-1)
                    .WithIndexingPolicy()
                        .WithIndexingMode(IndexingMode.Consistent)
                        .WithIncludedPaths()
                            .Attach()
                        .WithExcludedPaths()
                            .Path("/*")
                            .Attach()
                    .Attach()
                .CreateAsync(this.options.ContainerThroughput).ConfigureAwait(false);
            }
            else
            {
                ContainerResponse existingContainer = await this.cosmosClient.GetContainer(this.options.DatabaseName, this.options.ContainerName).ReadContainerAsync().ConfigureAwait(false);
                if (existingContainer.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"Cannot find an existing container named {this.options.ContainerName} within database {this.options.DatabaseName}");
                }
            }

            return this.cosmosClient.GetContainer(this.options.DatabaseName, this.options.ContainerName);
        }

        private CosmosClient GetClientInstance()
        {
            if (this.options.CosmosClient != null)
            {
                return this.options.CosmosClient;
            }

            if (string.IsNullOrEmpty(this.options.ConnectionString))
            {
                throw new ArgumentNullException(nameof(this.options.ConnectionString));
            }

            return new CosmosClient(this.options.ConnectionString, this.options.Configuration);
        }

        private static CosmosCacheSession BuildCosmosCacheSession(string key, byte[] content, DistributedCacheEntryOptions options, DateTimeOffset? creationTime = null)
        {
            if (!creationTime.HasValue)
            {
                creationTime = DateTimeOffset.UtcNow;
            }

            DateTimeOffset? absoluteExpiration = CosmosCache.GetAbsoluteExpiration(creationTime.Value, options);

            long? timeToLive = CosmosCache.GetExpirationInSeconds(creationTime.Value, absoluteExpiration, options);

            return new CosmosCacheSession()
            {
                SessionKey = key,
                Content = content,
                TimeToLive = timeToLive
            };
        }

        private static long? GetExpirationInSeconds(DateTimeOffset creationTime, DateTimeOffset? absoluteExpiration, DistributedCacheEntryOptions options)
        {
            if (absoluteExpiration.HasValue && options.SlidingExpiration.HasValue)
            {
                return (long)Math.Min(
                    (absoluteExpiration.Value - creationTime).TotalSeconds,
                    options.SlidingExpiration.Value.TotalSeconds);
            }
            else if (absoluteExpiration.HasValue)
            {
                return (long)(absoluteExpiration.Value - creationTime).TotalSeconds;
            }
            else if (options.SlidingExpiration.HasValue)
            {
                return (long)options.SlidingExpiration.Value.TotalSeconds;
            }

            return null;
        }

        private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset creationTime, DistributedCacheEntryOptions options)
        {
            if (options.AbsoluteExpiration.HasValue && options.AbsoluteExpiration <= creationTime)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
                    options.AbsoluteExpiration.Value,
                    "The absolute expiration value must be in the future.");
            }
            var absoluteExpiration = options.AbsoluteExpiration;
            if (options.AbsoluteExpirationRelativeToNow.HasValue)
            {
                absoluteExpiration = creationTime + options.AbsoluteExpirationRelativeToNow;
            }

            return absoluteExpiration;
        }
    }
}
