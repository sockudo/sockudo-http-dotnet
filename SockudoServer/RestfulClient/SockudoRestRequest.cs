using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SockudoServer.RestfulClient
{
    /// <summary>
    /// A REST request to be made to the Sockudo API
    /// </summary>
    public class SockudoRestRequest : ISockudoRestRequest
    {
        /// <summary>
        /// Creates a new REST request to make back to the Sockudo server
        /// </summary>
        /// <param name="resourceUri">The URI to call</param>
        public SockudoRestRequest(string resourceUri)
        {
            if (string.IsNullOrWhiteSpace(resourceUri))
                throw new ArgumentNullException(nameof(resourceUri), "The resource URI must be a populated string");

            ResourceUri = resourceUri;
            Headers = new Dictionary<string, string>();
        }

        /// <inheritdoc/>
        public SockudoMethod Method { get; set; }

        /// <inheritdoc/>
        public string ResourceUri { get; }

        /// <inheritdoc/>
        public object Body { get; set; }

        /// <inheritdoc/>
        public IDictionary<string, string> Headers { get; }

        /// <inheritdoc/>
        public string GetContentAsJsonString()
        {
            return Body != null ? DefaultSerializer.Default.Serialize(Body) : null;
        }
    }
}
