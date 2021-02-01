﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static RockLib.Messaging.HttpUtils;

namespace RockLib.Messaging.Http
{
    /// <summary>
    /// An implementation of <see cref="ISender" /> that sends messages with an
    /// <see cref="HttpClient"/>.
    /// </summary>
    public class HttpClientSender : ISender
    {
        private readonly HttpContentHeaders _defaultContentHeaders = new ByteArrayContent(new byte[0]).Headers;
        private readonly HttpRequestHeaders _defaultRequestHeaders = new HttpRequestMessage().Headers;

        private readonly HttpClient _client;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientSender"/> class.
        /// </summary>
        /// <param name="name">The name of the sender.</param>
        /// <param name="url">The url to send messages to.</param>
        /// <param name="method">The http method to use when sending messages.</param>
        /// <param name="defaultHeaders">Default headers that are added to each http request.</param>
        public HttpClientSender(string name, string url, string method = "POST", IReadOnlyDictionary<string, string> defaultHeaders = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Url = url ?? throw new ArgumentNullException(nameof(url));
            Method = new HttpMethod(method ?? throw new ArgumentNullException(nameof(method)));

            _client = new HttpClient();

            if (defaultHeaders != null)
            {
                foreach (var header in defaultHeaders)
                {
                    if (IsContentHeader(header.Key))
                        AddHeader(_defaultContentHeaders, header.Key, header.Value);
                    else
                        AddHeader(_defaultRequestHeaders, header.Key, header.Value);
                }
            }
        }

        /// <summary>
        /// Gets the name of this instance of <see cref="HttpClientSender"/>.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the url that messages are sent to.
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// Gets the http method that is used when sending messages.
        /// </summary>
        public HttpMethod Method { get; }

        /// <summary>
        /// Disposes the <see cref="HttpClient"/>.
        /// </summary>
        public void Dispose()
        {
            _client.Dispose();
        }

        /// <summary>
        /// Asynchronously sends the specified message.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        public async Task SendAsync(SenderMessage message, CancellationToken cancellationToken)
        {
            if (message.OriginatingSystem == null)
                message.OriginatingSystem = "HTTP";

            var headers = new Dictionary<string, object>(message.Headers);

            var url = GetUrl(headers);

            var request = new HttpRequestMessage(Method, url)
            {
                Content = new ByteArrayContent(message.BinaryPayload)
            };

            foreach (var defaultContentHeader in _defaultContentHeaders)
                if (!message.Headers.ContainsKey(defaultContentHeader.Key))
                    request.Content.Headers.Add(defaultContentHeader.Key, defaultContentHeader.Value);

            foreach (var defaultRequestHeader in _defaultRequestHeaders)
                if (!message.Headers.ContainsKey(defaultRequestHeader.Key))
                    request.Headers.Add(defaultRequestHeader.Key, defaultRequestHeader.Value);

            foreach (var header in message.Headers)
            {
                if (IsContentHeader(header.Key))
                    AddHeader(request.Content.Headers, header.Key, header.Value?.ToString());
                else
                    AddHeader(request.Headers, header.Key, header.Value?.ToString());
            }

            // TODO: if the message is compressed, add an http compression header?

            var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Get the url for the specified message, replacing any tokens with corresponding header
        /// values. If a header is used to replace a token in the url, it is removed from the
        /// <paramref name="headers"/> dictionary.
        /// </summary>
        private string GetUrl(Dictionary<string, object> headers)
        {
            return Regex.Replace(Url, "{([^}]+)}", match =>
            {
                var token = match.Groups[1].Value;

                if (headers.ContainsKey(token))
                {
                    var value = headers[token];
                    headers.Remove(token);
                    return value?.ToString();
                }

                throw new InvalidOperationException($"The url for this {nameof(HttpClientSender)} contains a token, '{token}', that is not present in the headers of the sender message.");
            });
        }
    }
}
