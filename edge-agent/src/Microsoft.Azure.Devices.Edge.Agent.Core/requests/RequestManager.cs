// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Core.Requests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;

    public class RequestManager : IRequestManager
    {
        readonly ConcurrentDictionary<string, IRequestHandler> requestHandlers;
        readonly TimeSpan maxRequestTimeout;

        public RequestManager(IEnumerable<IRequestHandler> requestHandlers, TimeSpan maxRequestTimeout)
        {
            IEnumerable<KeyValuePair<string, IRequestHandler>> requestHandlersList = Preconditions.CheckNotNull(requestHandlers, nameof(requestHandlers))
                .Select(r => new KeyValuePair<string, IRequestHandler>(r.RequestName, r));
            this.requestHandlers = new ConcurrentDictionary<string, IRequestHandler>(requestHandlersList, StringComparer.OrdinalIgnoreCase);
            this.maxRequestTimeout = maxRequestTimeout;
        }

        public void RegisterHandlers(IEnumerable<IRequestHandler> requestHandlers)
        {
            foreach (IRequestHandler requestHandler in Preconditions.CheckNotNull(requestHandlers, nameof(requestHandlers)))
            {
                Events.RegisteringHandler(requestHandler);
                this.requestHandlers.TryAdd(requestHandler.RequestName, requestHandler);
            }
        }

        public async Task<(int statusCode, Option<string> responsePayload)> ProcessRequest(string request, string payloadJson)
        {
            try
            {
                Preconditions.CheckNonWhiteSpace(request, nameof(request));
                Events.HandlingRequest(request, payloadJson);
                if (!this.requestHandlers.TryGetValue(request, out IRequestHandler requestHandler))
                {
                    string supportedCommands = string.Join(",", this.requestHandlers.Keys);
                    string message = $"Command '{request}' not found. The supported commands are - {supportedCommands}";
                    throw new ArgumentException(message);
                }

                // This does not timeout the actual handle request operation.
                // It relies on the handler to cancel the operation when the cancellation token is set to cancelled.
                using (var cancellationTokenSource = new CancellationTokenSource(this.maxRequestTimeout))
                {
                    Option<string> responsePayload = await requestHandler.HandleRequest(Option.Maybe(payloadJson), cancellationTokenSource.Token);
                    Events.HandledRequest(request);
                    return ((int)HttpStatusCode.OK, responsePayload);
                }
            }
            catch (Exception ex)
            {
                Events.ErrorHandlingRequest(request, ex);
                return GetErrorResponse(ex);
            }
        }

        static (int statusCode, Option<string> responsePayload) GetErrorResponse(Exception ex)
        {
            switch (ex)
            {
                case ArgumentException _:
                    return ((int)HttpStatusCode.BadRequest, Option.Some(GetErrorPayload(ex.Message)));
                default:
                    return ((int)HttpStatusCode.InternalServerError, Option.Some(GetErrorPayload(ex.Message)));
            }
        }

        static string GetErrorPayload(string message)
        {
            var errorPayload = new
            {
                message
            };
            string json = errorPayload.ToJson();
            return json;
        }

        static class Events
        {
            const int IdStart = AgentEventIds.RequestManager;
            static readonly ILogger Log = Logger.Factory.CreateLogger<RequestManager>();

            enum EventIds
            {
                HandlingRequest = IdStart + 1,
                ErrorHandlingRequest,
                RegisteringHandler
            }

            public static void ErrorHandlingRequest(string request, Exception exception)
            {
                Log.LogWarning((int)EventIds.ErrorHandlingRequest, exception, $"Error handling request {request}");
            }

            public static void HandlingRequest(string request, string payloadJson)
            {
                Log.LogInformation(
                    (int)EventIds.HandlingRequest,
                    string.IsNullOrWhiteSpace(payloadJson)
                        ? $"Received request {request}"
                        : $"Received request {request} with payload");
            }

            public static void HandledRequest(string request)
            {
                Log.LogInformation((int)EventIds.HandlingRequest, $"Successfully handled request {request}");
            }

            public static void RegisteringHandler(IRequestHandler requestHandler)
            {
                Log.LogInformation((int)EventIds.RegisteringHandler, $"Registering request handler {requestHandler.RequestName}");
            }
        }
    }
}
