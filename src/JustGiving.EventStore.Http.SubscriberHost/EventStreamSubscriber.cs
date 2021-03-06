using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JustGiving.EventStore.Http.Client;
using JustGiving.EventStore.Http.Client.Common.Utils;
using JustGiving.EventStore.Http.Client.Exceptions;
using JustGiving.EventStore.Http.SubscriberHost.Monitoring;
using log4net;

namespace JustGiving.EventStore.Http.SubscriberHost
{
    public class EventStreamSubscriber : IEventStreamSubscriber
    {
        public const string StreamIdentifierSeparator = "*@:^:@*";

        private readonly IEventStoreHttpConnection _connection;
        private readonly IEventHandlerResolver _eventHandlerResolver;
        private readonly IStreamPositionRepository _streamPositionRepository;
        private readonly ISubscriptionTimerManager _subscriptionTimerManager;
        private readonly IEventTypeResolver _eventTypeResolver;
        private readonly ILog _log;
        private readonly TimeSpan _defaultPollingInterval;
        private readonly int _sliceSize;
        private readonly TimeSpan? _longPollingTimeout;
        private readonly IEnumerable<IEventStreamSubscriberPerformanceMonitor> _performanceMonitors;
        private readonly int _eventNotFoundRetryCount;
        private readonly TimeSpan _eventNotFoundRetryDelay;

        public IStreamSubscriberIntervalMonitor StreamSubscriberMonitor { get; private set; }
        public PerformanceStats AllEventsStats { get; private set; }
        public PerformanceStats ProcessedEventsStats { get; private set; }

        private readonly object _synchroot = new object();

        /// <summary>
        /// Creates a new <see cref="IEventStreamSubscriber"/> to single node using default <see cref="ConnectionSettings"/>
        /// </summary>
        /// <param name="settings">The <see cref="ConnectionSettings"/> to apply to the new connection</param>
        /// <returns>a new <see cref="IEventStreamSubscriber"/></returns>
        public static IEventStreamSubscriber Create(EventStreamSubscriberSettings settings)
        {
            return new EventStreamSubscriber(settings);
        }

        /// <summary>
        /// Creates a new <see cref="IEventStreamSubscriber"/> to single node using default <see cref="ConnectionSettings"/>
        /// </summary>
        /// <returns>a new <see cref="IEventStreamSubscriber"/></returns>
        public static IEventStreamSubscriber Create(IEventStoreHttpConnection connection, IEventHandlerResolver eventHandlerResolver, IStreamPositionRepository streamPositionRepository)
        {
            return new EventStreamSubscriber(EventStreamSubscriberSettings.Default(connection, eventHandlerResolver, streamPositionRepository));
        }

        internal EventStreamSubscriber(EventStreamSubscriberSettings settings)
        {
            _connection = settings.Connection;
            _eventHandlerResolver = settings.EventHandlerResolver;
            _streamPositionRepository = settings.StreamPositionRepository;
            _subscriptionTimerManager = settings.SubscriptionTimerManager;
            _eventTypeResolver = settings.EventTypeResolver;
            _defaultPollingInterval = settings.DefaultPollingInterval;
            _sliceSize = settings.SliceSize;
            _longPollingTimeout = settings.LongPollingTimeout;
            _performanceMonitors = settings.PerformanceMonitors;
            _log = settings.Log;

            _eventNotFoundRetryCount = settings.EventNotFoundRetryCount;
            _eventNotFoundRetryDelay = settings.EventNotFoundRetryDelay;

            StreamSubscriberMonitor = settings.SubscriberIntervalMonitor;
            AllEventsStats = new PerformanceStats(settings.MessageProcessingStatsWindowPeriod, settings.MessageProcessingStatsWindowCount);
            ProcessedEventsStats = new PerformanceStats(settings.MessageProcessingStatsWindowPeriod, settings.MessageProcessingStatsWindowCount);
        }

        public void SubscribeTo(string stream, string subscriberId, TimeSpan? pollInterval = null)
        {
            lock (_synchroot)
            {
                var interval = pollInterval ?? _defaultPollingInterval;
                Log.Info(_log, "Subscribing to {0}|{1} with an interval of {2}", stream, subscriberId ?? "default", interval);
                _subscriptionTimerManager.Add(stream, subscriberId, interval, async () => await PollAsync(stream, subscriberId), () => StreamSubscriberMonitor.UpdateEventStreamSubscriberIntervalMonitor(stream, interval, subscriberId));
                Log.Info(_log, "Subscribed to {0}|{1} with an interval of {2}", stream, subscriberId ?? "default", interval);
            }
        }

        public void UnsubscribeFrom(string stream, string subscriberId)
        {
            lock (_synchroot)
            {
                Log.Info(_log, "Unsubscribing from {0}|{1}", stream, subscriberId ?? "default");
                _subscriptionTimerManager.Remove(stream, subscriberId);
                Log.Info(_log, "Unsubscribed from {0}|{1}", stream, subscriberId);

                StreamSubscriberMonitor.RemoveEventStreamMonitor(stream, subscriberId);
                Log.Info(_log, "Stream ticks monitor removed from {0}|{1}", stream, subscriberId ?? "default");
            }
        }

        public async Task<AdHocInvocationResult> AdHocInvokeAsync(string stream, int eventNumber, string subscriberId = null)
        {
            var eventMetadata = await _connection.ReadEventAsync(stream, eventNumber);

            if (eventMetadata.Status != EventReadStatus.Success)
            {
                return new AdHocInvocationResult(AdHocInvocationResult.AdHocInvocationResultCode.CouldNotFindEvent);
            }

            var eventInfo = eventMetadata.EventInfo;

            var handlers = GetEventHandlersFor(eventInfo.EventType, subscriberId);

            if (handlers.Any())
            {
                var eventType = _eventTypeResolver.Resolve(eventInfo.EventType);
                var @event = eventInfo.Content.GetObject(eventType);

                var errors = await InvokeMessageHandlersForEventMessageAsync(stream, eventType, handlers, @event, eventInfo);

                return errors.Any() ? new AdHocInvocationResult(errors) : new AdHocInvocationResult(AdHocInvocationResult.AdHocInvocationResultCode.Success);
            }

            return new AdHocInvocationResult(AdHocInvocationResult.AdHocInvocationResultCode.NoHandlersFound);
        }

        public async Task PollAsync(string stream, string subscriberId)
        {
            Log.Debug(_log, "{0}|{1}: Begin polling", stream, subscriberId ?? "default");
            lock (_synchroot)
            {
                _subscriptionTimerManager.Pause(stream, subscriberId);//we want to be able to cane a stream if we are not up to date, without reading it twice
            }

            try
            {
                await PollAsyncInternal(stream, subscriberId);
            }
            catch (Exception ex)
            {
                Log.Error(_log, ex, "{0}|{1}: Generic last-chance catch", stream, subscriberId ?? "default");
            }

            lock (_synchroot)
            {
                _subscriptionTimerManager.Resume(stream, subscriberId);
            }

            Log.Debug(_log, "{0}|{1}: Finished polling", stream, subscriberId);
        }

        private async Task PollAsyncInternal(string stream, string subscriberId)
        {
            var runAgain = false;
            do
            {
                var lastPosition = await _streamPositionRepository.GetPositionForAsync(stream, subscriberId) ?? -1;

                Log.Debug(_log, "{0}|{1}: Last position for stream was {2}", stream, subscriberId ?? "default", lastPosition);

                Log.Debug(_log, "{0}|{1}: Begin reading event metadata", stream, subscriberId??"default");
                var processingBatch =
                    await
                        _connection.ReadStreamEventsForwardAsync(stream, lastPosition + 1, _sliceSize,
                            _longPollingTimeout);
                Log.Debug(_log, "{0}|{1}: Finished reading event metadata: {2}", stream, subscriberId ?? "default", processingBatch.Status);

                if (processingBatch.Status == StreamReadStatus.Success)
                {
                    Log.Debug(_log, "{0}|{1}: Processing {2} events", stream, subscriberId ?? "default", processingBatch.Entries.Count);
                    foreach (var message in processingBatch.Entries)
                    {
                        var handlers = GetEventHandlersFor(message.EventType, subscriberId);
                        AllEventsStats.MessageProcessed(stream);

                        if (handlers.Any())
                        {
                            await ProcessSingleMessageAsync(stream, _eventTypeResolver.Resolve(message.EventType), handlers, message, subscriberId);
                            ProcessedEventsStats.MessageProcessed(stream);
                        }
                        else
                        {
                            _performanceMonitors.AsParallel()
                                                .ForAll(
                                                    x => x.Accept(stream, message.EventType, message.Updated, 0, Enumerable.Empty<KeyValuePair<Type, Exception>>())
                                                    );
                        }

                        Log.Debug(_log, "{0}|{1}: Storing last read event as {2}", stream, subscriberId ?? "default", message.SequenceNumber);
                        await _streamPositionRepository.SetPositionForAsync(stream, subscriberId, message.SequenceNumber);

                        if (!IsSubscribed(subscriberId))
                        {
                            Log.Debug(_log, "{0}|{1}: subscriber unsubscribed.", stream, subscriberId ?? "default");
                            break;
                        }
                    }

                    runAgain = processingBatch.Entries.Any() && IsSubscribed(subscriberId);
                    if (runAgain)
                    {
                        Log.Debug(_log, "{0}|{1}: New items were found; repolling", stream, subscriberId ?? "default");
                    }
                }
                else
                {
                    runAgain = false;
                }
            } while (runAgain);
        }

        private bool IsSubscribed(string subscriberId)
        {
            lock (_synchroot)
            {
                return _subscriptionTimerManager.GetSubscriptions().Any(x => x.SubscriberId == subscriberId);
            }
        }

        public IEnumerable<object> GetEventHandlersFor(string eventTypeName, string subscriberId)
        {
            var eventType = _eventTypeResolver.Resolve(eventTypeName);
            if (eventType == null)
            {
                Log.Info(_log, "An unsupported event type was passed in. No event type found for {0}", eventTypeName);
                return Enumerable.Empty<object>();
            }

            var baseHandlerInterfaceType = typeof(IHandleEventsOf<>).MakeGenericType(eventType);
            var baseMetadataHandlerInterfaceType = typeof(IHandleEventsAndMetadataOf<>).MakeGenericType(eventType);

            var handlers = _eventHandlerResolver.GetHandlersOf(baseHandlerInterfaceType).Cast<object>().ToList();
            var metadataHandlers = _eventHandlerResolver.GetHandlersOf(baseMetadataHandlerInterfaceType).Cast<object>();

            var allHandlers = handlers.Concat(metadataHandlers);
            var handlersForSubscriberId = GetHandlersApplicableToSubscriberId(allHandlers, subscriberId).ToList();

            if (handlersForSubscriberId.Any())
            {
                Log.Debug(_log, "{0} handlers found for {1}", handlersForSubscriberId.Count, eventType.FullName);
            }
            else
            {
                Log.Debug(_log, "No handlers found for {0}", eventType.FullName);
            }
            return handlersForSubscriberId;
        }

        public IEnumerable<object> GetHandlersApplicableToSubscriberId(IEnumerable<object> handlers, string subscriberId)
        {
            if (subscriberId == null)
            {
                return handlers.Where(x => !x.GetType().GetCustomAttributes<NonDefaultSubscriberAttribute>().Any());
            }

            return handlers.Where(x =>x.GetType().GetCustomAttributes<NonDefaultSubscriberAttribute>()
                                       .Any(att => att.SupportedSubscriberId == subscriberId));
        }

        /// <remarks>
        /// If the event store is deployed as a cluster, it may be possible to get transient read failures.
        /// If an event could not be found on the event stream, try again.
        ///</remarks>
        private async Task ProcessSingleMessageAsync(string stream, Type eventType, IEnumerable handlers, BasicEventInfo eventInfo, string subscriberId)
        {
            var maxAttempts = Math.Max(_eventNotFoundRetryCount, 1);

            object @event = null;
            for (var i = 0; i < maxAttempts; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(_eventNotFoundRetryDelay);
                }

                try
                {
                    Log.Debug(_log, "{0}|{1}: Processing event {2}. Attempt: {3}", stream, subscriberId ?? "default", eventInfo.Id, i + 1);

                    @event = await _connection.ReadEventBodyAsync(eventType, eventInfo.CanonicalEventLink);
                    if (@event != null)
                    {
                        break;
                    }
                }
                catch (EventNotFoundException ex)
                {
                    if (i < maxAttempts - 1)
                    {
                        Log.Warning(_log, "{0}|{1}: Event could not be found. Attempting to process the event again. {2}: {3}", stream, subscriberId ?? "default", eventInfo.Id, ex.Message);
                    }
                    else
                    {
                        Log.Error(_log, "{0}|{1}: Event could not be found after {2} attempts. {3}: {4}", stream, subscriberId ?? "default", i + 1, eventInfo.Id, ex.Message);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(_log, "{0}|{1}: Error getting message {2}: {3}", stream, subscriberId ?? "default", eventInfo.Id, ex);
                    return;
                }
            }

            try
            {
                await InvokeMessageHandlersForEventMessageAsync(stream, eventType, handlers, @event, eventInfo);
            }
            catch (Exception ex)
            {
                Log.Error(_log, "{0}|{1}: Error invoking message handlers for message {2}: {3}", stream, subscriberId ?? "default", eventInfo.Id, ex);
            }
        }

        public async Task<IDictionary<Type, Exception>> InvokeMessageHandlersForEventMessageAsync(string stream, Type eventType, IEnumerable handlers, object @event, BasicEventInfo eventInfo)
        {
            var handlerCount = 0;
            var eventTitle = eventInfo.Title;
            var updated = eventInfo.Updated;
            var errors = new Dictionary<Type, Exception>();
            foreach (var handler in handlers)
            {
                handlerCount++;
                var handlerType = handler.GetType();

                var handleMethod = GetMethodFromHandler(handlerType, eventType, "Handle");

                if (handleMethod == null)
                {
                    Log.Warning(_log, "Could not find the handle method for: {0}", handlerType.FullName);
                    continue;
                }

                try
                {
                    try
                    {
                        var arguments = new[] {@event, eventInfo}.Take(handleMethod.GetParameters().Length);
                        await (Task)handleMethod.Invoke(handler, arguments.ToArray());
                    }
                    catch (Exception invokeException)
                    {
                        errors[handlerType] = invokeException.InnerException;

                        var errorMessage = string.Format("{0} thrown processing event {1}",
                            invokeException.GetType().FullName, eventTitle);
                        Log.Error(_log, errorMessage, invokeException);

                        var errorMethod = GetMethodFromHandler(handlerType, eventType, "OnError");
                        errorMethod.Invoke(handler, new[] { invokeException, @event });
                    }
                }
                catch (Exception errorHandlingException)
                {
                    var errorMessage = string.Format("{0} thrown whilst handling error from event {1}",
                        errorHandlingException.GetType().FullName, eventTitle);
                    Log.Error(_log, errorMessage, errorHandlingException);
                }
            }

            _performanceMonitors.AsParallel().ForAll(x => x.Accept(stream, eventType.FullName, updated, handlerCount, errors));

            return errors;
        }

        ConcurrentDictionary<string, MethodInfo> methodCache = new ConcurrentDictionary<string, MethodInfo>();

        public MethodInfo GetMethodFromHandler(Type concreteHandlerType, Type eventType, string methodName)
        {
            var cacheKey = string.Format("{0}.{1}({2})", concreteHandlerType, methodName, eventType.FullName);

            MethodInfo result;
            if (methodCache.TryGetValue(cacheKey, out result))
            {
                return result;
            }

            var handlerInterfaces = new[] {typeof (IHandleEventsOf<>), typeof (IHandleEventsAndMetadataOf<>)};

            var @interface = concreteHandlerType.GetInterfaces()
                .Where(x => x.IsGenericType && handlerInterfaces.Contains(x.GetGenericTypeDefinition()) && x.GetGenericArguments()[0].IsAssignableFrom(eventType))
                .OrderBy(x => x.GetGenericArguments()[0], new TypeInheritanceComparer())
                .FirstOrDefault(); //a type can explicitly implement two IHandle<> interfaces (which would be insane, but will now at least work)

            if (@interface == null)
            {
                Log.Warning(_log, "{0}, which handles {1} did not contain a suitable method named {2}", concreteHandlerType.FullName, eventType.FullName, methodName);
                methodCache.TryAdd(cacheKey, result);
                return null;
            }

            result = @interface.GetMethod(methodName);
            methodCache.TryAdd(cacheKey, result);
            return result;
        }

        public IEnumerable<StreamSubscription> GetSubscriptions()
        {
            lock (_synchroot)
            {
                return _subscriptionTimerManager.GetSubscriptions()
                    .ToList()
                    .AsReadOnly();
            }
        }
    }
}
