﻿using MediatR.Internal;

namespace MediatR
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Default mediator implementation relying on single- and multi instance delegates for resolving handlers.
    /// </summary>
    public class Mediator : IMediator
    {
        private readonly SingleInstanceFactory _singleInstanceFactory;
        private readonly MultiInstanceFactory _multiInstanceFactory;

        private static readonly MethodInfo CreatePipelineMethod =
            typeof(Mediator).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(CreatePipeline));

        private static readonly ConcurrentDictionary<Type, Delegate> _requestHandlerFactories = new ConcurrentDictionary<Type, Delegate>();

        /// <summary>
        /// Initializes a new instance of the <see cref="Mediator"/> class.
        /// </summary>
        /// <param name="singleInstanceFactory">The single instance factory.</param>
        /// <param name="multiInstanceFactory">The multi instance factory.</param>
        public Mediator(SingleInstanceFactory singleInstanceFactory, MultiInstanceFactory multiInstanceFactory)
        {
            _singleInstanceFactory = singleInstanceFactory;
            _multiInstanceFactory = multiInstanceFactory;
        }

        public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var handler = GetHandler(request, cancellationToken);

            var pipeline = GetPipeline(request, handler);

            return pipeline;
        }

        public Task SendAsync(IRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            var handler = GetHandler(request, cancellationToken);

            var pipeline = GetPipeline(request, handler);

            return pipeline;
        }

        private RequestHandlerDelegate<Unit> GetHandler(IRequest request, CancellationToken cancellationToken)
        {
            var requestType = request.GetType();

            var handlerFactory = (Func<IRequest, CancellationToken, SingleInstanceFactory, RequestHandlerDelegate<Unit>>)
                _requestHandlerFactories.GetOrAdd(requestType, GetHandlerFactory(requestType, GetHandler));

            if (handlerFactory == null)
            {
                throw BuildException(request);
            }

            return handlerFactory(request, cancellationToken, _singleInstanceFactory);
        }

        private RequestHandlerDelegate<TResponse> GetHandler<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        {
            var requestType = request.GetType();

            var handlerFactory = (Func<IRequest<TResponse>, CancellationToken, SingleInstanceFactory, RequestHandlerDelegate<TResponse>>)
                _requestHandlerFactories.GetOrAdd(requestType, GetHandlerFactory<TResponse>(requestType, GetHandler));

            if (handlerFactory == null)
            {
                throw BuildException(request);
            }

            return handlerFactory(request, cancellationToken, _singleInstanceFactory);
        }

        private object GetHandler(Type requestType)
        {
            try
            {
                return _singleInstanceFactory(requestType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Func<IRequest, CancellationToken, SingleInstanceFactory, RequestHandlerDelegate<Unit>> GetHandlerFactory(Type requestType, SingleInstanceFactory factory)
        {
            var handlerType = typeof(IRequestHandler<>).MakeGenericType(requestType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(RequestHandlerWrapperImpl<>).MakeGenericType(requestType);
                var wrapper = (RequestHandlerWrapper) Activator.CreateInstance(wrapperType);
                return (request, token, fac) => () =>
                {
                    var handler = fac(handlerType);
                    wrapper.Handle(request, handler);
                    return Task.FromResult(Unit.Value);
                };
            }
            handlerType = typeof(IAsyncRequestHandler<>).MakeGenericType(requestType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(AsyncRequestHandlerWrapperImpl<>).MakeGenericType(requestType);
                var wrapper = (AsyncRequestHandlerWrapper)Activator.CreateInstance(wrapperType);
                return (request, token, fac) => async () => 
                {
                    var handler = fac(handlerType);
                    await wrapper.Handle(request, handler);
                    return Unit.Value;
                };
            }
            handlerType = typeof(ICancellableAsyncRequestHandler<>).MakeGenericType(requestType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(CancellableAsyncRequestHandlerWrapperImpl<>).MakeGenericType(requestType);
                var wrapper = (CancellableAsyncRequestHandlerWrapper)Activator.CreateInstance(wrapperType);
                return (request, token, fac) => async () =>
                {
                    var handler = fac(handlerType);
                    await wrapper.Handle(request, token, handler);
                    return Unit.Value;
                };
            }
            return null;
        }
        private static Func<IRequest<TResponse>, CancellationToken, SingleInstanceFactory, RequestHandlerDelegate<TResponse>> GetHandlerFactory<TResponse>(Type requestType, SingleInstanceFactory factory)
        {
            var responseType = typeof(TResponse);
            var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType);
                var wrapper = (RequestHandlerWrapper<TResponse>) Activator.CreateInstance(wrapperType);
                return (request, token, fac) => () =>
                {
                    var handler = fac(handlerType);
                    return Task.FromResult(wrapper.Handle(request, handler));
                };
            }
            handlerType = typeof(IAsyncRequestHandler<,>).MakeGenericType(requestType, responseType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(AsyncRequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType);
                var wrapper = (AsyncRequestHandlerWrapper<TResponse>)Activator.CreateInstance(wrapperType);
                return (request, token, fac) =>
                {
                    var handler = fac(handlerType);
                    return () => wrapper.Handle(request, handler);
                };
            }
            handlerType = typeof(ICancellableAsyncRequestHandler<,>).MakeGenericType(requestType, responseType);
            if (factory(handlerType) != null)
            {
                var wrapperType = typeof(CancellableAsyncRequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType);
                var wrapper = (CancellableAsyncRequestHandlerWrapper<TResponse>)Activator.CreateInstance(wrapperType);
                return (request, token, fac) =>
                {
                    var handler = fac(handlerType);
                    return () => wrapper.Handle(request, token, handler);
                };
            }
            return null;
        }

        public Task PublishAsync(INotification notification, CancellationToken cancellationToken = default(CancellationToken))
        {
            var notificationHandlers = GetNotificationHandlers(notification)
                .Select(handler =>
                {
                    handler.Handle(notification);
                    return Unit.Task;
                });
            var asyncNotificationHandlers = GetAsyncNotificationHandlers(notification)
                .Select(handler => handler.Handle(notification));
            var cancellableAsyncNotificationHandlers = GetCancellableAsyncNotificationHandlers(notification)
                .Select(handler => handler.Handle(notification, cancellationToken));

            var allHandlers = notificationHandlers
                .Concat(asyncNotificationHandlers)
                .Concat(cancellableAsyncNotificationHandlers);

            return Task.WhenAll(allHandlers);
        }

        private IEnumerable<NotificationHandlerWrapper> GetNotificationHandlers(INotification notification)
        {
            return GetNotificationHandlers<NotificationHandlerWrapper>(notification,
                typeof(INotificationHandler<>),
                typeof(NotificationHandlerWrapper<>));
        }

        private IEnumerable<AsyncNotificationHandlerWrapper> GetAsyncNotificationHandlers(INotification notification)
        {
            return GetNotificationHandlers<AsyncNotificationHandlerWrapper>(notification,
                typeof(IAsyncNotificationHandler<>),
                typeof(AsyncNotificationHandlerWrapper<>));
        }

        private IEnumerable<CancellableAsyncNotificationHandlerWrapper> GetCancellableAsyncNotificationHandlers(INotification notification)
        {
            return GetNotificationHandlers<CancellableAsyncNotificationHandlerWrapper>(notification,
                typeof(ICancellableAsyncNotificationHandler<>),
                typeof(CancellableAsyncNotificationHandlerWrapper<>));
        }

        private IEnumerable<TWrapper> GetNotificationHandlers<TWrapper>(object notification, Type handlerType, Type wrapperType)
        {
            var notificationType = notification.GetType();

            var genericHandlerType = handlerType.MakeGenericType(notificationType);
            var genericWrapperType = wrapperType.MakeGenericType(notificationType);

            return _multiInstanceFactory(genericHandlerType)
                .Select(handler => Activator.CreateInstance(genericWrapperType, handler))
                .Cast<TWrapper>()
                .ToList();
        }

        private Task<TResponse> GetPipeline<TResponse>(object request, RequestHandlerDelegate<TResponse> invokeHandler)
        {
            var requestType = request.GetType();
            var method = CreatePipelineMethod.MakeGenericMethod(requestType, typeof(TResponse));
            return (Task<TResponse>)method.Invoke(this, new[] { request, invokeHandler });
        }

        private Task<TResponse> CreatePipeline<TRequest, TResponse>(TRequest request, RequestHandlerDelegate<TResponse> invokeHandler)
        {
            var behaviors = _multiInstanceFactory(typeof(IPipelineBehavior<TRequest, TResponse>))
                .Cast<IPipelineBehavior<TRequest, TResponse>>()
                .Reverse();

            var aggregate = behaviors.Aggregate(invokeHandler, (next, pipeline) => () => pipeline.Handle(request, next));

            return aggregate();
        }

        private static InvalidOperationException BuildException(object message)
        {
            return new InvalidOperationException("Handler was not found for request of type " + message.GetType() + ".\r\nContainer or service locator not configured properly or handlers not registered with your container.");
        }
    }
}
