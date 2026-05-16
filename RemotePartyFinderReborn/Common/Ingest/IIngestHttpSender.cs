using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePartyFinderReborn;

internal interface IIngestHttpSender
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
