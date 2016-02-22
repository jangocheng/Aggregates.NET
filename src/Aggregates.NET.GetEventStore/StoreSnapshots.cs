﻿using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Aggregates.Internal;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using Newtonsoft.Json;
using NServiceBus.Logging;
using NServiceBus.MessageInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates
{
    public class StoreSnapshots : IStoreSnapshots
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(StoreSnapshots));
        private readonly IEventStoreConnection _client;
        private readonly JsonSerializerSettings _settings;

        public StoreSnapshots(IEventStoreConnection client, JsonSerializerSettings settings)
        {
            _client = client;
            _settings = settings;
        }

        public ISnapshot GetSnapshot(String bucket, String stream)
        {
            Logger.DebugFormat("Getting snapshot for stream '{0}' in bucket '{1}'", stream, bucket);

            var streamId = String.Format("{0}.{1}.{2}", bucket, stream, "snapshots");

            var read = _client.ReadEventAsync(streamId, StreamPosition.End, false).WaitForResult();
            if (read.Status != EventReadStatus.Success || !read.Event.HasValue)
                return null;

            var @event = read.Event.Value.Event;

            var descriptor = @event.Metadata.Deserialize(_settings);
            var data = @event.Data.Deserialize(@event.EventType, _settings);

            return new Snapshot
            {
                EntityType = descriptor.EntityType,
                Bucket = bucket,
                Stream = stream,
                Timestamp = descriptor.Timestamp,
                Version = descriptor.Version,
                Payload = data
            };
        }


        public void WriteSnapshots(String bucket, String stream, IEnumerable<ISnapshot> snapshots)
        {
            Logger.DebugFormat("Writing {0} snapshots to stream id '{1}' in bucket '{2}'", snapshots.Count(), stream, bucket);
            var streamId = String.Format("{0}.{1}.{2}", bucket, stream, "snapshots");

            var translatedEvents = snapshots.Select(e =>
            {
                var descriptor = new EventDescriptor
                {
                    EntityType = e.EntityType,
                    Timestamp = e.Timestamp,
                    Version = e.Version
                };
                return new EventData(
                    Guid.NewGuid(),
                    e.EntityType,
                    true,
                    e.Payload.Serialize(_settings).AsByteArray(),
                    descriptor.Serialize(_settings).AsByteArray()
                    );
            });

            try
            {
                _client.AppendToStreamAsync(streamId, ExpectedVersion.Any, translatedEvents).Wait();
            }
            catch (global::System.AggregateException e)
            {
                throw e.InnerException;
            }
        }
        
    }
}
