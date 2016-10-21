﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using NServiceBus.Logging;

namespace Aggregates.Internal
{
    /// <summary>
    /// Conflict from the store is ignored, events will always be written
    /// </summary>
    internal class IgnoreConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogManager.GetLogger(typeof(IgnoreConflictResolver));

        private readonly IStoreEvents _store;

        public IgnoreConflictResolver(IStoreEvents eventstore)
        {
            _store = eventstore;
        }

        public async Task<Guid> Resolve<T>(T entity, IEnumerable<IWritableEvent> uncommitted, Guid commitId, Guid startingEventId, IDictionary<string, string> commitHeaders) where T : class, IEventSource
        {
            var stream = entity.Stream;
            Logger.Write(LogLevel.Info, () => $"Resolving {uncommitted.Count()} uncommitted events to stream [{stream.StreamId}] bucket [{stream.Bucket}]");

            foreach (var u in uncommitted)
            {
                if (!u.EventId.HasValue)
                {
                    u.EventId = startingEventId;
                    startingEventId = startingEventId.Increment();
                }
                entity.Apply(u.Event);
            }

            await _store.AppendEvents<T>(stream.Bucket, stream.StreamId, uncommitted, commitHeaders).ConfigureAwait(false);
            stream.Flush(true);

            return startingEventId;
        }
    }
    /// <summary>
    /// Conflicted events are discarded
    /// </summary>
    internal class DiscardConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogManager.GetLogger(typeof(DiscardConflictResolver));

        public Task<Guid> Resolve<T>(T entity, IEnumerable<IWritableEvent> uncommitted, Guid commitId, Guid startingEventId, IDictionary<string, string> commitHeaders) where T : class, IEventSource
        {
            var stream = entity.Stream;
            Logger.Write(LogLevel.Info, () => $"Discarding {uncommitted.Count()} conflicting uncommitted events to stream [{stream.StreamId}] bucket [{stream.Bucket}]");

            return Task.FromResult(startingEventId);
        }
    }
    /// <summary>
    /// Pull latest events from store, merge into stream and re-commit
    /// </summary>
    internal class ResolveStronglyConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogManager.GetLogger(typeof(ResolveStronglyConflictResolver));

        private readonly IStoreEvents _store;

        public ResolveStronglyConflictResolver(IStoreEvents eventstore)
        {
            _store = eventstore;
        }

        public async Task<Guid> Resolve<T>(T entity, IEnumerable<IWritableEvent> uncommitted, Guid commitId, Guid startingEventId, IDictionary<string, string> commitHeaders) where T : class, IEventSource
        {
            var stream = entity.Stream;
            Logger.Write(LogLevel.Info, () => $"Resolving {uncommitted.Count()} uncommitted events to stream [{stream.StreamId}] bucket [{stream.Bucket}]");

            try
            {
                await _store.Freeze<T>(stream.Bucket, stream.StreamId).ConfigureAwait(false);

                var latestEvents =
                    await
                        _store.GetEvents<T>(stream.Bucket, stream.StreamId, stream.CommitVersion + 1)
                            .ConfigureAwait(false);
                Logger.Write(LogLevel.Debug, () => $"Stream is {latestEvents.Count()} events behind store");

                var writableEvents = latestEvents as IWritableEvent[] ?? latestEvents.ToArray();
                stream.Concat(writableEvents);
                entity.Hydrate(writableEvents.Select(x => x.Event));


                Logger.Write(LogLevel.Debug, () => "Merging conflicted events");
                try
                {
                    foreach (var u in uncommitted)
                        entity.Conflict(u.Event);
                }
                catch (NoRouteException e)
                {
                    Logger.Write(LogLevel.Info, () => $"Failed to resolve conflict: {e.Message}");
                    throw new ConflictResolutionFailedException("Failed to resolve conflict", e);
                }

                Logger.Write(LogLevel.Debug, () => "Successfully merged conflicted events");

                if (stream.StreamVersion != stream.CommitVersion && entity is ISnapshotting &&
                    ((ISnapshotting) entity).ShouldTakeSnapshot())
                {
                    Logger.Write(LogLevel.Debug,
                        () =>
                                $"Taking snapshot of {typeof(T).FullName} id [{entity.StreamId}] version {stream.StreamVersion}");
                    var memento = ((ISnapshotting) entity).TakeSnapshot();
                    stream.AddSnapshot(memento, commitHeaders);
                }

                startingEventId = await stream.Commit(commitId, startingEventId, commitHeaders).ConfigureAwait(false);
            }
            finally
            {
                await _store.Unfreeze<T>(stream.Bucket, stream.StreamId).ConfigureAwait(false);
            }
            return startingEventId;
        }
    }
    /// <summary>
    /// Save conflicts for later processing, can only be used if the stream can never fail to merge
    /// </summary>
    internal class ResolveWeaklyConflictResolver : IResolveConflicts
    {
        internal static readonly ILog Logger = LogManager.GetLogger(typeof(ResolveWeaklyConflictResolver));

        private readonly IStoreEvents _store;
        private readonly IDelayedChannel _delay;

        public ResolveWeaklyConflictResolver(IStoreEvents eventstore, IDelayedChannel delay)
        {
            _store = eventstore;
            _delay = delay;
        }

        public async Task<Guid> Resolve<T>(T entity, IEnumerable<IWritableEvent> uncommitted, Guid commitId, Guid startingEventId, IDictionary<string, string> commitHeaders) where T : class, IEventSource
        {
            // Store conflicting events in memory
            // After 100 or so pile up pull the latest stream and attempt to write them again

            foreach (var @event in uncommitted)
                await _delay.AddToQueue(entity.StreamId, @event).ConfigureAwait(false);

            // Todo: make 30 seconds configurable
            if (await _delay.Age(entity.StreamId).ConfigureAwait(false) < TimeSpan.FromSeconds(30))
                return startingEventId;

            var stream = entity.Stream;
            Logger.Write(LogLevel.Debug, () => $"Starting weak conflict resolve for stream [{stream.StreamId}] bucket [{stream.Bucket}]");
            try
            {
                await _store.Freeze<T>(stream.Bucket, stream.StreamId).ConfigureAwait(false);

                uncommitted = (await _delay.Pull(entity.StreamId).ConfigureAwait(false)).Cast<IWritableEvent>();
                Logger.Write(LogLevel.Info,
                    () =>
                            $"Resolving {uncommitted.Count()} uncommitted events to stream [{stream.StreamId}] bucket [{stream.Bucket}]");

                var latestEvents =
                    await
                        _store.GetEvents<T>(stream.Bucket, stream.StreamId, stream.CommitVersion + 1)
                            .ConfigureAwait(false);
                Logger.Write(LogLevel.Debug, () => $"Stream is {latestEvents.Count()} events behind store");

                var writableEvents = latestEvents as IWritableEvent[] ?? latestEvents.ToArray();
                stream.Concat(writableEvents);
                entity.Hydrate(writableEvents.Select(x => x.Event));


                Logger.Write(LogLevel.Debug, () => "Merging conflicted events");
                try
                {
                    foreach (var u in uncommitted)
                        entity.Conflict(u.Event);
                }
                catch (NoRouteException e)
                {
                    Logger.Write(LogLevel.Info, () => $"Failed to resolve conflict: {e.Message}");
                    throw new ConflictResolutionFailedException("Failed to resolve conflict", e);
                }


                Logger.Write(LogLevel.Debug, () => "Successfully merged conflicted events");

                if (stream.StreamVersion != stream.CommitVersion && entity is ISnapshotting &&
                    ((ISnapshotting) entity).ShouldTakeSnapshot())
                {
                    Logger.Write(LogLevel.Debug,
                        () =>
                                $"Taking snapshot of [{typeof(T).FullName}] id [{entity.StreamId}] version {stream.StreamVersion}");
                    var memento = ((ISnapshotting) entity).TakeSnapshot();
                    stream.AddSnapshot(memento, commitHeaders);
                }

                startingEventId = await stream.Commit(commitId, startingEventId, commitHeaders).ConfigureAwait(false);
            }
            catch (VersionException)
            {
                // Failed to merge - throw all conflicted events BACK on the queue
                foreach (var @event in uncommitted)
                    await _delay.AddToQueue(entity.StreamId, @event).ConfigureAwait(false);

                throw;
            }
            finally
            {
                await _store.Unfreeze<T>(stream.Bucket, stream.StreamId).ConfigureAwait(false);
            }
            return startingEventId;

        }

    }

}