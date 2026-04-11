using System;
using System.Linq;

using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SockudoServer.RestfulClient;

namespace SockudoServer
{
    /// <summary>
    /// Provides access to functionality within the Sockudo service such as Trigger to trigger events
    /// and authenticating subscription requests to private and presence channels.
    /// </summary>
    public class Sockudo : ISockudo
    {
        private const string ChannelUsersResource = "/channels/{0}/users";
        private const string ChannelResource = "/channels/{0}";
        private const string ChannelHistoryResource = "/channels/{0}/history";
        private const string PresenceHistoryResource = "/channels/{0}/presence/history";
        private const string PresenceSnapshotResource = "/channels/{0}/presence/history/snapshot";
        private const string MultipleChannelsResource = "/channels";

        private readonly string _appKey;
        private readonly string _appSecret;
        private readonly ISockudoOptions _options;

        private readonly IAuthenticatedRequestFactory _factory;

        private readonly IChannelDataEncrypter _dataEncrypter = new ChannelDataEncrypter();

        private readonly string _baseId;
        private long _publishSerial = 0;

        private const int MaxRetries = 3;

        /// <summary>
        /// Gets or sets whether deterministic idempotency keys are automatically
        /// generated for every <see cref="TriggerAsync"/> call that does not already
        /// carry an explicit key. The key format is <c>{baseId}:{publishSerial}</c>,
        /// where the serial increments before each call so retries reuse the same key.
        /// </summary>
        public bool AutoIdempotencyKey { get; set; } = true;

        /// <summary>
        /// Generates a new idempotency key as a UUID v4 string.
        /// </summary>
        /// <returns>A new UUID v4 string suitable for use as an idempotency key.</returns>
        public static string GenerateIdempotencyKey()
        {
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Sockudo library version information.
        /// </summary>
        public static Version VERSION
        {
            get
            {
                return typeof(Sockudo).GetTypeInfo().Assembly.GetName().Version;
            }
        }

        /// <summary>
        /// The Sockudo library name.
        /// </summary>
        public static String LIBRARY_NAME
        {
            get
            {
                Attribute attr = typeof(Sockudo).GetTypeInfo().Assembly.GetCustomAttribute(typeof(AssemblyProductAttribute));

                AssemblyProductAttribute adAttr = (AssemblyProductAttribute)attr;

                return adAttr.Product;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Sockudo" /> class.
        /// </summary>
        /// <param name="appId">The app id.</param>
        /// <param name="appKey">The app key.</param>
        /// <param name="appSecret">The app secret.</param>
        /// <param name="options">(Optional)Additional options to be used with the instance e.g. setting the call to the REST API to be made over HTTPS.</param>
        public Sockudo(string appId, string appKey, string appSecret, ISockudoOptions options = null)
        {
            ThrowArgumentExceptionIfNullOrEmpty(appId, "appId");
            ThrowArgumentExceptionIfNullOrEmpty(appKey, "appKey");
            ThrowArgumentExceptionIfNullOrEmpty(appSecret, "appSecret");

            if (options == null)
            {
                options = new SockudoOptions();
            }

            _appKey = appKey;
            _appSecret = appSecret;
            _options = options;

            var bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            _baseId = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            _factory = new AuthenticatedRequestFactory(appKey, appId, appSecret);
        }

        /// <inheritdoc/>
        public async Task<ITriggerResult> TriggerAsync(string channelName, string eventName, object data, ITriggerOptions options = null)
        {
            return await TriggerAsync(new[] { channelName }, eventName, data, options).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<ITriggerResult> TriggerAsync(string[] channelNames, string eventName, object data, ITriggerOptions options = null)
        {
            if (options == null)
                options = new TriggerOptions();

            long serial = Interlocked.Increment(ref _publishSerial) - 1;
            if (AutoIdempotencyKey && string.IsNullOrEmpty(options.IdempotencyKey))
            {
                options.IdempotencyKey = $"{_baseId}:{serial}";
            }

            var bodyData = CreateTriggerBody(channelNames, eventName, data, options);

            var request = _factory.Build(SockudoMethod.POST, "/events", requestBody: bodyData);

            if (!string.IsNullOrEmpty(options.IdempotencyKey))
            {
                request.Headers["X-Idempotency-Key"] = options.IdempotencyKey;
            }

            DebugTriggerRequest(request);

            TriggerResult result = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    result = await _options.RestClient.ExecutePostAsync(request).ConfigureAwait(false);

                    DebugTriggerResponse(result);

                    var code = (int)result.StatusCode;
                    if (code < 500)
                        return result;
                }
                catch (Exception) when (attempt < MaxRetries - 1)
                {
                    continue;
                }

                if (attempt < MaxRetries - 1)
                    await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
            }

            return result;
        }

        ///<inheritDoc/>
        public async Task<ITriggerResult> TriggerAsync(Event[] events)
        {
            long serial = Interlocked.Increment(ref _publishSerial) - 1;

            if (AutoIdempotencyKey)
            {
                for (int i = 0; i < events.Length; i++)
                {
                    if (string.IsNullOrEmpty(events[i].IdempotencyKey))
                    {
                        events[i].IdempotencyKey = $"{_baseId}:{serial}:{i}";
                    }
                }
            }

            var bodyData = CreateBatchTriggerBody(events);

            var request = _factory.Build(SockudoMethod.POST, "/batch_events", requestBody: bodyData);

            DebugTriggerRequest(request);

            TriggerResult result = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    result = await _options.RestClient.ExecutePostAsync(request).ConfigureAwait(false);

                    DebugTriggerResponse(result);

                    var code = (int)result.StatusCode;
                    if (code < 500)
                        return result;
                }
                catch (Exception) when (attempt < MaxRetries - 1)
                {
                    continue;
                }

                if (attempt < MaxRetries - 1)
                    await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
            }

            return result;
        }

        private string SerializeData(string channelName, object data)
        {
            string result = _options.JsonSerializer.Serialize(data);
            if (IsPrivateEncryptedChannel(channelName))
            {
                byte[] key = null;
                if (_options.EncryptionMasterKey != null)
                {
                    key = _options.EncryptionMasterKey;
                }

                EncryptedChannelData encryptedData = _dataEncrypter.EncryptData(channelName, result, key);
                result = _options.JsonSerializer.Serialize(encryptedData);
            }

            return result;
        }

        private TriggerBody CreateTriggerBody(string[] channelNames, string eventName, object data, ITriggerOptions options)
        {
            ValidationHelper.ValidateChannelNames(channelNames);
            ValidationHelper.ValidateSocketId(options.SocketId);

            string channelName = null;
            if (channelNames != null)
            {
                if (channelNames.Length > 0)
                {
                    channelName = channelNames[0];
                }
            }

            TriggerBody bodyData = new TriggerBody()
            {
                name = eventName,
                data = SerializeData(channelName, data),
                channels = channelNames
            };

            ValidationHelper.ValidateBatchEventData(bodyData.data, channelName, eventName, _options);

            if (string.IsNullOrEmpty(options.SocketId) == false)
            {
                bodyData.socket_id = options.SocketId;
            }

            if (options.Info != null && options.Info.Count > 0)
            {
                bodyData.info = string.Join(",", options.Info);
            }

            if (!string.IsNullOrEmpty(options.IdempotencyKey))
            {
                bodyData.idempotency_key = options.IdempotencyKey;
            }

            if (options.Extras != null)
            {
                bodyData.extras = options.Extras;
            }

            return bodyData;
        }

        private BatchTriggerBody CreateBatchTriggerBody(Event[] events)
        {
            ValidationHelper.ValidateBatchEvents(events);

            BatchEvent[] batchEvents = new BatchEvent[events.Length];
            int index = 0;
            foreach (Event item in events)
            {
                BatchEvent batchEvent = new BatchEvent
                {
                    name = item.EventName,
                    channel = item.Channel,
                    socket_id = item.SocketId,
                    data = SerializeData(item.Channel, item.Data),
                    idempotency_key = item.IdempotencyKey,
                    extras = item.Extras,
                };
                ValidationHelper.ValidateBatchEventData(batchEvent.data, batchEvent.channel, batchEvent.name, _options);

                batchEvents[index++] = batchEvent;
            }

            return new BatchTriggerBody()
            {
                batch = batchEvents
            };
        }

        ///<inheritDoc/>
        public IAuthenticationData Authenticate(string channelName, string socketId)
        {
            return (IAuthenticationData)AuthorizeChannel(channelName, socketId);
        }

        ///<inheritDoc/>
        public IAuthenticationData Authenticate(string channelName, string socketId, PresenceChannelData presenceData)
        {
            return (IAuthenticationData)AuthorizeChannel(channelName, socketId, presenceData);
        }


        ///<inheritDoc/>
        public IChannelAuthorizationResponse AuthorizeChannel(string channelName, string socketId)
        {
            IChannelAuthorizationResponse result;
            if (IsPrivateEncryptedChannel(channelName))
            {
                result = new ChannelAuthorizationResponse(_appKey, _appSecret, channelName, socketId, _options.EncryptionMasterKey);
            }
            else
            {
                result = new ChannelAuthorizationResponse(_appKey, _appSecret, channelName, socketId);
            }

            return result;
        }

        ///<inheritDoc/>
        public IChannelAuthorizationResponse AuthorizeChannel(string channelName, string socketId, PresenceChannelData presenceData)
        {
            if (presenceData == null)
            {
                throw new ArgumentNullException(nameof(presenceData));
            }

            return new ChannelAuthorizationResponse(_appKey, _appSecret, channelName, socketId, presenceData);
        }

        ///<inheritDoc/>
        public IUserAuthenticationResponse AuthenticateUser(string socketId, UserData userData)
        {
            if (userData == null)
            {
                throw new ArgumentNullException(nameof(userData));
            }

            return new UserAuthenticationResponse(_appKey, _appSecret, socketId, userData);
        }

        ///<inheritDoc/>
        public async Task<IGetResult<T>> GetAsync<T>(string resource, object parameters = null)
        {
            var request = _factory.Build(SockudoMethod.GET, resource, parameters);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        ///<inheritDoc/>
        public IWebHook ProcessWebHook(string signature, string body)
        {
            return new WebHook(_appSecret, signature, body);
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchUsersFromPresenceChannelAsync<T>(string channelName)
        {
            ThrowArgumentExceptionIfNullOrEmpty(channelName, "channelName");

            var request = _factory.Build(SockudoMethod.GET, string.Format(ChannelUsersResource, channelName));

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchStateForChannelAsync<T>(string channelName, object info = null)
        {
            ThrowArgumentExceptionIfNullOrEmpty(channelName, "channelName");

            var request = _factory.Build(SockudoMethod.GET, string.Format(ChannelResource, channelName), info);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchStateForChannelsAsync<T>(object info = null)
        {
            var request = _factory.Build(SockudoMethod.GET, MultipleChannelsResource, info);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchHistoryForChannelAsync<T>(string channelName, object parameters = null)
        {
            ThrowArgumentExceptionIfNullOrEmpty(channelName, "channelName");

            var request = _factory.Build(SockudoMethod.GET, string.Format(ChannelHistoryResource, channelName), parameters);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchPresenceHistoryForChannelAsync<T>(string channelName, object parameters = null)
        {
            ThrowArgumentExceptionIfNullOrEmpty(channelName, "channelName");

            var request = _factory.Build(SockudoMethod.GET, string.Format(PresenceHistoryResource, channelName), parameters);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        /// <inheritDoc/>
        public async Task<IGetResult<T>> FetchPresenceSnapshotForChannelAsync<T>(string channelName, object parameters = null)
        {
            ThrowArgumentExceptionIfNullOrEmpty(channelName, "channelName");

            var request = _factory.Build(SockudoMethod.GET, string.Format(PresenceSnapshotResource, channelName), parameters);

            var response = await _options.RestClient.ExecuteGetAsync<T>(request).ConfigureAwait(false);

            return response;
        }

        internal static bool IsPrivateEncryptedChannel(string channelName)
        {
            bool result = false;
            if (channelName != null)
            {
                if (channelName.StartsWith("private-encrypted-", StringComparison.OrdinalIgnoreCase))
                {
                    result = true;
                }
            }

            return result;
        }

        private void DebugTriggerRequest(ISockudoRestRequest request)
        {
            _options.TraceLogger?.Trace($"Method: {request.Method}{Environment.NewLine}Host: {_options.RestClient.BaseUrl}{Environment.NewLine}Resource: {request.ResourceUri}{Environment.NewLine}Body:{request.Body}");
        }

        private void DebugTriggerResponse(TriggerResult response)
        {
            _options.TraceLogger?.Trace($"Response{Environment.NewLine}StatusCode: {response.StatusCode}{Environment.NewLine}Body: {response.OriginalContent}");
        }

        private static void ThrowArgumentExceptionIfNullOrEmpty(string value, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{argumentName} cannot be null or empty");
            }
        }
    }
}
