using SockudoServer.RestfulClient;

namespace SockudoServer
{
    // Backward-compatible aliases so the historical Pusher-style test surface
    // continues to compile against the renamed Sockudo types.
    public interface IPusher : ISockudo
    {
    }

    public interface IPusherOptions : ISockudoOptions
    {
    }

    public class PusherOptions : SockudoOptions, IPusherOptions
    {
    }

    public class Pusher : Sockudo, IPusher
    {
        public Pusher(string appId, string appKey, string appSecret, IPusherOptions options = null)
            : base(appId, appKey, appSecret, options)
        {
        }
    }
}

namespace SockudoServer.RestfulClient
{
    public enum PusherMethod
    {
        GET,
        POST
    }

    public interface IPusherRestClient : ISockudoRestClient
    {
    }

    public interface IPusherRestRequest : ISockudoRestRequest
    {
        new PusherMethod Method { get; set; }
    }

    public class PusherRestClient : SockudoRestClient, IPusherRestClient
    {
        public PusherRestClient(string baseAddress, string libraryName, System.Version version)
            : base(baseAddress, libraryName, version)
        {
        }

        public PusherRestClient(System.Uri baseAddress, string libraryName, System.Version version)
            : base(baseAddress, libraryName, version)
        {
        }

        public PusherRestClient(System.Net.Http.HttpClient httpClient, string libraryName, System.Version version)
            : base(httpClient, libraryName, version)
        {
        }
    }

    public class PusherRestRequest : SockudoRestRequest, IPusherRestRequest
    {
        public PusherRestRequest(string resourceUri)
            : base(resourceUri)
        {
        }

        public new PusherMethod Method
        {
            get => base.Method == SockudoMethod.GET ? PusherMethod.GET : PusherMethod.POST;
            set => base.Method = value == PusherMethod.GET ? SockudoMethod.GET : SockudoMethod.POST;
        }
    }
}
